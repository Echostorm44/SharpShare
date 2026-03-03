using SharpShare.Models;
using SharpShare.Storage;

namespace SharpShare.Tests.Storage;

[NotInParallel]
public class SettingsManagerTests
{
    private string tempDirectory = null!;

    [Before(Test)]
    public void SetUp()
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), "SharpShareSettingsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        SettingsManager.SetSettingsDirectory(tempDirectory);
    }

    [After(Test)]
    public void TearDown()
    {
        if (Directory.Exists(tempDirectory))
            Directory.Delete(tempDirectory, recursive: true);
    }

    [Test]
    public async Task SaveAndLoad_RoundTrip_PreservesAllProperties()
    {
        var original = new UserSettings
        {
            SharedFolderPath = @"C:\Shared\Files",
            IsDarkMode = true,
            ListeningPort = 8080,
            EnableUpnp = false,
            MaxTransferSpeedMBps = 50,
            LastConnectionAddress = "192.168.1.42:9500"
        };

        SettingsManager.Save(original);
        var loaded = SettingsManager.Load();

        await Assert.That(loaded.SharedFolderPath).IsEqualTo(original.SharedFolderPath);
        await Assert.That(loaded.IsDarkMode).IsTrue();
        await Assert.That(loaded.ListeningPort).IsEqualTo(original.ListeningPort);
        await Assert.That(loaded.EnableUpnp).IsFalse();
        await Assert.That(loaded.MaxTransferSpeedMBps).IsEqualTo(original.MaxTransferSpeedMBps);
        await Assert.That(loaded.LastConnectionAddress).IsEqualTo(original.LastConnectionAddress);
    }

    [Test]
    public async Task Load_WhenFileMissing_ReturnsDefaults()
    {
        var settings = SettingsManager.Load();

        await Assert.That(settings.SharedFolderPath).IsEqualTo("");
        await Assert.That(settings.IsDarkMode).IsFalse();
        await Assert.That(settings.ListeningPort).IsEqualTo(9500);
        await Assert.That(settings.EnableUpnp).IsTrue();
        await Assert.That(settings.MaxTransferSpeedMBps).IsEqualTo(0);
        await Assert.That(settings.LastConnectionAddress).IsEqualTo("");
    }

    [Test]
    public async Task Load_WhenFileIsCorrupt_ReturnsDefaults()
    {
        string settingsFilePath = Path.Combine(tempDirectory, "settings.json");
        await File.WriteAllTextAsync(settingsFilePath, "!!!not valid json{{{");

        var settings = SettingsManager.Load();

        await Assert.That(settings.SharedFolderPath).IsEqualTo("");
        await Assert.That(settings.IsDarkMode).IsFalse();
        await Assert.That(settings.ListeningPort).IsEqualTo(9500);
        await Assert.That(settings.EnableUpnp).IsTrue();
        await Assert.That(settings.MaxTransferSpeedMBps).IsEqualTo(0);
        await Assert.That(settings.LastConnectionAddress).IsEqualTo("");
    }

    [Test]
    public async Task Save_DoesNotLeaveTempFile()
    {
        var settings = new UserSettings { SharedFolderPath = @"D:\MyFiles" };

        SettingsManager.Save(settings);

        string tempFilePath = Path.Combine(tempDirectory, "settings.json.tmp");
        await Assert.That(File.Exists(tempFilePath)).IsFalse();
    }

    [Test]
    public async Task Save_CreatesDirectoryIfMissing()
    {
        string nestedDirectory = Path.Combine(tempDirectory, "nested", "settings");
        SettingsManager.SetSettingsDirectory(nestedDirectory);

        var settings = new UserSettings { ListeningPort = 7777 };
        SettingsManager.Save(settings);

        var loaded = SettingsManager.Load();
        await Assert.That(loaded.ListeningPort).IsEqualTo(7777);
        await Assert.That(Directory.Exists(nestedDirectory)).IsTrue();
    }
}
