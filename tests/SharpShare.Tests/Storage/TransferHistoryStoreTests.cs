using SharpShare.Storage;

namespace SharpShare.Tests.Storage;

[NotInParallel]
public class TransferHistoryStoreTests
{
    private string tempDirectory = null!;
    private string historyFilePath = null!;

    [Before(Test)]
    public void SetUp()
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), "SharpShareHistoryTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        historyFilePath = Path.Combine(tempDirectory, "transfer_history.json");
        TransferHistoryStore.SetHistoryFilePath(historyFilePath);
    }

    [After(Test)]
    public void TearDown()
    {
        if (Directory.Exists(tempDirectory))
            Directory.Delete(tempDirectory, recursive: true);
    }

    private static TransferHistoryEntry CreateEntry(DateTime timestampUtc, string fileName = "test.txt") => new()
    {
        TimestampUtc = timestampUtc,
        FileName = fileName,
        Direction = "Upload",
        FileSizeBytes = 1024,
        DurationSeconds = 1.5,
        AverageSpeedBytesPerSec = 682.67,
        Status = "Complete",
        PeerAddress = "192.168.1.10:9500"
    };

    [Test]
    public async Task AppendAndLoad_RoundTrip_PreservesEntry()
    {
        var entry = CreateEntry(new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc), "photo.jpg");

        TransferHistoryStore.Append(entry);
        var loaded = TransferHistoryStore.Load();

        await Assert.That(loaded).Count().IsEqualTo(1);
        await Assert.That(loaded[0].FileName).IsEqualTo("photo.jpg");
        await Assert.That(loaded[0].Direction).IsEqualTo("Upload");
        await Assert.That(loaded[0].FileSizeBytes).IsEqualTo(1024);
        await Assert.That(loaded[0].DurationSeconds).IsEqualTo(1.5);
        await Assert.That(loaded[0].Status).IsEqualTo("Complete");
        await Assert.That(loaded[0].PeerAddress).IsEqualTo("192.168.1.10:9500");
    }

    [Test]
    public async Task Append_EnforcesMaxOf5000Entries()
    {
        var baseTime = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (int i = 0; i < 5001; i++)
        {
            var entry = CreateEntry(baseTime.AddSeconds(i), $"file_{i:D5}.txt");
            TransferHistoryStore.Append(entry);
        }

        var allEntries = TransferHistoryStore.Load();

        await Assert.That(allEntries).Count().IsEqualTo(5000);
        await Assert.That(allEntries[0].FileName).IsEqualTo("file_00001.txt");
        await Assert.That(allEntries[^1].FileName).IsEqualTo("file_05000.txt");
    }

    [Test]
    public async Task Load_WhenFileMissing_ReturnsEmptyList()
    {
        var entries = TransferHistoryStore.Load();

        await Assert.That(entries).Count().IsEqualTo(0);
    }

    [Test]
    public async Task Load_WhenFileIsCorrupt_ReturnsEmptyList()
    {
        await File.WriteAllTextAsync(historyFilePath, "!!!not valid json{{{");

        var entries = TransferHistoryStore.Load();

        await Assert.That(entries).Count().IsEqualTo(0);
    }

    [Test]
    public async Task Clear_RemovesHistoryFile()
    {
        TransferHistoryStore.Append(CreateEntry(DateTime.UtcNow));
        await Assert.That(File.Exists(historyFilePath)).IsTrue();

        TransferHistoryStore.Clear();

        await Assert.That(File.Exists(historyFilePath)).IsFalse();
    }

    [Test]
    public async Task GetAll_ReturnsMostRecentFirst()
    {
        var oldest = CreateEntry(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), "oldest.txt");
        var middle = CreateEntry(new DateTime(2025, 6, 15, 0, 0, 0, DateTimeKind.Utc), "middle.txt");
        var newest = CreateEntry(new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc), "newest.txt");

        TransferHistoryStore.Append(oldest);
        TransferHistoryStore.Append(middle);
        TransferHistoryStore.Append(newest);

        var allEntries = TransferHistoryStore.GetAll();

        await Assert.That(allEntries).Count().IsEqualTo(3);
        await Assert.That(allEntries[0].FileName).IsEqualTo("newest.txt");
        await Assert.That(allEntries[1].FileName).IsEqualTo("middle.txt");
        await Assert.That(allEntries[2].FileName).IsEqualTo("oldest.txt");
    }
}
