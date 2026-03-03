using SharpShare.Network;
using SharpShare.Storage;
using System.Buffers;

namespace SharpShare.Transfer;

/// <summary>
/// Handles the actual reading/writing of files during transfers. Sender: reads file → sends chunks via PeerSession.
/// Receiver: receives chunks → writes to temp file → verifies hash → renames.
/// </summary>
public sealed class FileTransferEngine
{
    private readonly PeerSession session;
    private readonly string sharedFolderPath;
    private uint nextTransferId;

    public FileTransferEngine(PeerSession session, string sharedFolderPath)
    {
        this.session = session;
        this.sharedFolderPath = sharedFolderPath;
    }

    // --- Sending (Upload) ---

    /// <summary>
    /// Sends a file to the connected peer. Called when we receive a FileDownloadRequest. Updates the provided
    /// TransferProgressState as chunks are sent.
    /// </summary>
    public async Task SendFileAsync(
        FileDownloadRequestMessage request,
        TransferProgressState progressState,
        CancellationToken cancellationToken = default)
    {
        string fullPath = SharedFolderWatcher.GetFullPath(sharedFolderPath, request.RelativePath);

        if (!File.Exists(fullPath))
        {
            await session.SendFileTransferErrorAsync(
                request.TransferId, TransferErrorCode.FileNotFound,
                $"File not found: {request.RelativePath}");
            progressState.Status = TransferStatus.Failed;
            progressState.ErrorMessage = "File not found";
            return;
        }

        progressState.Status = TransferStatus.Active;
        var hasher = new IncrementalHasher();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ProtocolConstants.FileChunkSize);
        try
        {
            await using var fileStream = new FileStream(fullPath,
                FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: ProtocolConstants.FileChunkSize,
                FileOptions.SequentialScan | FileOptions.Asynchronous);

            // Seek to requested offset for resume support
            if (request.StartOffset > 0)
            {
                fileStream.Seek(request.StartOffset, SeekOrigin.Begin);
                RollingFileLogger.Log(LogLevel.Info,
                    $"Resuming send of '{request.RelativePath}' from offset {request.StartOffset}");

                // We need to hash the skipped portion too for full-file integrity
                // Re-read from beginning to hash the skipped part
                if (request.StartOffset > 0)
                {
                    await HashSkippedPortionAsync(fullPath, request.StartOffset, hasher, cancellationToken);
                }
            }

            long offset = request.StartOffset;
            int bytesRead;
            while ((bytesRead = await fileStream.ReadAsync(buffer.AsMemory(0, ProtocolConstants.FileChunkSize), cancellationToken)) > 0)
            {
                hasher.AppendChunk(buffer.AsSpan(0, bytesRead));

                // Copy chunk data — the write loop serializes asynchronously via a channel,
                // so the buffer may be overwritten before the write completes.
                byte[] chunkData = buffer.AsSpan(0, bytesRead).ToArray();

                await session.SendFileChunkAsync(
                    request.TransferId, offset, bytesRead, chunkData, cancellationToken);

                offset += bytesRead;
                Interlocked.Exchange(ref progressState.BytesTransferred, offset);
            }

            byte[] hash = hasher.Finalize();
            await session.SendFileTransferCompleteAsync(request.TransferId, hash, cancellationToken);
            progressState.Status = TransferStatus.Complete;

            RollingFileLogger.Log(LogLevel.Info,
                $"Sent '{request.RelativePath}' ({progressState.TotalBytes} bytes)");
        }
        catch (OperationCanceledException)
        {
            progressState.Status = TransferStatus.Cancelled;
            try
            {
                await session.SendFileTransferCancelAsync(request.TransferId);
            }
            catch
            {
            }
        }
        catch (Exception ex)
        {
            progressState.Status = TransferStatus.Failed;
            progressState.ErrorMessage = ex.Message;
            RollingFileLogger.LogError($"Error sending '{request.RelativePath}'", ex);
            try
            {
                await session.SendFileTransferErrorAsync(
                    request.TransferId, TransferErrorCode.Unknown, ex.Message);
            }
            catch
            {
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // --- Receiving (Download) ---

    /// <summary>
    /// Manages a receiving transfer. Creates a temp file, writes chunks, verifies hash, and renames. Returns a
    /// ReceiveContext that should be fed incoming FileChunk messages.
    /// </summary>
    public ReceiveContext StartReceive(
        uint transferId,
        string relativePath,
        long totalBytes,
        TransferProgressState progressState)
    {
        string finalPath = SharedFolderWatcher.GetFullPath(sharedFolderPath, relativePath);
        string tempPath = $"{finalPath}.tmp";

        string? directory = Path.GetDirectoryName(finalPath);
        if (directory != null && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Check for existing temp file (resume support)
        long existingBytes = 0;
        if (File.Exists(tempPath))
        {
            existingBytes = new FileInfo(tempPath).Length;
            RollingFileLogger.Log(LogLevel.Info,
                $"Resuming download of '{relativePath}' from {existingBytes} bytes");
        }

        return new ReceiveContext(transferId, relativePath, finalPath, tempPath,
            totalBytes, existingBytes, progressState);
    }

    /// <summary>
    /// Initiates a file download request to the peer. Returns the TransferProgressState for tracking and the
    /// transferId.
    /// </summary>
    public async Task<(uint TransferId, TransferProgressState Progress)> RequestDownloadAsync(
        string relativePath, long fileSize, CancellationToken cancellationToken = default)
    {
        uint transferId = Interlocked.Increment(ref nextTransferId);

        var progressState = new TransferProgressState
        {
            TransferId = transferId,
            FileName = Path.GetFileName(relativePath),
            RelativePath = relativePath,
            TotalBytes = fileSize,
            Direction = TransferDirection.Download,
            Status = TransferStatus.Queued,
        };

        // Check for resume (existing .tmp file)
        string finalPath = SharedFolderWatcher.GetFullPath(sharedFolderPath, relativePath);
        string tempPath = $"{finalPath}.tmp";
        long startOffset = 0;
        if (File.Exists(tempPath))
        {
            startOffset = new FileInfo(tempPath).Length;
        }

        await session.SendFileDownloadRequestAsync(transferId, startOffset, relativePath, cancellationToken);

        RollingFileLogger.Log(LogLevel.Info,
            $"Requested download: '{relativePath}' (transferId={transferId}, offset={startOffset})");

        return (transferId, progressState);
    }

    private static async Task HashSkippedPortionAsync(
        string filePath, long bytesToHash, IncrementalHasher hasher, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ProtocolConstants.FileChunkSize);
        try
        {
            await using var stream = new FileStream(filePath,
                FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: ProtocolConstants.FileChunkSize,
                FileOptions.SequentialScan | FileOptions.Asynchronous);

            long remaining = bytesToHash;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(remaining, buffer.Length);
                int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                hasher.AppendChunk(buffer.AsSpan(0, bytesRead));
                remaining -= bytesRead;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

/// <summary>
/// Manages the state of a single incoming file transfer. Fed FileChunk messages as they arrive. Writes to temp file,
/// computes incremental hash.
/// </summary>
public sealed class ReceiveContext : IDisposable
{
    private readonly uint transferId;
    private readonly string relativePath;
    private readonly string finalPath;
    private readonly string tempPath;
    private readonly long totalBytes;
    private readonly TransferProgressState progressState;
    private readonly IncrementalHasher hasher = new();
    private FileStream? fileStream;
    private bool disposed;

    public uint TransferId => transferId;

    public ReceiveContext(
        uint transferId, string relativePath, string finalPath, string tempPath,
        long totalBytes, long existingBytes, TransferProgressState progressState)
    {
        this.transferId = transferId;
        this.relativePath = relativePath;
        this.finalPath = finalPath;
        this.tempPath = tempPath;
        this.totalBytes = totalBytes;
        this.progressState = progressState;

        progressState.Status = TransferStatus.Active;

        // Open temp file for append or create
        fileStream = new FileStream(tempPath,
            existingBytes > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.Read,
            bufferSize: ProtocolConstants.FileChunkSize,
            FileOptions.Asynchronous);

        // If resuming, we need to hash the existing portion
        if (existingBytes > 0)
        {
            Interlocked.Exchange(ref progressState.BytesTransferred, existingBytes);
            HashExistingTempFile(existingBytes);
        }
    }

    /// <summary>
    /// Processes an incoming file chunk. Writes data to temp file and updates hash.
    /// </summary>
    public async Task ProcessChunkAsync(FileChunkMessage chunk, CancellationToken cancellationToken = default)
    {
        if (disposed || fileStream == null)
        {
            return;
        }

        hasher.AppendChunk(chunk.Data.AsSpan(0, chunk.ChunkLength));
        await fileStream.WriteAsync(chunk.Data.AsMemory(0, chunk.ChunkLength), cancellationToken);

        long newTotal = Interlocked.Add(ref progressState.BytesTransferred, chunk.ChunkLength);
    }

    /// <summary>
    /// Finalizes the transfer. Verifies hash, renames temp file to final name. Returns true if hash matches and file
    /// was saved successfully.
    /// </summary>
    public async Task<bool> FinalizeAsync(byte[] expectedHash)
    {
        if (fileStream != null)
        {
            await fileStream.FlushAsync();
            await fileStream.DisposeAsync();
            fileStream = null;
        }

        byte[] actualHash = hasher.Finalize();

        if (!actualHash.AsSpan().SequenceEqual(expectedHash))
        {
            progressState.Status = TransferStatus.Failed;
            progressState.ErrorMessage = "Hash mismatch — file may be corrupted";
            RollingFileLogger.Log(LogLevel.Error,
                $"Hash mismatch for '{relativePath}'. Expected: {Convert.ToHexString(expectedHash)}, Got: {Convert.ToHexString(actualHash)}");

            // Delete corrupted temp file
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
            }
            return false;
        }

        // Rename temp to final
        try
        {
            if (File.Exists(finalPath))
            {
                File.Delete(finalPath);
            }

            File.Move(tempPath, finalPath);
        }
        catch (Exception ex)
        {
            progressState.Status = TransferStatus.Failed;
            progressState.ErrorMessage = $"Failed to save file: {ex.Message}";
            RollingFileLogger.LogError($"Failed to rename temp file for '{relativePath}'", ex);
            return false;
        }

        progressState.Status = TransferStatus.Complete;
        RollingFileLogger.Log(LogLevel.Info,
            $"Download complete: '{relativePath}' ({totalBytes} bytes)");
        return true;
    }

    /// <summary>
    /// Marks the transfer as failed with the given error.
    /// </summary>
    public void Fail(string errorMessage)
    {
        progressState.Status = TransferStatus.Failed;
        progressState.ErrorMessage = errorMessage;
        Dispose();
    }

    /// <summary>
    /// Cancels the transfer. Keeps the temp file for potential resume.
    /// </summary>
    public void Cancel()
    {
        progressState.Status = TransferStatus.Cancelled;
        Dispose();
    }

    /// <summary>
    /// Cancels the transfer and deletes the partial temp file.
    /// </summary>
    public void CancelAndDeleteTempFile()
    {
        progressState.Status = TransferStatus.Cancelled;
        Dispose();
        try
        {
            File.Delete(tempPath);
        }
        catch
        {
        }
    }

    private void HashExistingTempFile(long existingBytes)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ProtocolConstants.FileChunkSize);
        try
        {
            using var readStream = new FileStream(tempPath,
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: ProtocolConstants.FileChunkSize,
                FileOptions.SequentialScan);

            long remaining = existingBytes;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(remaining, buffer.Length);
                int bytesRead = readStream.Read(buffer, 0, toRead);
                if (bytesRead == 0)
                {
                    break;
                }

                hasher.AppendChunk(buffer.AsSpan(0, bytesRead));
                remaining -= bytesRead;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        fileStream?.Dispose();
        fileStream = null;
        hasher.Dispose();
    }
}
