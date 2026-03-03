using System.Net;
using System.Net.Sockets;
using SharpShare.Network;
using SharpShare.Storage;

namespace SharpShare.Tests.Network;

/// <summary>
/// Integration test: two peers connect over localhost TCP, authenticate with TLS + passphrase,
/// and exchange file lists through the full protocol stack.
/// </summary>
[NotInParallel]
public class PeerIntegrationTests
{
    private string tempDir = null!;

    [Before(Test)]
    public void SetUp()
    {
        RollingFileLogger.Shutdown();
        tempDir = Path.Combine(Path.GetTempPath(), "SharpShareIntegration_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        RollingFileLogger.SetLogDirectory(tempDir);
    }

    [After(Test)]
    public void TearDown()
    {
        RollingFileLogger.Shutdown();
        try { Directory.Delete(tempDir, true); } catch { }
    }

    [Test]
    public async Task FullConnection_AuthenticatesAndExchangesFileList()
    {
        string passphrase = "test-integration-pass";
        int port = GetAvailablePort();

        // Set up host listener
        using var listener = new PeerListener();
        PeerSession? hostSession = null;
        var hostSessionReady = new TaskCompletionSource<PeerSession>();

        listener.PeerAuthenticated += session =>
        {
            hostSession = session;
            hostSessionReady.TrySetResult(session);
        };

        listener.Start(port, passphrase);

        // Connect client
        var clientSession = await PeerConnector.ConnectAsync("127.0.0.1", port, passphrase);
        await Assert.That(clientSession).IsNotNull();

        // Wait for host to see the authenticated session
        var completedTask = await Task.WhenAny(hostSessionReady.Task, Task.Delay(10_000));
        await Assert.That(completedTask == hostSessionReady.Task).IsTrue();

        await Assert.That(hostSession).IsNotNull();

        // Start message loops
        hostSession!.StartMessageLoops();
        clientSession!.StartMessageLoops();

        // Client requests file list from host
        var fileListReceived = new TaskCompletionSource<FileListResponseMessage>();
        clientSession.FileListReceived += msg => fileListReceived.TrySetResult(msg);

        // Host responds to file list requests
        hostSession.FileListRequested += () =>
        {
            var files = new FileListEntry[]
            {
                new(1024 * 1024 * 500, DateTime.UtcNow.Ticks, "Movie_2026_01.mkv"),
                new(1024 * 1024 * 200, DateTime.UtcNow.Ticks, "Document.pdf"),
            };
            _ = hostSession.SendFileListResponseAsync(files);
        };

        await clientSession.SendFileListRequestAsync();

        // Wait for file list response
        var listTask = await Task.WhenAny(fileListReceived.Task, Task.Delay(5_000));
        await Assert.That(listTask == fileListReceived.Task).IsTrue();

        var fileList = await fileListReceived.Task;
        await Assert.That(fileList.Files.Length).IsEqualTo(2);
        await Assert.That(fileList.Files[0].RelativePath).IsEqualTo("Movie_2026_01.mkv");
        await Assert.That(fileList.Files[1].RelativePath).IsEqualTo("Document.pdf");

        // Clean disconnect
        await clientSession.DisconnectAsync("Test complete");
        listener.Stop();
    }

    [Test]
    public async Task WrongPassphrase_ConnectionRejected()
    {
        string hostPassphrase = "correct-passphrase";
        string clientPassphrase = "wrong-passphrase";
        int port = GetAvailablePort();

        using var listener = new PeerListener();
        listener.Start(port, hostPassphrase);

        var clientSession = await PeerConnector.ConnectAsync("127.0.0.1", port, clientPassphrase);
        await Assert.That(clientSession).IsNull();

        listener.Stop();
    }

    [Test]
    public async Task PingPong_Responded()
    {
        string passphrase = "ping-pong-test";
        int port = GetAvailablePort();

        using var listener = new PeerListener();
        PeerSession? hostSession = null;
        var hostReady = new TaskCompletionSource<PeerSession>();
        listener.PeerAuthenticated += s => { hostSession = s; hostReady.TrySetResult(s); };
        listener.Start(port, passphrase);

        var clientSession = await PeerConnector.ConnectAsync("127.0.0.1", port, passphrase);
        await Assert.That(clientSession).IsNotNull();

        await Task.WhenAny(hostReady.Task, Task.Delay(10_000));
        hostSession!.StartMessageLoops();
        clientSession!.StartMessageLoops();

        // Host sends ping, PeerSession auto-responds with pong
        // We just verify no errors occur
        var pingReceived = new TaskCompletionSource<PingMessage>();
        hostSession.PingReceived += p => pingReceived.TrySetResult(p);

        // Client sends ping to host
        await clientSession.SendPingAsync();

        var result = await Task.WhenAny(pingReceived.Task, Task.Delay(5_000));
        await Assert.That(result == pingReceived.Task).IsTrue();

        await clientSession.DisconnectAsync("done");
        listener.Stop();
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
