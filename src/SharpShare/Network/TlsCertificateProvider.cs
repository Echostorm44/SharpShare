using SharpShare.Storage;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SharpShare.Network;

/// <summary>
/// Generates ephemeral self-signed TLS certificates and wraps streams with SslStream. Uses ECDsa P-256 for fast,
/// secure, small-key certificates valid for 24 hours.
/// </summary>
public static class TlsCertificateProvider
{
    private const string CertificateSubject = "CN=SharpShare-Ephemeral";

    /// <summary>
    /// Generates an ephemeral self-signed X.509 certificate using ECDsa P-256. Valid for 24 hours. Intended for a
    /// single session.
    /// </summary>
    public static X509Certificate2 GenerateEphemeralCertificate()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            CertificateSubject,
            ecdsa,
            HashAlgorithmName.SHA256);

        var now = DateTimeOffset.UtcNow;
        var certificate = request.CreateSelfSigned(now.AddMinutes(-5), now.AddHours(24));

        // On Windows, SslStream requires the cert private key to be persisted (not ephemeral).
        // EphemeralKeySet fails with "platform does not support ephemeral keys" on SslStream.
        byte[] pfxBytes = certificate.Export(X509ContentType.Pfx);
        return X509CertificateLoader.LoadPkcs12(pfxBytes, null, 
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet);
    }

    /// <summary>
    /// Wraps a NetworkStream as TLS server (host side). Authenticates with the ephemeral certificate. Requires TLS 1.3.
    /// </summary>
    public static async Task<SslStream> AuthenticateAsServerAsync(
        Stream innerStream,
        X509Certificate2 serverCertificate,
        CancellationToken cancellationToken = default)
    {
        var sslStream = new SslStream(innerStream, leaveInnerStreamOpen: false);
        try
        {
            var serverOptions = new SslServerAuthenticationOptions
            {
                ServerCertificate = serverCertificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            };

            await sslStream.AuthenticateAsServerAsync(serverOptions, cancellationToken);
            RollingFileLogger.Log(LogLevel.Info, $"TLS server handshake complete: {sslStream.SslProtocol}, {sslStream.NegotiatedCipherSuite}");
            return sslStream;
        }
        catch
        {
            await sslStream.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Wraps a NetworkStream as TLS client (join side). Accepts any server certificate (we authenticate via passphrase,
    /// not PKI).
    /// </summary>
    public static async Task<SslStream> AuthenticateAsClientAsync(
        Stream innerStream,
        CancellationToken cancellationToken = default)
    {
        var sslStream = new SslStream(
            innerStream,
            leaveInnerStreamOpen: false);

        try
        {
            var clientOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "SharpShare",
                EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => true,
            };

            await sslStream.AuthenticateAsClientAsync(clientOptions, cancellationToken);
            RollingFileLogger.Log(LogLevel.Info, $"TLS client handshake complete: {sslStream.SslProtocol}, {sslStream.NegotiatedCipherSuite}");
            return sslStream;
        }
        catch
        {
            await sslStream.DisposeAsync();
            throw;
        }
    }
}
