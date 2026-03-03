using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace SharpShare.Network;

/// <summary>
/// Writes binary protocol messages to a Stream. All integers are little-endian, strings are UTF-8. Uses ArrayPool for
/// temporary buffers to avoid allocations in the hot path.
/// </summary>
public static class ProtocolWriter
{
    public static async ValueTask WriteHandshakeAsync(Stream stream, HandshakeMessage message, CancellationToken cancellationToken = default)
    {
        int payloadSize = HandshakeMessage.PayloadSize;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ProtocolConstants.HeaderSize + payloadSize);
        try
        {
            WriteHeader(buffer, (uint)(ProtocolConstants.HeaderSize + payloadSize), MessageType.Handshake);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), message.ProtocolVersion);
            message.ChallengeNonce.AsSpan().CopyTo(buffer.AsSpan(12));

            await stream.WriteAsync(buffer.AsMemory(0, ProtocolConstants.HeaderSize + payloadSize), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async ValueTask WriteHandshakeResponseAsync(Stream stream, HandshakeResponseMessage message, CancellationToken cancellationToken = default)
    {
        int payloadSize = HandshakeResponseMessage.PayloadSize;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ProtocolConstants.HeaderSize + payloadSize);
        try
        {
            WriteHeader(buffer, (uint)(ProtocolConstants.HeaderSize + payloadSize), MessageType.HandshakeResponse);
            message.HmacResponse.AsSpan().CopyTo(buffer.AsSpan(8));
            message.CounterNonce.AsSpan().CopyTo(buffer.AsSpan(8 + ProtocolConstants.HmacSize));

            await stream.WriteAsync(buffer.AsMemory(0, ProtocolConstants.HeaderSize + payloadSize), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async ValueTask WriteHandshakeAckAsync(Stream stream, HandshakeAckMessage message, CancellationToken cancellationToken = default)
    {
        int payloadSize = HandshakeAckMessage.PayloadSize;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ProtocolConstants.HeaderSize + payloadSize);
        try
        {
            WriteHeader(buffer, (uint)(ProtocolConstants.HeaderSize + payloadSize), MessageType.HandshakeAck);
            message.CounterHmac.AsSpan().CopyTo(buffer.AsSpan(8));
            buffer[8 + ProtocolConstants.HmacSize] = message.Result;

            await stream.WriteAsync(buffer.AsMemory(0, ProtocolConstants.HeaderSize + payloadSize), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async ValueTask WriteFileListRequestAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ProtocolConstants.HeaderSize);
        try
        {
            WriteHeader(buffer, (uint)ProtocolConstants.HeaderSize, MessageType.FileListRequest);
            await stream.WriteAsync(buffer.AsMemory(0, ProtocolConstants.HeaderSize), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async ValueTask WriteFileListResponseAsync(Stream stream, FileListResponseMessage message, CancellationToken cancellationToken = default)
    {
        // Calculate total payload size: 4 (count) + sum of per-file entries
        int payloadSize = 4; // file count
        foreach (var file in message.Files)
        {
            int nameByteCount = Encoding.UTF8.GetByteCount(file.RelativePath);
            payloadSize += 8 + 8 + 2 + nameByteCount; // size + ticks + nameLen + name
        }

        int totalSize = ProtocolConstants.HeaderSize + payloadSize;
        if (totalSize > ProtocolConstants.MaxMessageSize)
        {
            throw new InvalidOperationException($"File list response exceeds max message size ({totalSize} > {ProtocolConstants.MaxMessageSize})");
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(totalSize);
        try
        {
            WriteHeader(buffer, (uint)totalSize, MessageType.FileListResponse);
            var span = buffer.AsSpan();

            int offset = ProtocolConstants.HeaderSize;
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset), message.Files.Length);
            offset += 4;

            foreach (var file in message.Files)
            {
                BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset), file.SizeBytes);
                offset += 8;
                BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset), file.LastModifiedUtcTicks);
                offset += 8;
                int nameByteCount = Encoding.UTF8.GetBytes(file.RelativePath, span.Slice(offset + 2));
                BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset), (ushort)nameByteCount);
                offset += 2 + nameByteCount;
            }

            await stream.WriteAsync(buffer.AsMemory(0, totalSize), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async ValueTask WriteFileDownloadRequestAsync(Stream stream, FileDownloadRequestMessage message, CancellationToken cancellationToken = default)
    {
        int nameByteCount = Encoding.UTF8.GetByteCount(message.RelativePath);
        int payloadSize = 4 + 8 + 2 + nameByteCount; // transferId + offset + nameLen + name
        int totalSize = ProtocolConstants.HeaderSize + payloadSize;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(totalSize);
        try
        {
            WriteHeader(buffer, (uint)totalSize, MessageType.FileDownloadRequest);
            var span = buffer.AsSpan();
            int offset = ProtocolConstants.HeaderSize;

            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset), message.TransferId);
            offset += 4;
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset), message.StartOffset);
            offset += 8;
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset), (ushort)nameByteCount);
            offset += 2;
            Encoding.UTF8.GetBytes(message.RelativePath, span.Slice(offset));

            await stream.WriteAsync(buffer.AsMemory(0, totalSize), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async ValueTask WriteFileChunkAsync(Stream stream, FileChunkMessage message, CancellationToken cancellationToken = default)
    {
        // Header(8) + transferId(4) + offset(8) + chunkLength(4) = 24 bytes fixed, then data
        int fixedPartSize = ProtocolConstants.HeaderSize + 4 + 8 + 4;
        int totalSize = fixedPartSize + message.ChunkLength;

        if (totalSize > ProtocolConstants.MaxMessageSize)
        {
            throw new InvalidOperationException($"File chunk message exceeds max size ({totalSize} > {ProtocolConstants.MaxMessageSize})");
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(totalSize);
        try
        {
            WriteHeader(buffer, (uint)totalSize, MessageType.FileChunk);
            var span = buffer.AsSpan();
            int offset = ProtocolConstants.HeaderSize;

            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset), message.TransferId);
            offset += 4;
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset), message.Offset);
            offset += 8;
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset), message.ChunkLength);
            offset += 4;
            message.Data.AsSpan(0, message.ChunkLength).CopyTo(span.Slice(offset));

            await stream.WriteAsync(buffer.AsMemory(0, totalSize), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async ValueTask WriteFileTransferCompleteAsync(Stream stream, FileTransferCompleteMessage message, CancellationToken cancellationToken = default)
    {
        int payloadSize = 4 + ProtocolConstants.XxHash128Size; // transferId + hash
        int totalSize = ProtocolConstants.HeaderSize + payloadSize;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(totalSize);
        try
        {
            WriteHeader(buffer, (uint)totalSize, MessageType.FileTransferComplete);
            var span = buffer.AsSpan();
            int offset = ProtocolConstants.HeaderSize;

            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset), message.TransferId);
            offset += 4;
            message.XxHash128.AsSpan().CopyTo(span.Slice(offset));

            await stream.WriteAsync(buffer.AsMemory(0, totalSize), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async ValueTask WriteFileTransferErrorAsync(Stream stream, FileTransferErrorMessage message, CancellationToken cancellationToken = default)
    {
        int msgByteCount = Encoding.UTF8.GetByteCount(message.ErrorMessage);
        int payloadSize = 4 + 1 + 2 + msgByteCount; // transferId + errorCode + msgLen + msg
        int totalSize = ProtocolConstants.HeaderSize + payloadSize;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(totalSize);
        try
        {
            WriteHeader(buffer, (uint)totalSize, MessageType.FileTransferError);
            var span = buffer.AsSpan();
            int offset = ProtocolConstants.HeaderSize;

            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset), message.TransferId);
            offset += 4;
            span[offset] = message.ErrorCode;
            offset += 1;
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset), (ushort)msgByteCount);
            offset += 2;
            Encoding.UTF8.GetBytes(message.ErrorMessage, span.Slice(offset));

            await stream.WriteAsync(buffer.AsMemory(0, totalSize), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async ValueTask WriteFileTransferCancelAsync(Stream stream, FileTransferCancelMessage message, CancellationToken cancellationToken = default)
    {
        int payloadSize = 4; // transferId
        int totalSize = ProtocolConstants.HeaderSize + payloadSize;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(totalSize);
        try
        {
            WriteHeader(buffer, (uint)totalSize, MessageType.FileTransferCancel);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), message.TransferId);
            await stream.WriteAsync(buffer.AsMemory(0, totalSize), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async ValueTask WritePingAsync(Stream stream, PingMessage message, CancellationToken cancellationToken = default)
    {
        int payloadSize = 8; // timestamp
        int totalSize = ProtocolConstants.HeaderSize + payloadSize;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(totalSize);
        try
        {
            WriteHeader(buffer, (uint)totalSize, MessageType.Ping);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(8), message.TimestampUtcTicks);
            await stream.WriteAsync(buffer.AsMemory(0, totalSize), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async ValueTask WritePongAsync(Stream stream, PongMessage message, CancellationToken cancellationToken = default)
    {
        int payloadSize = 8; // timestamp
        int totalSize = ProtocolConstants.HeaderSize + payloadSize;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(totalSize);
        try
        {
            WriteHeader(buffer, (uint)totalSize, MessageType.Pong);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(8), message.EchoTimestampUtcTicks);
            await stream.WriteAsync(buffer.AsMemory(0, totalSize), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async ValueTask WriteDisconnectAsync(Stream stream, DisconnectMessage message, CancellationToken cancellationToken = default)
    {
        int reasonByteCount = Encoding.UTF8.GetByteCount(message.Reason);
        int payloadSize = 2 + reasonByteCount; // reasonLen + reason
        int totalSize = ProtocolConstants.HeaderSize + payloadSize;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(totalSize);
        try
        {
            WriteHeader(buffer, (uint)totalSize, MessageType.Disconnect);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8), (ushort)reasonByteCount);
            Encoding.UTF8.GetBytes(message.Reason, buffer.AsSpan(10));
            await stream.WriteAsync(buffer.AsMemory(0, totalSize), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void WriteHeader(Span<byte> buffer, uint totalLength, MessageType type, ushort flags = 0)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, totalLength);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(4), (ushort)type);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(6), flags);
    }
}
