using SharpShare.Storage;
using System.Collections.Concurrent;

namespace SharpShare.Transfer;

/// <summary>
/// Manages a queue of file downloads. Files are downloaded sequentially to avoid saturating the connection or causing
/// disk thrashing. The UI shows queued items and their current status.
/// </summary>
public sealed class TransferQueue
{
    private readonly ConcurrentQueue<QueuedTransfer> pendingDownloads = new();
    private readonly ConcurrentDictionary<uint, TransferProgressState> activeTransfers = new();
    private readonly ConcurrentDictionary<uint, TransferProgressState> completedTransfers = new();
    private volatile bool isProcessing;
    private CancellationTokenSource? processingCts;

    public event Action<TransferProgressState>? TransferStarted;
    public event Action<TransferProgressState>? TransferCompleted;
    public event Action<TransferProgressState>? TransferFailed;

    /// <summary>
    /// Gets all active transfers for UI progress display.
    /// </summary>
    public IReadOnlyCollection<TransferProgressState> ActiveTransfers =>
        activeTransfers.Values.ToArray();

    /// <summary>
    /// Gets the number of queued downloads waiting to start.
    /// </summary>
    public int QueuedCount => pendingDownloads.Count;

    /// <summary>
    /// Gets whether the queue is currently processing downloads.
    /// </summary>
    public bool IsProcessing => isProcessing;

    /// <summary>
    /// Enqueues a file for download. The download will start when the queue processor picks it up.
    /// </summary>
    public void EnqueueDownload(string relativePath, long fileSize)
    {
        var item = new QueuedTransfer(relativePath, fileSize);
        pendingDownloads.Enqueue(item);
        RollingFileLogger.Log(LogLevel.Info, $"Queued download: '{relativePath}' ({fileSize} bytes)");
    }

    /// <summary>
    /// Starts the queue processor. Downloads queued items one at a time. The downloadFunc is called for each item and
    /// should call FileTransferEngine.RequestDownloadAsync.
    /// </summary>
    public async Task ProcessQueueAsync(
        Func<string, long, CancellationToken, Task<(uint TransferId, TransferProgressState Progress)>> downloadFunc,
        Func<uint, Task> waitForCompletionFunc,
        CancellationToken cancellationToken = default)
    {
        if (isProcessing)
        {
            return;
        }

        isProcessing = true;
        processingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            while (pendingDownloads.TryDequeue(out var item) && !processingCts.Token.IsCancellationRequested)
            {
                try
                {
                    var (transferId, progress) = await downloadFunc(
                        item.RelativePath, item.FileSize, processingCts.Token);

                    activeTransfers[transferId] = progress;
                    TransferStarted?.Invoke(progress);

                    // Wait for the transfer to complete (the engine handles the actual data flow)
                    await waitForCompletionFunc(transferId);

                    activeTransfers.TryRemove(transferId, out _);
                    completedTransfers[transferId] = progress;

                    if (progress.Status == TransferStatus.Complete)
                    {
                        TransferCompleted?.Invoke(progress);
                    }
                    else if (progress.Status == TransferStatus.Failed)
                    {
                        TransferFailed?.Invoke(progress);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    RollingFileLogger.LogError($"Queue processing error for '{item.RelativePath}'", ex);
                }
            }
        }
        finally
        {
            isProcessing = false;
            processingCts?.Dispose();
            processingCts = null;
        }
    }

    /// <summary>
    /// Tracks an upload (incoming download request from peer). Uploads are not queued — they start immediately when
    /// requested.
    /// </summary>
    public TransferProgressState TrackUpload(uint transferId, string relativePath, long fileSize)
    {
        var progress = new TransferProgressState
        {
            TransferId = transferId,
            FileName = Path.GetFileName(relativePath),
            RelativePath = relativePath,
            TotalBytes = fileSize,
            Direction = TransferDirection.Upload,
            Status = TransferStatus.Active,
        };

        activeTransfers[transferId] = progress;
        TransferStarted?.Invoke(progress);
        return progress;
    }

    /// <summary>
    /// Marks an upload as complete and moves it to completed transfers.
    /// </summary>
    public void CompleteUpload(uint transferId)
    {
        if (activeTransfers.TryRemove(transferId, out var progress))
        {
            completedTransfers[transferId] = progress;
            if (progress.Status == TransferStatus.Complete)
            {
                TransferCompleted?.Invoke(progress);
            }
            else
            {
                TransferFailed?.Invoke(progress);
            }
        }
    }

    /// <summary>
    /// Cancels all pending and active transfers.
    /// </summary>
    public void CancelAll()
    {
        processingCts?.Cancel();

        // Clear pending queue
        while (pendingDownloads.TryDequeue(out _))
        {
        }

        // Mark active transfers as cancelled
        foreach (var kvp in activeTransfers)
        {
            kvp.Value.Status = TransferStatus.Cancelled;
        }
        activeTransfers.Clear();
    }
}

/// <summary>
/// Represents a file waiting in the download queue.
/// </summary>
public readonly record struct QueuedTransfer(string RelativePath, long FileSize);
