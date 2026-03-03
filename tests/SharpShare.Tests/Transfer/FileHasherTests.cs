using SharpShare.Storage;
using SharpShare.Transfer;

namespace SharpShare.Tests.Transfer;

public class FileHasherTests
{
    private string testDir = null!;

    [Before(Test)]
    public void Setup()
    {
        RollingFileLogger.Shutdown();
        testDir = Path.Combine(Path.GetTempPath(), "SharpShare_HashTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(testDir);
    }

    [After(Test)]
    public void Cleanup()
    {
        if (Directory.Exists(testDir))
            Directory.Delete(testDir, recursive: true);
    }

    [Test]
    public async Task ComputeFileHash_ProducesConsistentResults()
    {
        string filePath = Path.Combine(testDir, "test.bin");
        byte[] content = new byte[1024 * 100]; // 100 KB
        Random.Shared.NextBytes(content);
        await File.WriteAllBytesAsync(filePath, content);

        byte[] hash1 = FileHasher.ComputeFileHash(filePath);
        byte[] hash2 = FileHasher.ComputeFileHash(filePath);

        await Assert.That(hash1).Count().IsEqualTo(16); // XxHash128 = 16 bytes
        await Assert.That(hash1.SequenceEqual(hash2)).IsTrue();
    }

    [Test]
    public async Task ComputeFileHashAsync_MatchesSyncVersion()
    {
        string filePath = Path.Combine(testDir, "test.bin");
        byte[] content = new byte[1024 * 512]; // 512 KB - larger than chunk size
        Random.Shared.NextBytes(content);
        await File.WriteAllBytesAsync(filePath, content);

        byte[] syncHash = FileHasher.ComputeFileHash(filePath);
        byte[] asyncHash = await FileHasher.ComputeFileHashAsync(filePath);

        await Assert.That(syncHash.SequenceEqual(asyncHash)).IsTrue();
    }

    [Test]
    public async Task ComputeHash_Span_MatchesFileHash()
    {
        byte[] content = new byte[256];
        Random.Shared.NextBytes(content);
        string filePath = Path.Combine(testDir, "small.bin");
        await File.WriteAllBytesAsync(filePath, content);

        byte[] spanHash = FileHasher.ComputeHash(content);
        byte[] fileHash = FileHasher.ComputeFileHash(filePath);

        await Assert.That(spanHash.SequenceEqual(fileHash)).IsTrue();
    }

    [Test]
    public async Task DifferentContent_ProducesDifferentHashes()
    {
        string file1 = Path.Combine(testDir, "a.bin");
        string file2 = Path.Combine(testDir, "b.bin");
        await File.WriteAllBytesAsync(file1, new byte[] { 1, 2, 3, 4 });
        await File.WriteAllBytesAsync(file2, new byte[] { 5, 6, 7, 8 });

        byte[] hash1 = FileHasher.ComputeFileHash(file1);
        byte[] hash2 = FileHasher.ComputeFileHash(file2);

        await Assert.That(hash1.SequenceEqual(hash2)).IsFalse();
    }

    [Test]
    public async Task EmptyFile_ProducesValidHash()
    {
        string filePath = Path.Combine(testDir, "empty.bin");
        await File.WriteAllBytesAsync(filePath, Array.Empty<byte>());

        byte[] hash = FileHasher.ComputeFileHash(filePath);

        await Assert.That(hash).Count().IsEqualTo(16);
    }

    [Test]
    public async Task LargeFile_HashesCorrectly()
    {
        string filePath = Path.Combine(testDir, "large.bin");
        // 1 MB - spans multiple 256KB chunks
        byte[] content = new byte[1024 * 1024];
        Random.Shared.NextBytes(content);
        await File.WriteAllBytesAsync(filePath, content);

        byte[] hash = FileHasher.ComputeFileHash(filePath);
        byte[] spanHash = FileHasher.ComputeHash(content);

        await Assert.That(hash.SequenceEqual(spanHash)).IsTrue();
    }
}

public class IncrementalHasherTests
{
    [Test]
    public async Task IncrementalHash_MatchesWholeFileHash()
    {
        byte[] content = new byte[1024 * 300]; // 300KB
        Random.Shared.NextBytes(content);

        // Compute whole hash
        byte[] wholeHash = FileHasher.ComputeHash(content);

        // Compute incrementally in 64KB chunks
        using var hasher = new IncrementalHasher();
        int offset = 0;
        int chunkSize = 64 * 1024;
        while (offset < content.Length)
        {
            int len = Math.Min(chunkSize, content.Length - offset);
            hasher.AppendChunk(content.AsSpan(offset, len));
            offset += len;
        }
        byte[] incrementalHash = hasher.Finalize();

        await Assert.That(wholeHash.SequenceEqual(incrementalHash)).IsTrue();
    }

    [Test]
    public async Task Finalize_CalledTwice_Throws()
    {
        using var hasher = new IncrementalHasher();
        hasher.AppendChunk(new byte[] { 1, 2, 3 });
        hasher.Finalize();

        await Assert.That(() => hasher.Finalize()).ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task AppendAfterFinalize_Throws()
    {
        using var hasher = new IncrementalHasher();
        hasher.Finalize();

        await Assert.That(() => hasher.AppendChunk(new byte[] { 1 })).ThrowsExactly<InvalidOperationException>();
    }
}
