using System.Net;
using System.Net.Sockets;
using SharpShare.Network;
using SharpShare.Storage;

namespace SharpShare.Tests.Network;

/// <summary>
/// Tests TLS certificate generation and handshake in isolation.
/// </summary>
public class TlsCertificateTests
{
    [Test]
    public async Task GenerateEphemeralCertificate_CreatesCert()
    {
        using var cert = TlsCertificateProvider.GenerateEphemeralCertificate();
        await Assert.That(cert).IsNotNull();
        await Assert.That(cert.HasPrivateKey).IsTrue();
    }

    [Test]
    public async Task TlsHandshake_OverLoopback_Succeeds()
    {
        using var cert = TlsCertificateProvider.GenerateEphemeralCertificate();

        using var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        tcpListener.Start();
        int port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;

        Exception? serverError = null;

        var serverTask = Task.Run(async () =>
        {
            try
            {
                using var serverClient = await tcpListener.AcceptTcpClientAsync();
                var serverStream = serverClient.GetStream();
                using var serverSsl = await TlsCertificateProvider.AuthenticateAsServerAsync(serverStream, cert);

                // Write something to prove the connection works
                byte[] msg = System.Text.Encoding.UTF8.GetBytes("hello");
                await serverSsl.WriteAsync(msg);
                await serverSsl.FlushAsync();
            }
            catch (Exception ex)
            {
                serverError = ex;
                throw;
            }
        });

        var clientTask = Task.Run(async () =>
        {
            using var clientTcp = new TcpClient();
            await clientTcp.ConnectAsync(IPAddress.Loopback, port);
            var clientStream = clientTcp.GetStream();
            using var clientSsl = await TlsCertificateProvider.AuthenticateAsClientAsync(clientStream);

            // Read the server message
            byte[] buf = new byte[5];
            int n = await clientSsl.ReadAsync(buf);
            return System.Text.Encoding.UTF8.GetString(buf, 0, n);
        });

        await Task.WhenAll(serverTask, clientTask);
        tcpListener.Stop();

        await Assert.That(serverError).IsNull();
        await Assert.That(clientTask.Result).IsEqualTo("hello");
    }
}
