using SharpShare.Storage;
using SharpShare.Transfer;

namespace SharpShare.Tests.Transfer;

public class TransferQueueTests
{
    [Before(Test)]
    public void Setup()
    {
        RollingFileLogger.Shutdown();
    }

    [Test]
    public async Task EnqueueDownload_IncreasesQueuedCount()
    {
        var queue = new TransferQueue();
        queue.EnqueueDownload("movie.mp4", 1_000_000);

        await Assert.That(queue.QueuedCount).IsEqualTo(1);
    }

    [Test]
    public async Task EnqueueMultiple_QueuesAll()
    {
        var queue = new TransferQueue();
        queue.EnqueueDownload("a.mp4", 100);
        queue.EnqueueDownload("b.mp4", 200);
        queue.EnqueueDownload("c.mp4", 300);

        await Assert.That(queue.QueuedCount).IsEqualTo(3);
    }

    [Test]
    public async Task ProcessQueue_CallsDownloadFuncForEachItem()
    {
        var queue = new TransferQueue();
        queue.EnqueueDownload("file1.txt", 100);
        queue.EnqueueDownload("file2.txt", 200);

        var downloadedFiles = new List<string>();
        uint nextId = 1;

        await queue.ProcessQueueAsync(
            downloadFunc: (path, size, ct) =>
            {
                downloadedFiles.Add(path);
                uint id = nextId++;
                var progress = new TransferProgressState
                {
                    TransferId = id,
                    FileName = Path.GetFileName(path),
                    RelativePath = path,
                    TotalBytes = size,
                    Direction = TransferDirection.Download,
                    Status = TransferStatus.Active,
                };
                progress.Status = TransferStatus.Complete;
                return Task.FromResult((id, progress));
            },
            waitForCompletionFunc: id => Task.CompletedTask);

        await Assert.That(downloadedFiles).Count().IsEqualTo(2);
        await Assert.That(downloadedFiles[0]).IsEqualTo("file1.txt");
        await Assert.That(downloadedFiles[1]).IsEqualTo("file2.txt");
    }

    [Test]
    public async Task ProcessQueue_FiresTransferStartedEvent()
    {
        var queue = new TransferQueue();
        queue.EnqueueDownload("movie.mp4", 5000);

        var startedTransfers = new List<TransferProgressState>();
        queue.TransferStarted += state => startedTransfers.Add(state);

        await queue.ProcessQueueAsync(
            downloadFunc: (path, size, ct) =>
            {
                var progress = new TransferProgressState
                {
                    TransferId = 1,
                    FileName = Path.GetFileName(path),
                    RelativePath = path,
                    TotalBytes = size,
                    Direction = TransferDirection.Download,
                    Status = TransferStatus.Complete,
                };
                return Task.FromResult((1u, progress));
            },
            waitForCompletionFunc: id => Task.CompletedTask);

        await Assert.That(startedTransfers).Count().IsEqualTo(1);
        await Assert.That(startedTransfers[0].FileName).IsEqualTo("movie.mp4");
    }

    [Test]
    public async Task TrackUpload_CreatesActiveTransfer()
    {
        var queue = new TransferQueue();
        var progress = queue.TrackUpload(42, "photos/pic.jpg", 50000);

        await Assert.That(progress.TransferId).IsEqualTo(42u);
        await Assert.That(progress.Direction).IsEqualTo(TransferDirection.Upload);
        await Assert.That(progress.Status).IsEqualTo(TransferStatus.Active);
        await Assert.That(queue.ActiveTransfers.Count).IsEqualTo(1);
    }

    [Test]
    public async Task CompleteUpload_MovesToCompleted()
    {
        var queue = new TransferQueue();
        var progress = queue.TrackUpload(42, "file.bin", 1000);
        progress.Status = TransferStatus.Complete;

        bool completedFired = false;
        queue.TransferCompleted += _ => completedFired = true;

        queue.CompleteUpload(42);

        await Assert.That(queue.ActiveTransfers.Count).IsEqualTo(0);
        await Assert.That(completedFired).IsTrue();
    }

    [Test]
    public async Task CancelAll_ClearsPendingAndMarksActiveCancelled()
    {
        var queue = new TransferQueue();
        queue.EnqueueDownload("file1.txt", 100);
        queue.EnqueueDownload("file2.txt", 200);
        var upload = queue.TrackUpload(1, "upload.bin", 300);

        queue.CancelAll();

        await Assert.That(queue.QueuedCount).IsEqualTo(0);
        await Assert.That(upload.Status).IsEqualTo(TransferStatus.Cancelled);
    }

    [Test]
    public async Task IsProcessing_FalseWhenIdle()
    {
        var queue = new TransferQueue();
        await Assert.That(queue.IsProcessing).IsFalse();
    }
}
