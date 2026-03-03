using SharpShare.Storage;
using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SharpShare.Network.Nat;

/// <summary>
/// Lightweight NAT-PMP (RFC 6886) client for automatic port forwarding. Sends requests to the default gateway on UDP
/// port 5351. Fallback when UPnP SSDP discovery fails.
/// </summary>
public static class NatPmpClient
{
    private const int NatPmpPort = 5351;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Queries the gateway for its external (public) IP address via NAT-PMP. Returns null if NAT-PMP is not supported
    /// or the request fails.
    /// </summary>
    public static async Task<IPAddress?> GetExternalAddressAsync(
        IPAddress gateway, CancellationToken cancellationToken = default)
    {
        try
        {
            using var udp = new UdpClient();
            var endpoint = new IPEndPoint(gateway, NatPmpPort);

            // NAT-PMP external address request: version=0, opcode=0
            byte[] request = [ 0, 0 ];
            await udp.SendAsync(request, endpoint, cancellationToken);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(RequestTimeout);

            var result = await udp.ReceiveAsync(cts.Token);
            // Response: version(1), opcode(1)=128, resultcode(2), epoch(4), ip(4)
            if (result.Buffer.Length >= 12)
            {
                ushort resultCode = BinaryPrimitives.ReadUInt16BigEndian(result.Buffer.AsSpan(2));
                if (resultCode == 0)
                {
                    var ip = new IPAddress(result.Buffer.AsSpan(8, 4));
                    RollingFileLogger.Log(LogLevel.Info, $"NAT-PMP: External IP is {ip}");
                    return ip;
                }
                RollingFileLogger.Log(LogLevel.Info, $"NAT-PMP: External address request returned error {resultCode}");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException ex)
        {
            RollingFileLogger.Log(LogLevel.Debug, $"NAT-PMP: Gateway did not respond ({ex.SocketErrorCode})");
        }
        catch (Exception ex)
        {
            RollingFileLogger.Log(LogLevel.Debug, $"NAT-PMP: GetExternalAddress failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Creates a TCP port mapping on the gateway via NAT-PMP. Returns the actual mapped external port, or 0 on failure.
    /// </summary>
    public static async Task<int> MapPortAsync(
        IPAddress gateway, int internalPort, int externalPort,
        int lifetimeSeconds = 3600, CancellationToken cancellationToken = default)
    {
        try
        {
            using var udp = new UdpClient();
            var endpoint = new IPEndPoint(gateway, NatPmpPort);

            // TCP mapping: version=0, opcode=2, reserved(2), internal(2), external(2), lifetime(4)
            byte[] request = new byte[12];
            request[1] = 2; // opcode 2 = TCP
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), (ushort)internalPort);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(6), (ushort)externalPort);
            BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(8), (uint)lifetimeSeconds);

            await udp.SendAsync(request, endpoint, cancellationToken);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(RequestTimeout);

            var result = await udp.ReceiveAsync(cts.Token);
            // Response: version(1), opcode(1)=130, resultcode(2), epoch(4), internal(2), external(2), lifetime(4)
            if (result.Buffer.Length >= 16)
            {
                ushort resultCode = BinaryPrimitives.ReadUInt16BigEndian(result.Buffer.AsSpan(2));
                if (resultCode == 0)
                {
                    int mappedPort = BinaryPrimitives.ReadUInt16BigEndian(result.Buffer.AsSpan(12));
                    RollingFileLogger.Log(LogLevel.Info,
                        $"NAT-PMP: Mapped port {internalPort} → external {mappedPort}");
                    return mappedPort;
                }
                RollingFileLogger.Log(LogLevel.Info, $"NAT-PMP: Port mapping returned error {resultCode}");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException ex)
        {
            RollingFileLogger.Log(LogLevel.Debug, $"NAT-PMP: Gateway did not respond ({ex.SocketErrorCode})");
        }
        catch (Exception ex)
        {
            RollingFileLogger.Log(LogLevel.Debug, $"NAT-PMP: MapPort failed: {ex.Message}");
        }

        return 0;
    }

    /// <summary>
    /// Removes a TCP port mapping by setting lifetime to 0.
    /// </summary>
    public static async Task RemovePortMappingAsync(
        IPAddress gateway, int internalPort, CancellationToken cancellationToken = default)
    {
        try
        {
            using var udp = new UdpClient();
            var endpoint = new IPEndPoint(gateway, NatPmpPort);

            byte[] request = new byte[12];
            request[1] = 2; // TCP
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), (ushort)internalPort);
            // external port = 0, lifetime = 0 → delete mapping

            await udp.SendAsync(request, endpoint, cancellationToken);
            // Fire and forget — don't wait for response
        }
        catch (Exception ex)
        {
            RollingFileLogger.Log(LogLevel.Debug, $"NAT-PMP: RemovePortMapping failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Finds the default gateway IP for the primary network interface.
    /// </summary>
    public static IPAddress? GetDefaultGateway()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                          && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(ni => ni.GetIPProperties().GatewayAddresses)
                .Select(ga => ga.Address)
                .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork
                                   && !ip.Equals(IPAddress.Any));
        }
        catch
        {
            return null;
        }
    }
}
