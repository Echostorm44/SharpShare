using SharpShare.Network;
using SharpShare.Storage;

namespace SharpShare.Tests.Network;

public class AuthenticatorTests
{
    [Test]
    public async Task DeriveKey_SamePassphrase_ProducesSameKey()
    {
        byte[] key1 = Authenticator.DeriveKey("test-passphrase-123");
        byte[] key2 = Authenticator.DeriveKey("test-passphrase-123");

        await Assert.That(key1).IsEquivalentTo(key2);
    }

    [Test]
    public async Task DeriveKey_DifferentPassphrases_ProduceDifferentKeys()
    {
        byte[] key1 = Authenticator.DeriveKey("passphrase-one");
        byte[] key2 = Authenticator.DeriveKey("passphrase-two");

        await Assert.That(key1).IsNotEquivalentTo(key2);
    }

    [Test]
    public async Task DeriveKey_ReturnsExpectedLength()
    {
        byte[] key = Authenticator.DeriveKey("any-passphrase");
        await Assert.That(key.Length).IsEqualTo(32);
    }

    [Test]
    public async Task ComputeHmac_SameInputs_ProduceSameResult()
    {
        byte[] key = Authenticator.DeriveKey("test");
        byte[] nonce = new byte[32];
        Random.Shared.NextBytes(nonce);

        byte[] hmac1 = Authenticator.ComputeHmac(key, nonce);
        byte[] hmac2 = Authenticator.ComputeHmac(key, nonce);

        await Assert.That(hmac1).IsEquivalentTo(hmac2);
    }

    [Test]
    public async Task ComputeHmac_DifferentNonces_ProduceDifferentResults()
    {
        byte[] key = Authenticator.DeriveKey("test");
        byte[] nonce1 = new byte[32]; nonce1[0] = 1;
        byte[] nonce2 = new byte[32]; nonce2[0] = 2;

        byte[] hmac1 = Authenticator.ComputeHmac(key, nonce1);
        byte[] hmac2 = Authenticator.ComputeHmac(key, nonce2);

        await Assert.That(hmac1).IsNotEquivalentTo(hmac2);
    }

    [Test]
    public async Task VerifyHmac_MatchingArrays_ReturnsTrue()
    {
        byte[] key = Authenticator.DeriveKey("test");
        byte[] nonce = Authenticator.GenerateNonce();
        byte[] hmac = Authenticator.ComputeHmac(key, nonce);
        byte[] hmacCopy = (byte[])hmac.Clone();

        await Assert.That(Authenticator.VerifyHmac(hmac, hmacCopy)).IsTrue();
    }

    [Test]
    public async Task VerifyHmac_DifferentArrays_ReturnsFalse()
    {
        byte[] a = new byte[32]; a[0] = 1;
        byte[] b = new byte[32]; b[0] = 2;

        await Assert.That(Authenticator.VerifyHmac(a, b)).IsFalse();
    }

    [Test]
    public async Task GenerateNonce_Returns32Bytes()
    {
        byte[] nonce = Authenticator.GenerateNonce();
        await Assert.That(nonce.Length).IsEqualTo(32);
    }

    [Test]
    public async Task GenerateNonce_ProducesUniqueValues()
    {
        byte[] nonce1 = Authenticator.GenerateNonce();
        byte[] nonce2 = Authenticator.GenerateNonce();

        await Assert.That(nonce1).IsNotEquivalentTo(nonce2);
    }

    [Test]
    public async Task GeneratePassphrase_HasThreeWordsSeparatedByDashes()
    {
        string passphrase = Authenticator.GeneratePassphrase();
        string[] parts = passphrase.Split('-');

        await Assert.That(parts.Length).IsEqualTo(3);
        await Assert.That(parts[0].Length).IsGreaterThan(0);
        await Assert.That(parts[1].Length).IsGreaterThan(0);
        await Assert.That(parts[2].Length).IsGreaterThan(0);
    }

    [Test]
    public async Task GeneratePassphrase_ProducesDifferentValues()
    {
        string p1 = Authenticator.GeneratePassphrase();
        string p2 = Authenticator.GeneratePassphrase();

        // With ~800^3 combinations, collision is extremely unlikely
        await Assert.That(p1).IsNotEqualTo(p2);
    }

    [Test]
    public async Task WordListSize_IsReasonable()
    {
        await Assert.That(Authenticator.WordListSize).IsGreaterThanOrEqualTo(500);
        await Assert.That(Authenticator.WordListSize).IsLessThanOrEqualTo(1500);
    }

    // --- Mutual auth over a stream pair ---

    [Test]
    public async Task MutualAuth_SamePassphrase_BothSucceed()
    {
        string passphrase = "tiger-ocean-seven";

        // Use anonymous pipes for full-duplex communication
        using var hostToClientPipe = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out);
        using var clientReadsFromHost = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, hostToClientPipe.ClientSafePipeHandle);
        using var clientToHostPipe = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out);
        using var hostReadsFromClient = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, clientToHostPipe.ClientSafePipeHandle);

        var hostStream = new DuplexPipeStream(hostReadsFromClient, hostToClientPipe);
        var clientStream = new DuplexPipeStream(clientReadsFromHost, clientToHostPipe);

        var hostTask = Task.Run(() => Authenticator.AuthenticateAsHostAsync(hostStream, passphrase));
        var clientTask = Task.Run(() => Authenticator.AuthenticateAsClientAsync(clientStream, passphrase));

        bool[] results = await Task.WhenAll(hostTask, clientTask);

        await Assert.That(results[0]).IsTrue();
        await Assert.That(results[1]).IsTrue();
    }

    [Test]
    public async Task MutualAuth_DifferentPassphrases_BothFail()
    {
        using var hostToClientPipe = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out);
        using var clientReadsFromHost = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, hostToClientPipe.ClientSafePipeHandle);
        using var clientToHostPipe = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out);
        using var hostReadsFromClient = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, clientToHostPipe.ClientSafePipeHandle);

        var hostStream = new DuplexPipeStream(hostReadsFromClient, hostToClientPipe);
        var clientStream = new DuplexPipeStream(clientReadsFromHost, clientToHostPipe);

        var hostTask = Task.Run(() => Authenticator.AuthenticateAsHostAsync(hostStream, "correct-pass-phrase"));
        var clientTask = Task.Run(() => Authenticator.AuthenticateAsClientAsync(clientStream, "wrong-pass-phrase"));

        bool[] results = await Task.WhenAll(hostTask, clientTask);

        await Assert.That(results[0]).IsFalse();
        await Assert.That(results[1]).IsFalse();
    }
}

/// <summary>
/// A simple duplex stream backed by two pipe streams.
/// Reads from one pipe, writes to another. Used for testing protocol flows without actual TCP sockets.
/// </summary>
internal sealed class DuplexPipeStream : Stream
{
    private readonly Stream readStream;
    private readonly Stream writeStream;

    public DuplexPipeStream(Stream readFrom, Stream writeTo)
    {
        readStream = readFrom;
        writeStream = writeTo;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => readStream.Read(buffer, offset, count);
    public override void Write(byte[] buffer, int offset, int count) => writeStream.Write(buffer, offset, count);
    public override void Flush() => writeStream.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => readStream.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => readStream.ReadAsync(buffer, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => writeStream.WriteAsync(buffer, offset, count, cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => writeStream.WriteAsync(buffer, cancellationToken);

    public override Task FlushAsync(CancellationToken cancellationToken) => writeStream.FlushAsync(cancellationToken);
}
