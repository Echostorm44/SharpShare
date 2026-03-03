using System.Buffers;
using SharpShare.Network;

namespace SharpShare.Tests.Network;

public class ProtocolReaderWriterTests
{
    // --- Handshake round-trip ---

    [Test]
    public async Task Handshake_RoundTrip_PreservesAllFields()
    {
        var nonce = new byte[32];
        Random.Shared.NextBytes(nonce);
        var original = new HandshakeMessage(ProtocolConstants.ProtocolVersion, nonce);

        using var ms = new MemoryStream();
        await ProtocolWriter.WriteHandshakeAsync(ms, original);
        ms.Position = 0;

        var header = await ProtocolReader.ReadHeaderAsync(ms);
        await Assert.That(header).IsNotNull();
        await Assert.That(header!.Value.Type).IsEqualTo(MessageType.Handshake);

        byte[] payload = await ProtocolReader.ReadPayloadAsync(ms, header.Value);
        try
        {
            var parsed = ProtocolReader.ParseHandshake(payload.AsSpan(0, header.Value.PayloadLength));
            await Assert.That(parsed.ProtocolVersion).IsEqualTo(ProtocolConstants.ProtocolVersion);
            await Assert.That(parsed.ChallengeNonce).IsEquivalentTo(nonce);
        }
        finally
        {
            if (payload.Length > 0) ArrayPool<byte>.Shared.Return(payload);
        }
    }

    // --- HandshakeResponse round-trip ---

    [Test]
    public async Task HandshakeResponse_RoundTrip_PreservesAllFields()
    {
        var hmac = new byte[32];
        var counterNonce = new byte[32];
        Random.Shared.NextBytes(hmac);
        Random.Shared.NextBytes(counterNonce);
        var original = new HandshakeResponseMessage(hmac, counterNonce);

        using var ms = new MemoryStream();
        await ProtocolWriter.WriteHandshakeResponseAsync(ms, original);
        ms.Position = 0;

        var header = await ProtocolReader.ReadHeaderAsync(ms);
        await Assert.That(header).IsNotNull();

        byte[] payload = await ProtocolReader.ReadPayloadAsync(ms, header!.Value);
        try
        {
            var parsed = ProtocolReader.ParseHandshakeResponse(payload.AsSpan(0, header.Value.PayloadLength));
            await Assert.That(parsed.HmacResponse).IsEquivalentTo(hmac);
            await Assert.That(parsed.CounterNonce).IsEquivalentTo(counterNonce);
        }
        finally
        {
            if (payload.Length > 0) ArrayPool<byte>.Shared.Return(payload);
        }
    }

    // --- HandshakeAck round-trip ---

    [Test]
    public async Task HandshakeAck_RoundTrip_PreservesAllFields()
    {
        var counterHmac = new byte[32];
        Random.Shared.NextBytes(counterHmac);
        var original = new HandshakeAckMessage(counterHmac, 0);

        using var ms = new MemoryStream();
        await ProtocolWriter.WriteHandshakeAckAsync(ms, original);
        ms.Position = 0;

        var header = await ProtocolReader.ReadHeaderAsync(ms);
        byte[] payload = await ProtocolReader.ReadPayloadAsync(ms, header!.Value);
        try
        {
            var parsed = ProtocolReader.ParseHandshakeAck(payload.AsSpan(0, header.Value.PayloadLength));
            await Assert.That(parsed.CounterHmac).IsEquivalentTo(counterHmac);
            await Assert.That(parsed.Result).IsEqualTo((byte)0);
        }
        finally
        {
            if (payload.Length > 0) ArrayPool<byte>.Shared.Return(payload);
        }
    }

    // --- FileListRequest round-trip ---

    [Test]
    public async Task FileListRequest_RoundTrip_HasCorrectType()
    {
        using var ms = new MemoryStream();
        await ProtocolWriter.WriteFileListRequestAsync(ms);
        ms.Position = 0;

        var header = await ProtocolReader.ReadHeaderAsync(ms);
        await Assert.That(header).IsNotNull();
        await Assert.That(header!.Value.Type).IsEqualTo(MessageType.FileListRequest);
        await Assert.That(header.Value.PayloadLength).IsEqualTo(0);
    }

    // --- FileListResponse round-trip ---

