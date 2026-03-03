namespace SharpShare.Transfer;

public enum TransferDirection
{
    Download,
    Upload
}

public enum TransferStatus
{
    Queued,
    Active,
    Complete,
    Failed,
    Cancelled
}

/// <summary>
/// Shared mutable state for a single transfer. Written by the transfer engine thread, read by the UI timer at 4Hz. Uses
/// Interlocked for thread-safe progress updates with zero allocations in the hot path.
/// </summary>
public sealed class TransferProgressState
{
    public uint TransferId { get; init; }
    public string FileName { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public long TotalBytes { get; init; }
    public TransferDirection Direction { get; init; }

    // Updated atomically by the transfer engine
    public long BytesTransferred;
    public volatile TransferStatus Status;
    public volatile string? ErrorMessage;

    public double ProgressFraction
    {
        get
        {
            long total = TotalBytes;
            if (total <= 0)
            {
                return 0;
            }

            long transferred = Interlocked.Read(ref BytesTransferred);
            return (double)transferred / total;
        }
    }
}
