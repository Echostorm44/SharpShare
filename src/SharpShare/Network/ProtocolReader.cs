using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace SharpShare.Network;

/// <summary>
/// Reads binary protocol messages from a Stream. All integers are little-endian, strings are UTF-8. Uses ArrayPool for
/// temporary buffers to avoid allocations in the hot path.
/// </summary>
public static class ProtocolReader
{
    /// <summary>
    /// Reads a complete message header from the stream. Returns null if the stream ends cleanly. Throws on
    /// invalid/oversized messages.
    /// </summary>
    public static async ValueTask<MessageHeader?> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] headerBuffer = ArrayPool<byte>.Shared.Rent(ProtocolConstants.HeaderSize);
        try
        {
            int bytesRead = await ReadExactlyAsync(stream, headerBuffer, ProtocolConstants.HeaderSize, cancellationToken);
            if (bytesRead == 0)
            {
                return null; // Clean disconnect
            }

            var span = headerBuffer.AsSpan();
            uint totalLength = BinaryPrimitives.ReadUInt32LittleEndian(span);
            ushort typeRaw = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(4));
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(6));

            if (totalLength < ProtocolConstants.HeaderSize)
            {
                throw new ProtocolException($"Invalid message length: {totalLength} (minimum is {ProtocolConstants.HeaderSize})");
            }

            if (totalLength > ProtocolConstants.MaxMessageSize)
            {
                throw new ProtocolException($"Message exceeds max size: {totalLength} > {ProtocolConstants.MaxMessageSize}");
            }

            return new MessageHeader(totalLength, (MessageType)typeRaw, flags);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuffer);
        }
    }

    /// <summary>
    /// Reads the payload bytes for a message with the given header. The returned byte[] is rented from ArrayPool and
    /// MUST be returned by the caller.
    /// </summary>
    public static async ValueTask<byte[]> ReadPayloadAsync(Stream stream, MessageHeader header, CancellationToken cancellationToken = default)
    {
        int payloadLength = header.PayloadLength;
        if (payloadLength == 0)
        {
            return Array.Empty<byte>();
        }

        byte[] payloadBuffer = ArrayPool<byte>.Shared.Rent(payloadLength);
        int bytesRead = await ReadExactlyAsync(stream, payloadBuffer, payloadLength, cancellationToken);
        if (bytesRead < payloadLength)
        {
            ArrayPool<byte>.Shared.Return(payloadBuffer);
            throw new ProtocolException($"Unexpected end of stream reading payload: expected {payloadLength} bytes, got {bytesRead}");
        }
        return payloadBuffer;
    }

    public static HandshakeMessage ParseHandshake(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HandshakeMessage.PayloadSize)
        {
            throw new ProtocolException($"Handshake payload too small: {payload.Length} < {HandshakeMessage.PayloadSize}");
        }

        uint protocolVersion = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        byte[] nonce = payload.Slice(4, ProtocolConstants.NonceSize).ToArray();
        return new HandshakeMessage(protocolVersion, nonce);
    }

    public static HandshakeResponseMessage ParseHandshakeResponse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HandshakeResponseMessage.PayloadSize)
        {
            throw new ProtocolException($"HandshakeResponse payload too small: {payload.Length} < {HandshakeResponseMessage.PayloadSize}");
        }

        byte[] hmac = payload.Slice(0, ProtocolConstants.HmacSize).ToArray();
        byte[] counterNonce = payload.Slice(ProtocolConstants.HmacSize, ProtocolConstants.NonceSize).ToArray();
        return new HandshakeResponseMessage(hmac, counterNonce);
    }

    public static HandshakeAckMessage ParseHandshakeAck(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HandshakeAckMessage.PayloadSize)
        {
            throw new ProtocolException($"HandshakeAck payload too small: {payload.Length} < {HandshakeAckMessage.PayloadSize}");
        }

        byte[] counterHmac = payload.Slice(0, ProtocolConstants.HmacSize).ToArray();
        byte result = payload[ProtocolConstants.HmacSize];
        return new HandshakeAckMessage(counterHmac, result);
    }

    public static FileListResponseMessage ParseFileListResponse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
        {
            throw new ProtocolException("FileListResponse payload too small for file count");
        }

        int fileCount = BinaryPrimitives.ReadInt32LittleEndian(payload);
        if (fileCount < 0 || fileCount > 100_000)
        {
            throw new ProtocolException($"Invalid file count: {fileCount}");
        }

        var files = new FileListEntry[fileCount];
        int offset = 4;

        for (int i = 0;i < fileCount;i++)
        {
            if (offset + 18 > payload.Length) // 8+8+2 minimum per entry
            {
                throw new ProtocolException($"Payload truncated reading file entry {i}");
            }

            long sizeBytes = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(offset));
            offset += 8;
            long lastModifiedTicks = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(offset));
            offset += 8;
            ushort nameLen = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(offset));
            offset += 2;

            if (nameLen > ProtocolConstants.MaxFileNameLength)
            {
                throw new ProtocolException($"File name too long: {nameLen} > {ProtocolConstants.MaxFileNameLength}");
            }

            if (offset + nameLen > payload.Length)
            {
                throw new ProtocolException($"Payload truncated reading file name for entry {i}");
            }

            string relativePath = Encoding.UTF8.GetString(payload.Slice(offset, nameLen));
            offset += nameLen;

            files[i] = new FileListEntry(sizeBytes, lastModifiedTicks, relativePath);
        }

        return new FileListResponseMessage(files);
    }

    public static FileDownloadRequestMessage ParseFileDownloadRequest(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 14) // 4+8+2 minimum
        {
            throw new ProtocolException("FileDownloadRequest payload too small");
        }

        int offset = 0;
        uint transferId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset));
        offset += 4;
        long startOffset = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(offset));
        offset += 8;
        ushort nameLen = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(offset));
        offset += 2;

        if (nameLen > ProtocolConstants.MaxFileNameLength)
        {
            throw new ProtocolException($"File path too long: {nameLen}");
        }

        if (offset + nameLen > payload.Length)
        {
            throw new ProtocolException("Payload truncated reading file path");
        }

        string relativePath = Encoding.UTF8.GetString(payload.Slice(offset, nameLen));
        return new FileDownloadRequestMessage(transferId, startOffset, relativePath);
    }

    public static FileChunkMessage ParseFileChunk(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 16) // 4+8+4 minimum
        {
            throw new ProtocolException("FileChunk payload too small");
        }

        int offset = 0;
        uint transferId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset));
        offset += 4;
        long fileOffset = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(offset));
        offset += 8;
        int chunkLength = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset));
        offset += 4;

        if (chunkLength < 0 || offset + chunkLength > payload.Length)
        {
            throw new ProtocolException($"Invalid chunk length: {chunkLength}");
        }

        byte[] data = payload.Slice(offset, chunkLength).ToArray();
        return new FileChunkMessage(transferId, fileOffset, chunkLength, data);
    }

    public static FileTransferCompleteMessage ParseFileTransferComplete(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4 + ProtocolConstants.XxHash128Size)
        {
            throw new ProtocolException("FileTransferComplete payload too small");
        }

        uint transferId = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        byte[] hash = payload.Slice(4, ProtocolConstants.XxHash128Size).ToArray();
        return new FileTransferCompleteMessage(transferId, hash);
    }

    public static FileTransferErrorMessage ParseFileTransferError(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 7) // 4+1+2 minimum
        {
            throw new ProtocolException("FileTransferError payload too small");
        }

        int offset = 0;
        uint transferId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset));
        offset += 4;
        byte errorCode = payload[offset];
        offset += 1;
        ushort msgLen = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(offset));
        offset += 2;

        if (offset + msgLen > payload.Length)
        {
            throw new ProtocolException("Payload truncated reading error message");
        }

        string errorMessage = Encoding.UTF8.GetString(payload.Slice(offset, msgLen));
        return new FileTransferErrorMessage(transferId, errorCode, errorMessage);
    }

    public static FileTransferCancelMessage ParseFileTransferCancel(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
        {
            throw new ProtocolException("FileTransferCancel payload too small");
        }

        uint transferId = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        return new FileTransferCancelMessage(transferId);
    }

    public static PingMessage ParsePing(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8)
        {
            throw new ProtocolException("Ping payload too small");
        }

        long timestamp = BinaryPrimitives.ReadInt64LittleEndian(payload);
        return new PingMessage(timestamp);
    }

    public static PongMessage ParsePong(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8)
        {
            throw new ProtocolException("Pong payload too small");
        }

        long timestamp = BinaryPrimitives.ReadInt64LittleEndian(payload);
        return new PongMessage(timestamp);
    }

    public static DisconnectMessage ParseDisconnect(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
        {
            throw new ProtocolException("Disconnect payload too small");
        }

        ushort reasonLen = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        if (2 + reasonLen > payload.Length)
        {
            throw new ProtocolException("Payload truncated reading disconnect reason");
        }

        string reason = Encoding.UTF8.GetString(payload.Slice(2, reasonLen));
        return new DisconnectMessage(reason);
    }

    /// <summary>
    /// Reads exactly the specified number of bytes from the stream. Returns 0 if the stream is at EOF before any bytes
    /// are read. Throws ProtocolException if the stream ends mid-read.
    /// </summary>
    private static async ValueTask<int> ReadExactlyAsync(Stream stream, byte[] buffer, int count, CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int bytesRead = await stream.ReadAsync(buffer.AsMemory(totalRead, count - totalRead), cancellationToken);
            if (bytesRead == 0)
            {
                if (totalRead == 0)
                {
                    return 0; // Clean EOF
                }

                throw new ProtocolException($"Connection closed mid-read: got {totalRead} of {count} bytes");
            }
            totalRead += bytesRead;
        }
        return totalRead;
    }
}

/// <summary>
/// Thrown when a protocol violation is detected (malformed messages, oversized payloads, etc.)
/// </summary>
public sealed class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message)
    {
    }

    public ProtocolException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
