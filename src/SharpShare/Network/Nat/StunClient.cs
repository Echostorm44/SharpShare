using SharpShare.Storage;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace SharpShare.Network.Nat;

/// <summary>
/// Minimal STUN client (RFC 5389) for discovering the public IP address. Sends a Binding Request to a STUN server and
/// parses the XOR-MAPPED-ADDRESS from the Binding Response.
/// </summary>
public static class StunClient
{
    private const int StunHeaderSize = 20;
    private const ushort BindingRequest = 0x0001;
    private const ushort BindingResponse = 0x0101;
    private const uint MagicCookie = 0x2112A442;
    private const ushort XorMappedAddressType = 0x0020;
    private const ushort MappedAddressType = 0x0001;

    private static readonly string[] StunServers =[ "stun.l.google.com", "stun1.l.google.com", "stun2.l.google.com", "stun.cloudflare.com", ];

    private const int StunPort = 19302;
    private const int CloudflareStunPort = 3478;

    /// <summary>
    /// Discovers the public IP address using STUN. Tries multiple servers with a timeout. Returns null if all servers
    /// fail.
    /// </summary>
    public static async Task<IPAddress?> DiscoverPublicIpAsync(
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(5);

        foreach (string server in StunServers)
        {
            try
            {
                int port = server.Contains("cloudflare") ? CloudflareStunPort : StunPort;
                var result = await QueryStunServerAsync(server, port, effectiveTimeout, cancellationToken);
                if (result != null)
                {
                    RollingFileLogger.Log(LogLevel.Info, $"STUN: Public IP is {result} (via {server})");
                    return result;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RollingFileLogger.Log(LogLevel.Warning, $"STUN: Failed to query {server}: {ex.Message}");
            }
        }

        RollingFileLogger.Log(LogLevel.Warning, "STUN: All servers failed to discover public IP");
        return null;
    }

    internal static async Task<IPAddress?> QueryStunServerAsync(
        string server, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        using var udpClient = new UdpClient();
        udpClient.Client.ReceiveTimeout = (int)timeout.TotalMilliseconds;

        // Build Binding Request (20 bytes)
        byte[] transactionId = new byte[12];
        RandomNumberGenerator.Fill(transactionId);

        byte[] request = new byte[StunHeaderSize];
        // Message Type: Binding Request (0x0001)
        request[0] = (byte)(BindingRequest >> 8);
        request[1] = (byte)(BindingRequest & 0xFF);
        // Message Length: 0 (no attributes)
        request[2] = 0;
        request[3] = 0;
        // Magic Cookie: 0x2112A442
        request[4] = unchecked((byte)(MagicCookie >> 24));
        request[5] = unchecked((byte)(MagicCookie >> 16));
        request[6] = unchecked((byte)(MagicCookie >> 8));
        request[7] = unchecked((byte)(MagicCookie & 0xFF));
        // Transaction ID (12 bytes)
        Array.Copy(transactionId, 0, request, 8, 12);

        var endpoint = new IPEndPoint(
            (await Dns.GetHostAddressesAsync(server, cancellationToken))[0], port);

        await udpClient.SendAsync(request, endpoint, cts.Token);

        // Wait for response
        var receiveResult = await udpClient.ReceiveAsync(cts.Token);
        byte[] response = receiveResult.Buffer;

        return ParseBindingResponse(response, transactionId);
    }

    internal static IPAddress? ParseBindingResponse(byte[] response, byte[] expectedTransactionId)
    {
        if (response.Length < StunHeaderSize)
        {
            return null;
        }

        // Verify it's a Binding Response
        ushort messageType = (ushort)((response[0] << 8) | response[1]);
        if (messageType != BindingResponse)
        {
            return null;
        }

        // Verify magic cookie
        uint cookie = (uint)((response[4] << 24) | (response[5] << 16) | (response[6] << 8) | response[7]);
        if (cookie != MagicCookie)
        {
            return null;
        }

        // Verify transaction ID
        for (int i = 0;i < 12;i++)
        {
            if (response[8 + i] != expectedTransactionId[i])
            {
                return null;
            }
        }

        ushort messageLength = (ushort)((response[2] << 8) | response[3]);
        int offset = StunHeaderSize;
        int end = StunHeaderSize + messageLength;
        if (end > response.Length)
        {
            return null;
        }

        // Parse attributes looking for XOR-MAPPED-ADDRESS or MAPPED-ADDRESS
        while (offset + 4 <= end)
        {
            ushort attrType = (ushort)((response[offset] << 8) | response[offset + 1]);
            ushort attrLength = (ushort)((response[offset + 2] << 8) | response[offset + 3]);
            offset += 4;

            if (offset + attrLength > end)
            {
                break;
            }

            if (attrType == XorMappedAddressType)
            {
                var ip = ParseXorMappedAddress(response, offset, attrLength);
                if (ip != null)
                {
                    return ip;
                }
            }
            else if (attrType == MappedAddressType)
            {
                var ip = ParseMappedAddress(response, offset, attrLength);
                if (ip != null)
                {
                    return ip;
                }
            }

            // Attributes are padded to 4-byte boundaries
            offset += attrLength;
            int padding = (4 - (attrLength % 4)) % 4;
            offset += padding;
        }

        return null;
    }

    private static IPAddress? ParseXorMappedAddress(byte[] data, int offset, int length)
    {
        if (length < 8)
        {
            return null;
        }

        byte family = data[offset + 1]; // 0x01 = IPv4, 0x02 = IPv6
        // Port is XOR'd with top 16 bits of magic cookie (skip for IP discovery)

        if (family == 0x01 && length >= 8) // IPv4
        {
            // IP is XOR'd with magic cookie
            byte[] ipBytes = new byte[4];
            ipBytes[0] = (byte)(data[offset + 4] ^ unchecked((byte)(MagicCookie >> 24)));
            ipBytes[1] = (byte)(data[offset + 5] ^ unchecked((byte)(MagicCookie >> 16)));
            ipBytes[2] = (byte)(data[offset + 6] ^ unchecked((byte)(MagicCookie >> 8)));
            ipBytes[3] = (byte)(data[offset + 7] ^ unchecked((byte)(MagicCookie & 0xFF)));
            return new IPAddress(ipBytes);
        }
        else if (family == 0x02 && length >= 20) // IPv6
        {
            // IP XOR'd with magic cookie + transaction ID (not common, handle for correctness)
            byte[] ipBytes = new byte[16];
            // First 4 bytes XOR with magic cookie
            ipBytes[0] = (byte)(data[offset + 4] ^ unchecked((byte)(MagicCookie >> 24)));
            ipBytes[1] = (byte)(data[offset + 5] ^ unchecked((byte)(MagicCookie >> 16)));
            ipBytes[2] = (byte)(data[offset + 6] ^ unchecked((byte)(MagicCookie >> 8)));
            ipBytes[3] = (byte)(data[offset + 7] ^ unchecked((byte)(MagicCookie & 0xFF)));
            // Remaining 12 bytes XOR with transaction ID (bytes 8-19 of the header)
            // We'd need the transaction ID here — but we don't pass it. For now, skip IPv6.
            return null;
        }

        return null;
    }

    private static IPAddress? ParseMappedAddress(byte[] data, int offset, int length)
    {
        if (length < 8)
        {
            return null;
        }

        byte family = data[offset + 1];
        if (family == 0x01 && length >= 8) // IPv4
        {
            byte[] ipBytes = new byte[4];
            Array.Copy(data, offset + 4, ipBytes, 0, 4);
            return new IPAddress(ipBytes);
        }

        return null;
    }
}
