using SharpShare.Models;
using SharpShare.Storage;
using System.Buffers;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading.Channels;

namespace SharpShare.Network;

/// <summary>
/// Manages a single authenticated peer connection. Runs background read/write loops using Channels for thread-safe
/// message passing.
/// </summary>
public sealed class PeerSession : IDisposable
{
    private readonly TcpClient tcpClient;
    private readonly SslStream sslStream;
    private CancellationTokenSource? sessionCts;
    private Task? readLoopTask;
    private Task? writeLoopTask;
    private Task? keepAliveTask;
    private bool disposed;
    private long lastMessageReceivedTicks = DateTime.UtcNow.Ticks;

    /// <summary>
    /// How often to send a keepalive ping (seconds).
    /// </summary>
    public const int KeepAliveIntervalSeconds = 15;

    /// <summary>
    /// If no message received within this many seconds, consider peer dead.
    /// </summary>
    public const int KeepAliveTimeoutSeconds = 45;

    private readonly Channel<WriteRequest> outgoingMessages = Channel.CreateBounded<WriteRequest>(
        new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    public string RemoteAddress { get; }
    public bool IsConnected => !disposed && tcpClient.Connected;
    public int LatencyMs { get; private set; }

    // Events for incoming messages
    public event Action<FileListResponseMessage>? FileListReceived;
    public event Action<FileDownloadRequestMessage>? FileDownloadRequested;
    public event Action<FileChunkMessage>? FileChunkReceived;
    public event Action<FileTransferCompleteMessage>? FileTransferCompleted;
    public event Action<FileTransferErrorMessage>? FileTransferErrorReceived;
    public event Action<FileTransferCancelMessage>? FileTransferCancelled;
    public event Action<PingMessage>? PingReceived;
    public event Action? Disconnected;
    public event Action<string>? ErrorOccurred;
    public event Action? FileListRequested;

    public PeerSession(TcpClient tcpClient, SslStream sslStream, string remoteAddress)
    {
        this.tcpClient = tcpClient;
        this.sslStream = sslStream;
        RemoteAddress = remoteAddress;
    }

    /// <summary>
    /// Starts the background read and write loops.
    /// </summary>
    public void StartMessageLoops()
    {
        if (sessionCts != null)
        {
            throw new InvalidOperationException("Message loops already started");
        }

        sessionCts = new CancellationTokenSource();
        var token = sessionCts.Token;

        readLoopTask = Task.Run(() => ReadLoopAsync(token), token);
        writeLoopTask = Task.Run(() => WriteLoopAsync(token), token);
        keepAliveTask = Task.Run(() => KeepAliveLoopAsync(token), token);
    }

    // --- Outgoing message methods ---

    public ValueTask SendFileListRequestAsync(CancellationToken cancellationToken = default) =>
        EnqueueWriteAsync(stream => ProtocolWriter.WriteFileListRequestAsync(stream, cancellationToken), cancellationToken);

    public ValueTask SendFileListResponseAsync(FileListEntry[] files, CancellationToken cancellationToken = default) =>
        EnqueueWriteAsync(stream => ProtocolWriter.WriteFileListResponseAsync(stream, new FileListResponseMessage(files), cancellationToken), cancellationToken);

    public ValueTask SendFileDownloadRequestAsync(uint transferId, long startOffset, string relativePath, CancellationToken cancellationToken = default) =>
        EnqueueWriteAsync(stream => ProtocolWriter.WriteFileDownloadRequestAsync(stream, new FileDownloadRequestMessage(transferId, startOffset, relativePath), cancellationToken), cancellationToken);

    public ValueTask SendFileChunkAsync(uint transferId, long offset, int chunkLength, byte[] data, CancellationToken cancellationToken = default) =>
        EnqueueWriteAsync(stream => ProtocolWriter.WriteFileChunkAsync(stream, new FileChunkMessage(transferId, offset, chunkLength, data), cancellationToken), cancellationToken);

    public ValueTask SendFileTransferCompleteAsync(uint transferId, byte[] xxHash128, CancellationToken cancellationToken = default) =>
        EnqueueWriteAsync(stream => ProtocolWriter.WriteFileTransferCompleteAsync(stream, new FileTransferCompleteMessage(transferId, xxHash128), cancellationToken), cancellationToken);

    public ValueTask SendFileTransferErrorAsync(uint transferId, byte errorCode, string errorMessage, CancellationToken cancellationToken = default) =>
        EnqueueWriteAsync(stream => ProtocolWriter.WriteFileTransferErrorAsync(stream, new FileTransferErrorMessage(transferId, errorCode, errorMessage), cancellationToken), cancellationToken);

    public ValueTask SendFileTransferCancelAsync(uint transferId, CancellationToken cancellationToken = default) =>
        EnqueueWriteAsync(stream => ProtocolWriter.WriteFileTransferCancelAsync(stream, new FileTransferCancelMessage(transferId), cancellationToken), cancellationToken);

    public ValueTask SendPongAsync(long echoTimestamp, CancellationToken cancellationToken = default) =>
        EnqueueWriteAsync(stream => ProtocolWriter.WritePongAsync(stream, new PongMessage(echoTimestamp), cancellationToken), cancellationToken);

    public ValueTask SendPingAsync(CancellationToken cancellationToken = default) =>
        EnqueueWriteAsync(stream => ProtocolWriter.WritePingAsync(stream, new PingMessage(DateTime.UtcNow.Ticks), cancellationToken), cancellationToken);

    public ValueTask SendDisconnectAsync(string reason, CancellationToken cancellationToken = default) =>
        EnqueueWriteAsync(stream => ProtocolWriter.WriteDisconnectAsync(stream, new DisconnectMessage(reason), cancellationToken), cancellationToken);

    // --- Internal: Read/Write loops ---

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var header = await ProtocolReader.ReadHeaderAsync(sslStream, cancellationToken);
                if (header is null)
                {
                    RollingFileLogger.Log(LogLevel.Info, $"Peer {RemoteAddress} disconnected cleanly");
                    Disconnected?.Invoke();
                    return;
                }

                Interlocked.Exchange(ref lastMessageReceivedTicks, DateTime.UtcNow.Ticks);

                byte[] payload = await ProtocolReader.ReadPayloadAsync(sslStream, header.Value, cancellationToken);
                try
                {
                    DispatchMessage(header.Value, payload.AsSpan(0, header.Value.PayloadLength));
                }
                finally
                {
                    if (payload.Length > 0)
                    {
                        ArrayPool<byte>.Shared.Return(payload);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (ProtocolException ex)
        {
            RollingFileLogger.LogError($"Protocol error from {RemoteAddress}", ex);
            ErrorOccurred?.Invoke($"Protocol error: {ex.Message}");
            Disconnected?.Invoke();
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                RollingFileLogger.LogError($"Read loop error for {RemoteAddress}", ex);
                ErrorOccurred?.Invoke($"Connection error: {ex.Message}");
                Disconnected?.Invoke();
            }
        }
    }

    private void DispatchMessage(MessageHeader header, ReadOnlySpan<byte> payload)
    {
        switch (header.Type)
        {
            case MessageType.FileListRequest:
                FileListRequested?.Invoke();
                break;
            case MessageType.FileListResponse:
                FileListReceived?.Invoke(ProtocolReader.ParseFileListResponse(payload));
                break;
            case MessageType.FileDownloadRequest:
                FileDownloadRequested?.Invoke(ProtocolReader.ParseFileDownloadRequest(payload));
                break;
            case MessageType.FileChunk:
                FileChunkReceived?.Invoke(ProtocolReader.ParseFileChunk(payload));
                break;
            case MessageType.FileTransferComplete:
                FileTransferCompleted?.Invoke(ProtocolReader.ParseFileTransferComplete(payload));
                break;
            case MessageType.FileTransferError:
                FileTransferErrorReceived?.Invoke(ProtocolReader.ParseFileTransferError(payload));
                break;
            case MessageType.FileTransferCancel:
                FileTransferCancelled?.Invoke(ProtocolReader.ParseFileTransferCancel(payload));
                break;
            case MessageType.Ping:
                var ping = ProtocolReader.ParsePing(payload);
                PingReceived?.Invoke(ping);
                // Auto-respond with pong
                _ = SendPongAsync(ping.TimestampUtcTicks);
                break;
            case MessageType.Pong:
                var pong = ProtocolReader.ParsePong(payload);
                var roundTripTicks = DateTime.UtcNow.Ticks - pong.EchoTimestampUtcTicks;
                LatencyMs = (int)(roundTripTicks / TimeSpan.TicksPerMillisecond);
                break;
            case MessageType.Disconnect:
                var disconnect = ProtocolReader.ParseDisconnect(payload);
                RollingFileLogger.Log(LogLevel.Info, $"Peer {RemoteAddress} sent disconnect: {disconnect.Reason}");
                Disconnected?.Invoke();
                break;
            default:
                RollingFileLogger.Log(LogLevel.Warning, $"Unknown message type {(ushort)header.Type} from {RemoteAddress}");
                break;
        }
    }

    private async Task KeepAliveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(KeepAliveIntervalSeconds), cancellationToken);

