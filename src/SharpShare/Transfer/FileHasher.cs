using SharpShare.Storage;
using System.Buffers;
using System.IO.Hashing;

namespace SharpShare.Transfer;

/// <summary>
/// Computes XxHash128 for file integrity verification. Supports both full-file hashing and incremental (streaming)
/// hashing.
/// </summary>
public static class FileHasher
{
    /// <summary>
    /// Computes XxHash128 of an entire file. Uses ArrayPool for the read buffer.
    /// </summary>
    public static byte[] ComputeFileHash(string filePath)
    {
        var hasher = new XxHash128();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            using var stream = new FileStream(filePath,
                FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 256 * 1024,
                FileOptions.SequentialScan);

            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hasher.Append(buffer.AsSpan(0, bytesRead));
            }

            return hasher.GetHashAndReset();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Computes XxHash128 of an entire file asynchronously.
    /// </summary>
    public static async Task<byte[]> ComputeFileHashAsync(
        string filePath, CancellationToken cancellationToken = default)
    {
        var hasher = new XxHash128();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            await using var stream = new FileStream(filePath,
                FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 256 * 1024,
                FileOptions.SequentialScan | FileOptions.Asynchronous);

            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                hasher.Append(buffer.AsSpan(0, bytesRead));
            }

            return hasher.GetHashAndReset();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Computes XxHash128 of a byte span (for small data or testing).
    /// </summary>
    public static byte[] ComputeHash(ReadOnlySpan<byte> data)
    {
        return XxHash128.Hash(data);
    }
}

/// <summary>
/// Incremental XxHash128 hasher for computing hash as chunks arrive. Wraps System.IO.Hashing.XxHash128 for convenience.
/// </summary>
public sealed class IncrementalHasher : IDisposable
{
    private readonly XxHash128 hasher = new();
    private bool finalized;

    public void AppendChunk(ReadOnlySpan<byte> data)
    {
        if (finalized)
        {
            throw new InvalidOperationException("Hasher already finalized");
        }

        hasher.Append(data);
    }

    public byte[] Finalize()
    {
        if (finalized)
        {
            throw new InvalidOperationException("Hasher already finalized");
        }

        finalized = true;
        return hasher.GetHashAndReset();
    }

    public void Dispose()
    {
        // XxHash128 doesn't need disposal, but this makes the pattern clean
    }
}
