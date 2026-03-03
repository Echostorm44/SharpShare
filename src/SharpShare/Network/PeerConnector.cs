using SharpShare.Storage;
using System.Net;
using System.Net.Sockets;

namespace SharpShare.Network;

/// <summary>
/// TCP client for join mode. Connects to a host, performs TLS and passphrase authentication.
/// </summary>
public static class PeerConnector
{
    /// <summary>
    /// Connects to the specified host, performs TLS handshake and passphrase authentication. Returns a PeerSession on
    /// success, or null if authentication fails. Throws on network errors.
    /// </summary>
    public static async Task<PeerSession?> ConnectAsync(
        string hostAddress, int port, string passphrase, CancellationToken cancellationToken = default)
    {
        RollingFileLogger.Log(LogLevel.Info, $"Connecting to {hostAddress}:{port}...");

        var tcpClient = new TcpClient();
        try
        {
            // Configure socket for performance
            tcpClient.NoDelay = true;
            tcpClient.ReceiveBufferSize = 512 * 1024;
            tcpClient.SendBufferSize = 512 * 1024;

            // Connect with timeout
            using var connectTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeoutCts.CancelAfter(TimeSpan.FromSeconds(ProtocolConstants.ConnectionTimeoutSeconds));

            await tcpClient.ConnectAsync(hostAddress, port, connectTimeoutCts.Token);

            var remoteEndpoint = tcpClient.Client.RemoteEndPoint as IPEndPoint;
            RollingFileLogger.Log(LogLevel.Info, $"TCP connected to {remoteEndpoint}");

            var networkStream = tcpClient.GetStream();

            // TLS handshake
            var sslStream = await TlsCertificateProvider.AuthenticateAsClientAsync(
                networkStream, connectTimeoutCts.Token);

            // Passphrase authentication with auth timeout
            using var authTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            authTimeoutCts.CancelAfter(TimeSpan.FromSeconds(ProtocolConstants.AuthTimeoutSeconds));

            bool authSuccess = await Authenticator.AuthenticateAsClientAsync(
                sslStream, passphrase, authTimeoutCts.Token);

            if (!authSuccess)
            {
                RollingFileLogger.Log(LogLevel.Warning, "Authentication failed - wrong passphrase or host rejected connection");
                await sslStream.DisposeAsync();
                tcpClient.Dispose();
                return null;
            }

            string remoteAddress = remoteEndpoint?.Address.ToString() ?? hostAddress;
            var session = new PeerSession(tcpClient, sslStream, remoteAddress);
            RollingFileLogger.Log(LogLevel.Info, $"Connected and authenticated with {remoteAddress}");
            return session;
        }
        catch
        {
            tcpClient.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Parses an address string like "192.168.1.10:9500" or "73.162.45.12" into host + port. Uses the default port if
    /// none is specified.
    /// </summary>
    public static (string Host, int Port) ParseAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new ArgumentException("Address cannot be empty");
        }

        address = address.Trim();

        int colonIndex = address.LastIndexOf(':');
        if (colonIndex > 0 && int.TryParse(address.AsSpan(colonIndex + 1), out int port))
        {
            string host = address[..colonIndex];
            return (host, port);
        }

        return (address, ProtocolConstants.DefaultPort);
    }
}
