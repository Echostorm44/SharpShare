using SharpShare.Network;
using SharpShare.Storage;
using SharpShare.Transfer;

namespace SharpShare.Tests.Transfer;

public class ReceiveContextTests
{
    private string testDir = null!;

    [Before(Test)]
    public void Setup()
    {
        RollingFileLogger.Shutdown();
        testDir = Path.Combine(Path.GetTempPath(), "SharpShare_RcvTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(testDir);
    }

    [After(Test)]
    public void Cleanup()
    {
        if (Directory.Exists(testDir))
            Directory.Delete(testDir, recursive: true);
    }

    [Test]
    public async Task ReceiveFile_WritesAndVerifiesHash()
    {
        // Prepare source data
        byte[] fileData = new byte[1024];
        Random.Shared.NextBytes(fileData);
        byte[] expectedHash = FileHasher.ComputeHash(fileData);

        string relativePath = "received.bin";
        string finalPath = Path.Combine(testDir, relativePath);
        string tempPath = finalPath + ".tmp";

        var progress = new TransferProgressState
        {
            TransferId = 1,
            FileName = "received.bin",
            RelativePath = relativePath,
            TotalBytes = fileData.Length,
            Direction = TransferDirection.Download,
        };

        // Simulate receiving in chunks
        using var ctx = new ReceiveContext(1, relativePath, finalPath, tempPath,
            fileData.Length, 0, progress);

        int chunkSize = 256;
        for (int offset = 0; offset < fileData.Length; offset += chunkSize)
        {
            int len = Math.Min(chunkSize, fileData.Length - offset);
            byte[] chunkData = new byte[len];
            Array.Copy(fileData, offset, chunkData, 0, len);

            var chunk = new FileChunkMessage(1, offset, len, chunkData);
            await ctx.ProcessChunkAsync(chunk);
        }

        bool success = await ctx.FinalizeAsync(expectedHash);

        await Assert.That(success).IsTrue();
        await Assert.That(File.Exists(finalPath)).IsTrue();
        await Assert.That(File.Exists(tempPath)).IsFalse();

        byte[] savedContent = await File.ReadAllBytesAsync(finalPath);
        await Assert.That(savedContent.SequenceEqual(fileData)).IsTrue();
        await Assert.That(progress.Status).IsEqualTo(TransferStatus.Complete);
    }

    [Test]
    public async Task ReceiveFile_HashMismatch_FailsAndDeletesTemp()
    {
        byte[] fileData = new byte[512];
        Random.Shared.NextBytes(fileData);
        byte[] wrongHash = new byte[16]; // all zeros = wrong hash

        string relativePath = "bad.bin";
        string finalPath = Path.Combine(testDir, relativePath);
        string tempPath = finalPath + ".tmp";

        var progress = new TransferProgressState
        {
            TransferId = 2,
            FileName = "bad.bin",
            RelativePath = relativePath,
            TotalBytes = fileData.Length,
            Direction = TransferDirection.Download,
        };

        using var ctx = new ReceiveContext(2, relativePath, finalPath, tempPath,
            fileData.Length, 0, progress);

        var chunk = new FileChunkMessage(2, 0, fileData.Length, fileData);
        await ctx.ProcessChunkAsync(chunk);

        bool success = await ctx.FinalizeAsync(wrongHash);

        await Assert.That(success).IsFalse();
        await Assert.That(File.Exists(finalPath)).IsFalse();
        await Assert.That(File.Exists(tempPath)).IsFalse();
        await Assert.That(progress.Status).IsEqualTo(TransferStatus.Failed);
        await Assert.That(progress.ErrorMessage).Contains("Hash mismatch");
    }

    [Test]
    public async Task ReceiveFile_Resume_ContinuesFromExistingTemp()
    {
        byte[] fullData = new byte[1024];
        Random.Shared.NextBytes(fullData);
        byte[] expectedHash = FileHasher.ComputeHash(fullData);

        string relativePath = "resumed.bin";
        string finalPath = Path.Combine(testDir, relativePath);
        string tempPath = finalPath + ".tmp";

        // Write first half to temp file (simulating interrupted transfer)
        int halfLength = 512;
        await File.WriteAllBytesAsync(tempPath, fullData.AsSpan(0, halfLength).ToArray());

        var progress = new TransferProgressState
        {
            TransferId = 3,
            FileName = "resumed.bin",
            RelativePath = relativePath,
            TotalBytes = fullData.Length,
            Direction = TransferDirection.Download,
        };

        // Resume from existing temp file
        using var ctx = new ReceiveContext(3, relativePath, finalPath, tempPath,
            fullData.Length, halfLength, progress);

        // BytesTransferred should already reflect existing data
        await Assert.That(Interlocked.Read(ref progress.BytesTransferred)).IsEqualTo(halfLength);

        // Send remaining chunks
        int chunkSize = 128;
        for (int offset = halfLength; offset < fullData.Length; offset += chunkSize)
        {
            int len = Math.Min(chunkSize, fullData.Length - offset);
            byte[] chunkData = new byte[len];
            Array.Copy(fullData, offset, chunkData, 0, len);

            var chunk = new FileChunkMessage(3, offset, len, chunkData);
            await ctx.ProcessChunkAsync(chunk);
        }

        bool success = await ctx.FinalizeAsync(expectedHash);

        await Assert.That(success).IsTrue();
        await Assert.That(File.Exists(finalPath)).IsTrue();

        byte[] savedContent = await File.ReadAllBytesAsync(finalPath);
        await Assert.That(savedContent.SequenceEqual(fullData)).IsTrue();
    }

    [Test]
    public async Task Cancel_KeepsTempFileForResume()
    {
        byte[] fileData = new byte[256];
        Random.Shared.NextBytes(fileData);

        string relativePath = "cancelled.bin";
        string finalPath = Path.Combine(testDir, relativePath);
        string tempPath = finalPath + ".tmp";

        var progress = new TransferProgressState
        {
            TransferId = 4,
            FileName = "cancelled.bin",
            RelativePath = relativePath,
            TotalBytes = fileData.Length,
            Direction = TransferDirection.Download,
        };

        var ctx = new ReceiveContext(4, relativePath, finalPath, tempPath,
            fileData.Length, 0, progress);

        // Write some data
        var chunk = new FileChunkMessage(4, 0, 128, fileData.AsSpan(0, 128).ToArray());
        await ctx.ProcessChunkAsync(chunk);

        // Cancel
        ctx.Cancel();

        await Assert.That(progress.Status).IsEqualTo(TransferStatus.Cancelled);
        // Temp file should still exist for potential resume
        await Assert.That(File.Exists(tempPath)).IsTrue();
    }

    [Test]
    public async Task Fail_SetsErrorMessage()
    {
        string relativePath = "failed.bin";
        string finalPath = Path.Combine(testDir, relativePath);
        string tempPath = finalPath + ".tmp";

        var progress = new TransferProgressState
        {
            TransferId = 5,
            FileName = "failed.bin",
            RelativePath = relativePath,
            TotalBytes = 100,
            Direction = TransferDirection.Download,
        };

        var ctx = new ReceiveContext(5, relativePath, finalPath, tempPath,
            100, 0, progress);

        ctx.Fail("Connection lost");

        await Assert.That(progress.Status).IsEqualTo(TransferStatus.Failed);
        await Assert.That(progress.ErrorMessage).IsEqualTo("Connection lost");
    }

    [Test]
    public async Task ProgressUpdates_ReflectBytesReceived()
    {
        byte[] fileData = new byte[1024];
        Random.Shared.NextBytes(fileData);

        string relativePath = "progress.bin";
        string finalPath = Path.Combine(testDir, relativePath);
        string tempPath = finalPath + ".tmp";

        var progress = new TransferProgressState
        {
            TransferId = 6,
            FileName = "progress.bin",
            RelativePath = relativePath,
            TotalBytes = 1024,
            Direction = TransferDirection.Download,
        };

        using var ctx = new ReceiveContext(6, relativePath, finalPath, tempPath,
            1024, 0, progress);

        // Send half the data
        var chunk = new FileChunkMessage(6, 0, 512, fileData.AsSpan(0, 512).ToArray());
        await ctx.ProcessChunkAsync(chunk);

        long transferred = Interlocked.Read(ref progress.BytesTransferred);
        await Assert.That(transferred).IsEqualTo(512);
        await Assert.That(progress.ProgressFraction).IsEqualTo(0.5);
    }
}
