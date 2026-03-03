using SharpShare.Storage;

namespace SharpShare.Tests.Storage;

public class SharedFolderWatcherTests
{
    private string tempDirectory = null!;

    [Before(Test)]
    public void SetUp()
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), "SharpShareWatcherTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
    }

    [After(Test)]
    public void TearDown()
    {
        if (Directory.Exists(tempDirectory))
            Directory.Delete(tempDirectory, recursive: true);
    }

    [Test]
    public async Task EnumerateFiles_MultipleFilesAcrossSubdirectories_ReturnsAll()
    {
        string subDir = Path.Combine(tempDirectory, "movies");
        Directory.CreateDirectory(subDir);

        File.WriteAllText(Path.Combine(tempDirectory, "readme.txt"), "hello");
        File.WriteAllText(Path.Combine(subDir, "clip.mp4"), "video data");
        File.WriteAllText(Path.Combine(subDir, "poster.jpg"), "image data");

        using var watcher = new SharedFolderWatcher(tempDirectory);
        var entries = watcher.EnumerateFiles();

        await Assert.That(entries.Count).IsEqualTo(3);
    }

    [Test]
    public async Task EnumerateFiles_RelativePathsAreCorrect()
    {
        string subDir = Path.Combine(tempDirectory, "docs");
        Directory.CreateDirectory(subDir);

        File.WriteAllText(Path.Combine(subDir, "notes.txt"), "content");

        using var watcher = new SharedFolderWatcher(tempDirectory);
        var entries = watcher.EnumerateFiles();

        await Assert.That(entries.Count).IsEqualTo(1);

        string expectedRelative = Path.Combine("docs", "notes.txt");
        await Assert.That(entries[0].RelativePath).IsEqualTo(expectedRelative);
    }

    [Test]
    public async Task EnumerateFiles_EmptyFolder_ReturnsEmptyList()
    {
        using var watcher = new SharedFolderWatcher(tempDirectory);
        var entries = watcher.EnumerateFiles();

        await Assert.That(entries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task EnumerateFiles_CapturesFileSizeAndLastModified()
    {
        string filePath = Path.Combine(tempDirectory, "data.bin");
        byte[] content = new byte[1234];
        File.WriteAllBytes(filePath, content);

        using var watcher = new SharedFolderWatcher(tempDirectory);
        var entries = watcher.EnumerateFiles();

        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].SizeBytes).IsEqualTo(1234);
        await Assert.That(entries[0].LastModifiedUtc).IsNotEqualTo(default(DateTime));
    }

    [Test]
    public async Task IsPathSafe_RejectsParentDirectoryTraversal()
    {
        await Assert.That(SharedFolderWatcher.IsPathSafe(@"..\secret.txt")).IsFalse();
        await Assert.That(SharedFolderWatcher.IsPathSafe(@"subdir\..\..\secret.txt")).IsFalse();
        await Assert.That(SharedFolderWatcher.IsPathSafe("../etc/passwd")).IsFalse();
    }

    [Test]
    public async Task IsPathSafe_RejectsAbsolutePathWithDriveLetter()
    {
        await Assert.That(SharedFolderWatcher.IsPathSafe(@"C:\windows\system32\cmd.exe")).IsFalse();
        await Assert.That(SharedFolderWatcher.IsPathSafe(@"D:\data\file.txt")).IsFalse();
    }

    [Test]
    public async Task IsPathSafe_RejectsAbsolutePathWithLeadingSlash()
    {
        await Assert.That(SharedFolderWatcher.IsPathSafe("/etc/passwd")).IsFalse();
        await Assert.That(SharedFolderWatcher.IsPathSafe(@"\windows\system32")).IsFalse();
    }

    [Test]
    public async Task IsPathSafe_RejectsNullBytes()
    {
        await Assert.That(SharedFolderWatcher.IsPathSafe("file\0.txt")).IsFalse();
    }

    [Test]
    public async Task IsPathSafe_AcceptsValidRelativePaths()
    {
        await Assert.That(SharedFolderWatcher.IsPathSafe("movies/file.mkv")).IsTrue();
        await Assert.That(SharedFolderWatcher.IsPathSafe(@"docs\readme.txt")).IsTrue();
        await Assert.That(SharedFolderWatcher.IsPathSafe("photo.jpg")).IsTrue();
        await Assert.That(SharedFolderWatcher.IsPathSafe("a/b/c/d.txt")).IsTrue();
    }

    [Test]
    public async Task GetFullPath_ThrowsOnPathTraversalAttempt()
    {
        await Assert.That(() => SharedFolderWatcher.GetFullPath(tempDirectory, @"..\..\windows\system32"))
            .ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task GetFullPath_ReturnsResolvedPathForValidInput()
    {
        string result = SharedFolderWatcher.GetFullPath(tempDirectory, @"movies\clip.mp4");

        string expected = Path.Combine(tempDirectory, "movies", "clip.mp4");
        await Assert.That(result).IsEqualTo(expected);
    }
}