                var elapsed = DateTime.UtcNow - new DateTime(Interlocked.Read(ref lastMessageReceivedTicks), DateTimeKind.Utc);
                if (elapsed.TotalSeconds > KeepAliveTimeoutSeconds)
                {
                    RollingFileLogger.Log(LogLevel.Warning, $"Peer {RemoteAddress} timed out after {elapsed.TotalSeconds:F0}s");
                    ErrorOccurred?.Invoke("Connection timed out — no response from peer");
                    Disconnected?.Invoke();
                    return;
                }

                await SendPingAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                RollingFileLogger.LogError($"Keepalive error for {RemoteAddress}", ex);
            }
        }
    }

    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var writeRequest in outgoingMessages.Reader.ReadAllAsync(cancellationToken))
            {
                await writeRequest.WriteAction(sslStream);
                await sslStream.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                RollingFileLogger.LogError($"Write loop error for {RemoteAddress}", ex);
                ErrorOccurred?.Invoke($"Send error: {ex.Message}");
            }
        }
    }

    private async ValueTask EnqueueWriteAsync(
        Func<Stream, ValueTask> writeAction, CancellationToken cancellationToken = default)
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(PeerSession));
        }

        await outgoingMessages.Writer.WriteAsync(new WriteRequest(writeAction), cancellationToken);
    }

    /// <summary>
    /// Gracefully disconnects from the peer, sending a Disconnect message.
    /// </summary>
    public async Task DisconnectAsync(string reason = "User disconnected")
    {
        if (disposed)
        {
            return;
        }

        try
        {
            await SendDisconnectAsync(reason);
            // Give time for the message to be sent
            await Task.Delay(100);
        }
        catch
        {
            // Best effort
        }

        Dispose();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        outgoingMessages.Writer.TryComplete();
        sessionCts?.Cancel();

        try
        {
            sslStream.Dispose();
        }
        catch
        {
        }
        try
        {
            tcpClient.Dispose();
        }
        catch
        {
        }

        try
        {
            if (readLoopTask != null)
            {
                readLoopTask.GetAwaiter().GetResult();
            }
        }
        catch
        {
        }
        try
        {
            if (writeLoopTask != null)
            {
                writeLoopTask.GetAwaiter().GetResult();
            }
        }
        catch
        {
        }
        try
        {
            if (keepAliveTask != null)
            {
                keepAliveTask.GetAwaiter().GetResult();
            }
        }
        catch
        {
        }

        sessionCts?.Dispose();
    }

    private readonly struct WriteRequest
    {
        public readonly Func<Stream, ValueTask> WriteAction;

        public WriteRequest(Func<Stream, ValueTask> writeAction)
        {
            WriteAction = writeAction;
        }
    }
}
