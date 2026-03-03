namespace SharpShare.Network;

public static class ProtocolConstants
{
    public const uint ProtocolVersion = 1;
    public const int HeaderSize = 8;
    public const int DefaultPort = 9500;
    public const int MaxMessageSize = 1024 * 1024;          // 1 MB
    public const int FileChunkSize = 256 * 1024;             // 256 KB
    public const int MaxFileNameLength = 4096;
    public const int PingIntervalSeconds = 30;
    public const int ConnectionTimeoutSeconds = 60;
    public const int AuthTimeoutSeconds = 10;
    public const int NonceSize = 32;
    public const int HmacSize = 32;                          // HMAC-SHA256
    public const int XxHash128Size = 16;
}

public enum MessageType : ushort
{
    Handshake = 0x0001,
    HandshakeResponse = 0x0002,
    HandshakeAck = 0x0003,
    FileListRequest = 0x0010,
    FileListResponse = 0x0011,
    FileDownloadRequest = 0x0020,
    FileChunk = 0x0021,
    FileTransferComplete = 0x0022,
    FileTransferError = 0x0023,
    FileTransferCancel = 0x0024,
    Ping = 0x0030,
    Pong = 0x0031,
    Disconnect = 0x00FF,
}

public readonly struct MessageHeader
{
    public readonly uint TotalLength;
    public readonly MessageType Type;
    public readonly ushort Flags;

    public MessageHeader(uint totalLength, MessageType type, ushort flags = 0)
    {
        TotalLength = totalLength;
        Type = type;
        Flags = flags;
    }

    public int PayloadLength => (int)(TotalLength - ProtocolConstants.HeaderSize);
}

// --- Authentication Messages ---

public readonly struct HandshakeMessage
{
    public readonly uint ProtocolVersion;
    public readonly byte[] ChallengeNonce; // 32 bytes

    public HandshakeMessage(uint protocolVersion, byte[] challengeNonce)
    {
        ProtocolVersion = protocolVersion;
        ChallengeNonce = challengeNonce;
    }

    public const int PayloadSize = 4 + ProtocolConstants.NonceSize; // 36
}

public readonly struct HandshakeResponseMessage
{
    public readonly byte[] HmacResponse;   // 32 bytes
    public readonly byte[] CounterNonce;   // 32 bytes

    public HandshakeResponseMessage(byte[] hmacResponse, byte[] counterNonce)
    {
        HmacResponse = hmacResponse;
        CounterNonce = counterNonce;
    }

    public const int PayloadSize = ProtocolConstants.HmacSize + ProtocolConstants.NonceSize; // 64
}

public readonly struct HandshakeAckMessage
{
    public readonly byte[] CounterHmac;    // 32 bytes
    public readonly byte Result;           // 0 = success, 1 = fail

    public HandshakeAckMessage(byte[] counterHmac, byte result)
    {
        CounterHmac = counterHmac;
        Result = result;
    }

    public const int PayloadSize = ProtocolConstants.HmacSize + 1; // 33
}

// --- File List Messages ---

public readonly struct FileListEntry
{
    public readonly long SizeBytes;
    public readonly long LastModifiedUtcTicks;
    public readonly string RelativePath;

    public FileListEntry(long sizeBytes, long lastModifiedUtcTicks, string relativePath)
    {
        SizeBytes = sizeBytes;
        LastModifiedUtcTicks = lastModifiedUtcTicks;
        RelativePath = relativePath;
    }
}

public readonly struct FileListResponseMessage
{
    public readonly FileListEntry[] Files;

    public FileListResponseMessage(FileListEntry[] files)
    {
        Files = files;
    }
}

// --- Transfer Messages ---

public readonly struct FileDownloadRequestMessage
{
    public readonly uint TransferId;
    public readonly long StartOffset;
    public readonly string RelativePath;

    public FileDownloadRequestMessage(uint transferId, long startOffset, string relativePath)
    {
        TransferId = transferId;
        StartOffset = startOffset;
        RelativePath = relativePath;
    }
}

public readonly struct FileChunkMessage
{
    public readonly uint TransferId;
    public readonly long Offset;
    public readonly int ChunkLength;
    public readonly byte[] Data;

    public FileChunkMessage(uint transferId, long offset, int chunkLength, byte[] data)
    {
        TransferId = transferId;
        Offset = offset;
        ChunkLength = chunkLength;
        Data = data;
    }
}

public readonly struct FileTransferCompleteMessage
{
    public readonly uint TransferId;
    public readonly byte[] XxHash128; // 16 bytes

    public FileTransferCompleteMessage(uint transferId, byte[] xxHash128)
    {
        TransferId = transferId;
        XxHash128 = xxHash128;
    }
}

public readonly struct FileTransferErrorMessage
{
    public readonly uint TransferId;
    public readonly byte ErrorCode;
    public readonly string ErrorMessage;

    public FileTransferErrorMessage(uint transferId, byte errorCode, string errorMessage)
    {
        TransferId = transferId;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }
}

public readonly struct FileTransferCancelMessage
{
    public readonly uint TransferId;

    public FileTransferCancelMessage(uint transferId)
    {
        TransferId = transferId;
    }
}

// --- Keepalive ---

public readonly struct PingMessage
{
    public readonly long TimestampUtcTicks;

    public PingMessage(long timestampUtcTicks)
    {
        TimestampUtcTicks = timestampUtcTicks;
    }
}

public readonly struct PongMessage
{
    public readonly long EchoTimestampUtcTicks;

    public PongMessage(long echoTimestampUtcTicks)
    {
        EchoTimestampUtcTicks = echoTimestampUtcTicks;
    }
}

// --- Disconnect ---

public readonly struct DisconnectMessage
{
    public readonly string Reason;

    public DisconnectMessage(string reason)
    {
        Reason = reason;
    }
}

// Transfer error codes
public static class TransferErrorCode
{
    public const byte FileNotFound = 1;
    public const byte AccessDenied = 2;
    public const byte DiskFull = 3;
    public const byte HashMismatch = 4;
    public const byte InvalidPath = 5;
    public const byte Unknown = 255;
}