    [Test]
    public async Task FileListResponse_RoundTrip_PreservesFileEntries()
    {
        var files = new FileListEntry[]
        {
            new(1024 * 1024 * 500, DateTime.UtcNow.Ticks, "Movies/Movie_2026_01.mkv"),
            new(1024 * 1024 * 200, DateTime.UtcNow.AddDays(-3).Ticks, "Documents/Notes.txt"),
            new(0, DateTime.UtcNow.Ticks, "empty.dat"),
        };
        var original = new FileListResponseMessage(files);

        using var ms = new MemoryStream();
        await ProtocolWriter.WriteFileListResponseAsync(ms, original);
        ms.Position = 0;

        var header = await ProtocolReader.ReadHeaderAsync(ms);
        byte[] payload = await ProtocolReader.ReadPayloadAsync(ms, header!.Value);
        try
        {
            var parsed = ProtocolReader.ParseFileListResponse(payload.AsSpan(0, header.Value.PayloadLength));
            await Assert.That(parsed.Files.Length).IsEqualTo(3);
            await Assert.That(parsed.Files[0].RelativePath).IsEqualTo("Movies/Movie_2026_01.mkv");
            await Assert.That(parsed.Files[0].SizeBytes).IsEqualTo(1024L * 1024 * 500);
            await Assert.That(parsed.Files[1].RelativePath).IsEqualTo("Documents/Notes.txt");
            await Assert.That(parsed.Files[2].RelativePath).IsEqualTo("empty.dat");
            await Assert.That(parsed.Files[2].SizeBytes).IsEqualTo(0);
        }
        finally
        {
            if (payload.Length > 0) ArrayPool<byte>.Shared.Return(payload);
        }
    }

    [Test]
    public async Task FileListResponse_EmptyList_RoundTrips()
    {
        var original = new FileListResponseMessage(Array.Empty<FileListEntry>());

        using var ms = new MemoryStream();
        await ProtocolWriter.WriteFileListResponseAsync(ms, original);
        ms.Position = 0;

        var header = await ProtocolReader.ReadHeaderAsync(ms);
        byte[] payload = await ProtocolReader.ReadPayloadAsync(ms, header!.Value);
        try
        {
            var parsed = ProtocolReader.ParseFileListResponse(payload.AsSpan(0, header.Value.PayloadLength));
            await Assert.That(parsed.Files.Length).IsEqualTo(0);
        }
        finally
        {
            if (payload.Length > 0) ArrayPool<byte>.Shared.Return(payload);
        }
    }

    // --- FileDownloadRequest round-trip ---

    [Test]
    public async Task FileDownloadRequest_RoundTrip_PreservesAllFields()
    {
        var original = new FileDownloadRequestMessage(42, 1024 * 1024, "Videos/big_movie.mkv");

        using var ms = new MemoryStream();
        await ProtocolWriter.WriteFileDownloadRequestAsync(ms, original);
        ms.Position = 0;

        var header = await ProtocolReader.ReadHeaderAsync(ms);
        byte[] payload = await ProtocolReader.ReadPayloadAsync(ms, header!.Value);
        try
        {
            var parsed = ProtocolReader.ParseFileDownloadRequest(payload.AsSpan(0, header.Value.PayloadLength));
            await Assert.That(parsed.TransferId).IsEqualTo(42u);
            await Assert.That(parsed.StartOffset).IsEqualTo(1024L * 1024);
            await Assert.That(parsed.RelativePath).IsEqualTo("Videos/big_movie.mkv");
        }
        finally
        {
            if (payload.Length > 0) ArrayPool<byte>.Shared.Return(payload);
        }
    }

    // --- FileChunk round-trip ---

    [Test]
    public async Task FileChunk_RoundTrip_PreservesData()
    {
        var data = new byte[256];
        Random.Shared.NextBytes(data);
        var original = new FileChunkMessage(7, 512, 256, data);

        using var ms = new MemoryStream();
        await ProtocolWriter.WriteFileChunkAsync(ms, original);
        ms.Position = 0;

        var header = await ProtocolReader.ReadHeaderAsync(ms);
        byte[] payload = await ProtocolReader.ReadPayloadAsync(ms, header!.Value);
        try
        {
            var parsed = ProtocolReader.ParseFileChunk(payload.AsSpan(0, header.Value.PayloadLength));
            await Assert.That(parsed.TransferId).IsEqualTo(7u);
            await Assert.That(parsed.Offset).IsEqualTo(512L);
            await Assert.That(parsed.ChunkLength).IsEqualTo(256);
            await Assert.That(parsed.Data).IsEquivalentTo(data);
        }
        finally
        {
            if (payload.Length > 0) ArrayPool<byte>.Shared.Return(payload);
        }
    }

    // --- FileTransferComplete round-trip ---

