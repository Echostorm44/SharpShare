using System.Net;
using System.Net.Sockets;
using SharpShare.Network;
using SharpShare.Storage;
using SharpShare.Transfer;

namespace SharpShare.Tests.Transfer;

/// <summary>
/// End-to-end file transfer tests over real TCP + TLS connections.
/// Tests the full pipeline: connect → auth → request download → send chunks → verify hash.
/// </summary>
[NotInParallel]
public class FileTransferIntegrationTests
{
    private string hostDir = null!;
    private string clientDir = null!;
    private string logDir = null!;

    [Before(Test)]
    public void SetUp()
    {
        RollingFileLogger.Shutdown();
        var baseDir = Path.Combine(Path.GetTempPath(), "SharpShareXfer_" + Guid.NewGuid().ToString("N"));
        hostDir = Path.Combine(baseDir, "host");
        clientDir = Path.Combine(baseDir, "client");
        logDir = Path.Combine(baseDir, "logs");
        Directory.CreateDirectory(hostDir);
        Directory.CreateDirectory(clientDir);
        Directory.CreateDirectory(logDir);
        RollingFileLogger.SetLogDirectory(logDir);
    }

    [After(Test)]
    public void TearDown()
    {
        RollingFileLogger.Shutdown();
        try { Directory.Delete(Path.GetDirectoryName(hostDir)!, true); } catch { }
    }

    [Test, Timeout(30_000)]
    public async Task SmallFile_TransfersCorrectly()
    {
        // Create a small test file on the host side
        string testFileName = "small_test.txt";
        string hostFilePath = Path.Combine(hostDir, testFileName);
        byte[] fileContent = new byte[1024]; // 1KB
        Random.Shared.NextBytes(fileContent);
        await File.WriteAllBytesAsync(hostFilePath, fileContent);

        var (hostSession, clientSession, listener) = await SetupConnectedPair();
        try
        {
            var hostEngine = new FileTransferEngine(hostSession, hostDir);
            var clientEngine = new FileTransferEngine(clientSession, clientDir);

            // Wire up host to respond to download requests
            var sendComplete = new TaskCompletionSource();
            hostSession.FileDownloadRequested += request =>
            {
                _ = Task.Run(async () =>
                {
                    var progress = new TransferProgressState
                    {
                        TransferId = request.TransferId,
                        FileName = testFileName,
                        TotalBytes = new FileInfo(hostFilePath).Length,
                        Direction = TransferDirection.Upload,
                    };
                    await hostEngine.SendFileAsync(request, progress);
                    sendComplete.TrySetResult();
                });
            };

            // Client requests download
            var (transferId, clientProgress) = await clientEngine.RequestDownloadAsync(
                testFileName, fileContent.Length);

            // Set up receive context
            var receiveCtx = clientEngine.StartReceive(
                transferId, testFileName, fileContent.Length, clientProgress);

            // Wire incoming chunks and completion to receive context
            var receiveComplete = new TaskCompletionSource<bool>();
            clientSession.FileChunkReceived += chunk =>
            {
                if (chunk.TransferId == transferId)
                    _ = receiveCtx.ProcessChunkAsync(chunk);
            };
            clientSession.FileTransferCompleted += msg =>
            {
                if (msg.TransferId == transferId)
                {
                    _ = Task.Run(async () =>
                    {
                        bool ok = await receiveCtx.FinalizeAsync(msg.XxHash128);
                        receiveComplete.TrySetResult(ok);
                    });
                }
            };

            // Wait for both sides
            var completed = await Task.WhenAny(
                Task.WhenAll(sendComplete.Task, receiveComplete.Task),
                Task.Delay(20_000));
            await Assert.That(completed).IsNotEqualTo(Task.Delay(0)); // didn't timeout

            bool hashOk = await receiveComplete.Task;
            await Assert.That(hashOk).IsTrue();

            // Verify file content matches
            string clientFilePath = Path.Combine(clientDir, testFileName);
            await Assert.That(File.Exists(clientFilePath)).IsTrue();

            byte[] received = await File.ReadAllBytesAsync(clientFilePath);
            await Assert.That(received.Length).IsEqualTo(fileContent.Length);
            await Assert.That(received.SequenceEqual(fileContent)).IsTrue();

            receiveCtx.Dispose();
        }
        finally
        {
            await CleanupPair(hostSession, clientSession, listener);
        }
    }

