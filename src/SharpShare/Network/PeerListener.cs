using SharpShare.Storage;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace SharpShare.Network;

/// <summary>
/// TCP listener for host mode. Accepts one connection at a time, applies TLS and authentication. Protected by
/// ConnectionGuard against brute force and flooding.
/// </summary>
public sealed class PeerListener : IDisposable
{
    private TcpListener? tcpListener;
    private readonly ConnectionGuard connectionGuard = new();
    private X509Certificate2? tlsCertificate;
    private CancellationTokenSource? listenerCts;
    private Task? listenTask;
    private bool disposed;

    public event Action<PeerSession>? PeerAuthenticated;
    public event Action<string>? ListenerError;
    public event Action? ListenerStarted;

    public int ListeningPort { get; private set; }
    public bool IsListening => tcpListener != null && listenTask != null && !listenTask.IsCompleted;

    /// <summary>
    /// Starts listening for incoming connections on the specified port.
    /// </summary>
    public void Start(int port, string passphrase)
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(PeerListener));
        }

        if (IsListening)
        {
            throw new InvalidOperationException("Listener is already running");
        }

        ListeningPort = port;
        tlsCertificate = TlsCertificateProvider.GenerateEphemeralCertificate();
        listenerCts = new CancellationTokenSource();

        tcpListener = new TcpListener(IPAddress.Any, port);
        tcpListener.Start(backlog: 2);

        RollingFileLogger.Log(LogLevel.Info, $"Listening on port {port}");
        ListenerStarted?.Invoke();

        var cancellationToken = listenerCts.Token;
        listenTask = Task.Run(() => AcceptLoopAsync(passphrase, cancellationToken), cancellationToken);
    }

    private async Task AcceptLoopAsync(string passphrase, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? tcpClient = null;
            try
            {
                tcpClient = await tcpListener!.AcceptTcpClientAsync(cancellationToken);
                var remoteEndpoint = tcpClient.Client.RemoteEndPoint as IPEndPoint;
                var remoteIp = remoteEndpoint?.Address ?? IPAddress.None;

                RollingFileLogger.Log(LogLevel.Info, $"Incoming connection from {remoteEndpoint}");

                // ConnectionGuard checks
                if (connectionGuard.ShouldRejectConnection(remoteIp))
                {
                    tcpClient.Dispose();
                    continue;
                }

                connectionGuard.RecordPendingConnection();
                _ = HandleConnectionAsync(tcpClient, remoteIp, passphrase, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                tcpClient?.Dispose();
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                tcpClient?.Dispose();
                RollingFileLogger.LogError("Accept loop error", ex);
            }
        }
    }

    private async Task HandleConnectionAsync(
        TcpClient tcpClient, IPAddress remoteIp, string passphrase, CancellationToken cancellationToken)
    {
        try
        {
            // Apply delay if this IP has prior failures
            var delay = connectionGuard.GetRequiredDelay(remoteIp);
            if (delay > TimeSpan.Zero)
            {
                RollingFileLogger.Log(LogLevel.Info, $"Applying {delay.TotalSeconds:F0}s delay for {remoteIp}");
                await Task.Delay(delay, cancellationToken);
            }

            // Configure socket for performance
            tcpClient.NoDelay = true;
            tcpClient.ReceiveBufferSize = 512 * 1024;
            tcpClient.SendBufferSize = 512 * 1024;

            var networkStream = tcpClient.GetStream();

            // TLS handshake with auth timeout
            using var authTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            authTimeoutCts.CancelAfter(TimeSpan.FromSeconds(ProtocolConstants.AuthTimeoutSeconds));

            var sslStream = await TlsCertificateProvider.AuthenticateAsServerAsync(
                networkStream, tlsCertificate!, authTimeoutCts.Token);

            // Passphrase authentication
            bool authSuccess = await Authenticator.AuthenticateAsHostAsync(
                sslStream, passphrase, authTimeoutCts.Token);

            if (!authSuccess)
            {
                connectionGuard.RecordFailure(remoteIp);
                connectionGuard.ReleasePendingConnection();
                await sslStream.DisposeAsync();
                tcpClient.Dispose();
                return;
            }

            connectionGuard.RecordSuccess(remoteIp);
            connectionGuard.ReleasePendingConnection();

            // Use IPv4 string when address is IPv4-mapped IPv6 (e.g., ::ffff:1.2.3.4 → 1.2.3.4)
            string remoteAddr = remoteIp.IsIPv4MappedToIPv6
                ? remoteIp.MapToIPv4().ToString()
                : remoteIp.ToString();
            var session = new PeerSession(tcpClient, sslStream, remoteAddr);
            RollingFileLogger.Log(LogLevel.Info, $"Peer authenticated: {remoteAddr}");
            PeerAuthenticated?.Invoke(session);
        }
        catch (OperationCanceledException)
        {
            connectionGuard.ReleasePendingConnection();
            tcpClient.Dispose();
        }
        catch (Exception ex)
        {
            connectionGuard.ReleasePendingConnection();
            tcpClient.Dispose();
            RollingFileLogger.LogError($"Connection from {remoteIp} failed", ex);
            ListenerError?.Invoke($"Connection failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Stops the listener and releases resources.
    /// </summary>
    public void Stop()
    {
        listenerCts?.Cancel();
        tcpListener?.Stop();

        try
        {
            listenTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RollingFileLogger.LogError("Error stopping listener", ex);
        }

        tcpListener = null;
        listenTask = null;
        listenerCts?.Dispose();
        listenerCts = null;
        tlsCertificate?.Dispose();
        tlsCertificate = null;

        RollingFileLogger.Log(LogLevel.Info, "Listener stopped");
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Stop();
    }
}