    [Test]
    public async Task FileTransferComplete_RoundTrip_PreservesHash()
    {
        var hash = new byte[16];
        Random.Shared.NextBytes(hash);
        var original = new FileTransferCompleteMessage(99, hash);

        using var ms = new MemoryStream();
        await ProtocolWriter.WriteFileTransferCompleteAsync(ms, original);
        ms.Position = 0;

        var header = await ProtocolReader.ReadHeaderAsync(ms);
        byte[] payload = await ProtocolReader.ReadPayloadAsync(ms, header!.Value);
        try
        {
            var parsed = ProtocolReader.ParseFileTransferComplete(payload.AsSpan(0, header.Value.PayloadLength));
            await Assert.That(parsed.TransferId).IsEqualTo(99u);
            await Assert.That(parsed.XxHash128).IsEquivalentTo(hash);
        }
        finally
        {
            if (payload.Length > 0) ArrayPool<byte>.Shared.Return(payload);
        }
    }

    // --- FileTransferError round-trip ---

    [Test]
    public async Task FileTransferError_RoundTrip_PreservesAllFields()
    {
        var original = new FileTransferErrorMessage(5, TransferErrorCode.DiskFull, "Not enough disk space available");

        using var ms = new MemoryStream();
        await ProtocolWriter.WriteFileTransferErrorAsync(ms, original);
        ms.Position = 0;

        var header = await ProtocolReader.ReadHeaderAsync(ms);
        byte[] payload = await ProtocolReader.ReadPayloadAsync(ms, header!.Value);
        try
        {
            var parsed = ProtocolReader.ParseFileTransferError(payload.AsSpan(0, header.Value.PayloadLength));
            await Assert.That(parsed.TransferId).IsEqualTo(5u);
            await Assert.That(parsed.ErrorCode).IsEqualTo(TransferErrorCode.DiskFull);
            await Assert.That(parsed.ErrorMessage).IsEqualTo("Not enough disk space available");
        }
        finally
        {
            if (payload.Length > 0) ArrayPool<byte>.Shared.Return(payload);
        }
    }

    // --- FileTransferCancel round-trip ---

    [Test]
    public async Task FileTransferCancel_RoundTrip_PreservesTransferId()
    {
        var original = new FileTransferCancelMessage(77);

        using var ms = new MemoryStream();
        await ProtocolWriter.WriteFileTransferCancelAsync(ms, original);
        ms.Position = 0;

        var header = await ProtocolReader.ReadHeaderAsync(ms);
        byte[] payload = await ProtocolReader.ReadPayloadAsync(ms, header!.Value);
        try
        {
            var parsed = ProtocolReader.ParseFileTransferCancel(payload.AsSpan(0, header.Value.PayloadLength));
            await Assert.That(parsed.TransferId).IsEqualTo(77u);
        }
        finally
        {
            if (payload.Length > 0) ArrayPool<byte>.Shared.Return(payload);
        }
    }

    // --- Ping/Pong round-trip ---

    [Test]
    public async Task Ping_RoundTrip_PreservesTimestamp()
    {
        long timestamp = DateTime.UtcNow.Ticks;
        var original = new PingMessage(timestamp);

        using var ms = new MemoryStream();
        await ProtocolWriter.WritePingAsync(ms, original);
        ms.Position = 0;

        var header = await ProtocolReader.ReadHeaderAsync(ms);
        byte[] payload = await ProtocolReader.ReadPayloadAsync(ms, header!.Value);
        try
        {
            var parsed = ProtocolReader.ParsePing(payload.AsSpan(0, header.Value.PayloadLength));
            await Assert.That(parsed.TimestampUtcTicks).IsEqualTo(timestamp);
        }
        finally
        {
            if (payload.Length > 0) ArrayPool<byte>.Shared.Return(payload);
        }
    }

    [Test]
    public async Task Pong_RoundTrip_PreservesTimestamp()
    {
        long timestamp = DateTime.UtcNow.Ticks;
        var original = new PongMessage(timestamp);

        using var ms = new MemoryStream();
        await ProtocolWriter.WritePongAsync(ms, original);
        ms.Position = 0;

        var header = await ProtocolReader.ReadHeaderAsync(ms);
        byte[] payload = await ProtocolReader.ReadPayloadAsync(ms, header!.Value);
        try
        {
            var parsed = ProtocolReader.ParsePong(payload.AsSpan(0, header.Value.PayloadLength));
            await Assert.That(parsed.EchoTimestampUtcTicks).IsEqualTo(timestamp);
        }
        finally
        {
            if (payload.Length > 0) ArrayPool<byte>.Shared.Return(payload);
        }
    }

    // --- Disconnect round-trip ---