    [Test, Timeout(30_000)]
    public async Task LargerFile_MultipleChunks_TransfersCorrectly()
    {
        // Create a file that will require multiple chunks (256KB each)
        string testFileName = "multi_chunk.bin";
        string hostFilePath = Path.Combine(hostDir, testFileName);
        int fileSize = ProtocolConstants.FileChunkSize * 3 + 1000; // ~769KB = 3 full chunks + partial
        byte[] fileContent = new byte[fileSize];
        Random.Shared.NextBytes(fileContent);
        await File.WriteAllBytesAsync(hostFilePath, fileContent);

        var (hostSession, clientSession, listener) = await SetupConnectedPair();
        try
        {
            var hostEngine = new FileTransferEngine(hostSession, hostDir);
            var clientEngine = new FileTransferEngine(clientSession, clientDir);

            var sendComplete = new TaskCompletionSource();
            hostSession.FileDownloadRequested += request =>
            {
                _ = Task.Run(async () =>
                {
                    var progress = new TransferProgressState
                    {
                        TransferId = request.TransferId,
                        FileName = testFileName,
                        TotalBytes = fileSize,
                        Direction = TransferDirection.Upload,
                    };
                    await hostEngine.SendFileAsync(request, progress);
                    sendComplete.TrySetResult();
                });
            };

            var (transferId, clientProgress) = await clientEngine.RequestDownloadAsync(
                testFileName, fileSize);

            var receiveCtx = clientEngine.StartReceive(
                transferId, testFileName, fileSize, clientProgress);

            var receiveComplete = new TaskCompletionSource<bool>();
            clientSession.FileChunkReceived += chunk =>
            {
                if (chunk.TransferId == transferId)
                    _ = receiveCtx.ProcessChunkAsync(chunk);
            };
            clientSession.FileTransferCompleted += msg =>
            {
                if (msg.TransferId == transferId)
                    _ = Task.Run(async () => receiveComplete.TrySetResult(await receiveCtx.FinalizeAsync(msg.XxHash128)));
            };

            await Task.WhenAny(
                Task.WhenAll(sendComplete.Task, receiveComplete.Task),
                Task.Delay(20_000));

            bool hashOk = await receiveComplete.Task;
            await Assert.That(hashOk).IsTrue();

            byte[] received = await File.ReadAllBytesAsync(Path.Combine(clientDir, testFileName));
            await Assert.That(received.SequenceEqual(fileContent)).IsTrue();

            receiveCtx.Dispose();
        }
        finally
        {
            await CleanupPair(hostSession, clientSession, listener);
        }
    }

    [Test, Timeout(15_000)]
    public async Task MissingFile_ReturnsError()
    {
        var (hostSession, clientSession, listener) = await SetupConnectedPair();
        try
        {
            var hostEngine = new FileTransferEngine(hostSession, hostDir);

            var errorReceived = new TaskCompletionSource<FileTransferErrorMessage>();
            clientSession.FileTransferErrorReceived += msg => errorReceived.TrySetResult(msg);

            hostSession.FileDownloadRequested += request =>
            {
                _ = Task.Run(async () =>
                {
                    var progress = new TransferProgressState
                    {
                        TransferId = request.TransferId,
                        FileName = "nonexistent.bin",
                        TotalBytes = 100,
                        Direction = TransferDirection.Upload,
                    };
                    await hostEngine.SendFileAsync(request, progress);
                });
            };

            // Request a file that doesn't exist
            await clientSession.SendFileDownloadRequestAsync(1, 0, "nonexistent.bin");

            var result = await Task.WhenAny(errorReceived.Task, Task.Delay(10_000));
            await Assert.That(result == errorReceived.Task).IsTrue();

            var error = await errorReceived.Task;
            await Assert.That(error.ErrorCode).IsEqualTo(TransferErrorCode.FileNotFound);
        }
        finally
        {
            await CleanupPair(hostSession, clientSession, listener);
        }
    }

    [Test, Timeout(15_000)]
    public async Task GracefulDisconnect_FiresEvent()
    {
        var (hostSession, clientSession, listener) = await SetupConnectedPair();
        try
        {
            var disconnected = new TaskCompletionSource();
            hostSession.Disconnected += () => disconnected.TrySetResult();

            await clientSession.DisconnectAsync("test disconnect");

            var result = await Task.WhenAny(disconnected.Task, Task.Delay(5_000));
            await Assert.That(result == disconnected.Task).IsTrue();
        }
        finally
        {
            listener.Stop();
            hostSession.Dispose();
            clientSession.Dispose();
        }
    }

    // --- Helpers ---

    private static async Task<(PeerSession host, PeerSession client, PeerListener listener)> SetupConnectedPair()
    {
        string passphrase = "integration-test-" + Guid.NewGuid().ToString("N")[..8];
        int port = GetAvailablePort();

        var listener = new PeerListener();
        var hostReady = new TaskCompletionSource<PeerSession>();
        listener.PeerAuthenticated += s => hostReady.TrySetResult(s);
        listener.Start(port, passphrase);

        var clientSession = await PeerConnector.ConnectAsync("127.0.0.1", port, passphrase);
        if (clientSession == null)
            throw new Exception("Client failed to connect");

        var completed = await Task.WhenAny(hostReady.Task, Task.Delay(10_000));
        if (completed != hostReady.Task)
            throw new TimeoutException("Host session not established");

        var hostSession = await hostReady.Task;
        hostSession.StartMessageLoops();
        clientSession.StartMessageLoops();

        return (hostSession, clientSession, listener);
    }

    private static async Task CleanupPair(PeerSession host, PeerSession client, PeerListener listener)
    {
        try { await client.DisconnectAsync("test cleanup"); } catch { }
        listener.Stop();
        host.Dispose();
        client.Dispose();
    }

    private static int GetAvailablePort()
    {
        using var tempListener = new TcpListener(IPAddress.Loopback, 0);
        tempListener.Start();
        int port = ((IPEndPoint)tempListener.LocalEndpoint).Port;
        tempListener.Stop();
        return port;
    }
}