    [Test]
    public async Task Disconnect_RoundTrip_PreservesReason()
    {
        var original = new DisconnectMessage("User closed the application");

        using var ms = new MemoryStream();
        await ProtocolWriter.WriteDisconnectAsync(ms, original);
        ms.Position = 0;

        var header = await ProtocolReader.ReadHeaderAsync(ms);
        byte[] payload = await ProtocolReader.ReadPayloadAsync(ms, header!.Value);
        try
        {
            var parsed = ProtocolReader.ParseDisconnect(payload.AsSpan(0, header.Value.PayloadLength));
            await Assert.That(parsed.Reason).IsEqualTo("User closed the application");
        }
        finally
        {
            if (payload.Length > 0) ArrayPool<byte>.Shared.Return(payload);
        }
    }

    // --- Error handling ---

    [Test]
    public async Task ReadHeader_EmptyStream_ReturnsNull()
    {
        using var ms = new MemoryStream();
        var header = await ProtocolReader.ReadHeaderAsync(ms);
        await Assert.That(header).IsNull();
    }

    [Test]
    public async Task ReadHeader_TruncatedStream_ThrowsProtocolException()
    {
        using var ms = new MemoryStream(new byte[] { 0x10, 0x00 }); // only 2 bytes
        await Assert.That(() => ProtocolReader.ReadHeaderAsync(ms).AsTask())
            .ThrowsExactly<ProtocolException>();
    }

    [Test]
    public async Task ReadHeader_OversizedMessage_ThrowsProtocolException()
    {
        using var ms = new MemoryStream();
        // Write a header claiming to be 2MB (exceeds max)
        var header = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header, 2 * 1024 * 1024);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)MessageType.Ping);
        ms.Write(header);
        ms.Position = 0;

        await Assert.That(() => ProtocolReader.ReadHeaderAsync(ms).AsTask())
            .ThrowsExactly<ProtocolException>();
    }

    // --- Multiple messages on same stream ---

    [Test]
    public async Task MultipleMessages_ReadSequentially_AllPreserved()
    {
        using var ms = new MemoryStream();

        // Write 3 different messages
        await ProtocolWriter.WritePingAsync(ms, new PingMessage(100));
        await ProtocolWriter.WriteFileListRequestAsync(ms);
        await ProtocolWriter.WriteDisconnectAsync(ms, new DisconnectMessage("bye"));

        ms.Position = 0;

        // Read all 3
        var h1 = await ProtocolReader.ReadHeaderAsync(ms);
        await Assert.That(h1!.Value.Type).IsEqualTo(MessageType.Ping);
        byte[] p1 = await ProtocolReader.ReadPayloadAsync(ms, h1.Value);
        var ping = ProtocolReader.ParsePing(p1.AsSpan(0, h1.Value.PayloadLength));
        await Assert.That(ping.TimestampUtcTicks).IsEqualTo(100);
        if (p1.Length > 0) ArrayPool<byte>.Shared.Return(p1);

        var h2 = await ProtocolReader.ReadHeaderAsync(ms);
        await Assert.That(h2!.Value.Type).IsEqualTo(MessageType.FileListRequest);

        var h3 = await ProtocolReader.ReadHeaderAsync(ms);
        byte[] p3 = await ProtocolReader.ReadPayloadAsync(ms, h3!.Value);
        var disconnect = ProtocolReader.ParseDisconnect(p3.AsSpan(0, h3.Value.PayloadLength));
        await Assert.That(disconnect.Reason).IsEqualTo("bye");
        if (p3.Length > 0) ArrayPool<byte>.Shared.Return(p3);
    }

    // --- Unicode file names ---

    [Test]
    public async Task FileListResponse_UnicodeFileNames_RoundTrips()
    {
        var files = new FileListEntry[]
        {
            new(1024, DateTime.UtcNow.Ticks, "映画/テスト.mkv"),
            new(2048, DateTime.UtcNow.Ticks, "фильмы/тест.mp4"),
            new(4096, DateTime.UtcNow.Ticks, "películas/café.avi"),
        };
        var original = new FileListResponseMessage(files);

        using var ms = new MemoryStream();
        await ProtocolWriter.WriteFileListResponseAsync(ms, original);
        ms.Position = 0;

        var header = await ProtocolReader.ReadHeaderAsync(ms);
        byte[] payload = await ProtocolReader.ReadPayloadAsync(ms, header!.Value);
        try
        {
            var parsed = ProtocolReader.ParseFileListResponse(payload.AsSpan(0, header.Value.PayloadLength));
            await Assert.That(parsed.Files[0].RelativePath).IsEqualTo("映画/テスト.mkv");
            await Assert.That(parsed.Files[1].RelativePath).IsEqualTo("фильмы/тест.mp4");
            await Assert.That(parsed.Files[2].RelativePath).IsEqualTo("películas/café.avi");
        }
        finally
        {
            if (payload.Length > 0) ArrayPool<byte>.Shared.Return(payload);
        }
    }
}
