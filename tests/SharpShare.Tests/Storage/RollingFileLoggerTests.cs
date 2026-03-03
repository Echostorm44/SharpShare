using System.Text.RegularExpressions;
using SharpShare.Storage;

namespace SharpShare.Tests.Storage;

[NotInParallel]
public class RollingFileLoggerTests
{
    private string tempDirectory = null!;

    [Before(Test)]
    public void SetUp()
    {
        RollingFileLogger.Shutdown(); // Ensure logger is stopped in case another test class initialized it
        tempDirectory = Path.Combine(Path.GetTempPath(), "SharpShareLogTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        RollingFileLogger.SetLogDirectory(tempDirectory);
    }

    [After(Test)]
    public void TearDown()
    {
        RollingFileLogger.Shutdown();

        if (Directory.Exists(tempDirectory))
            Directory.Delete(tempDirectory, recursive: true);
    }

    [Test]
    public async Task LogMessageFormat_MatchesExpectedPattern()
    {
        RollingFileLogger.Log(LogLevel.Info, "Hello format test");
        RollingFileLogger.Shutdown();

        string logContent = await File.ReadAllTextAsync(LogPath());
        // Expected: [2026-02-27 14:30:05.123] [INF] Hello format test
        var pattern = @"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\] \[INF\] Hello format test";

        await Assert.That(Regex.IsMatch(logContent, pattern)).IsTrue();
    }

    [Test]
    public async Task LogLevelDebug_WritesDbgPrefix()
    {
        RollingFileLogger.Log(LogLevel.Debug, "debug message");
        RollingFileLogger.Shutdown();

        string logContent = await File.ReadAllTextAsync(LogPath());
        await Assert.That(logContent).Contains("[DBG]");
    }

    [Test]
    public async Task LogLevelWarning_WritesWrnPrefix()
    {
        RollingFileLogger.Log(LogLevel.Warning, "warning message");
        RollingFileLogger.Shutdown();

        string logContent = await File.ReadAllTextAsync(LogPath());
        await Assert.That(logContent).Contains("[WRN]");
    }

    [Test]
    public async Task LogLevelError_WritesErrPrefix()
    {
        RollingFileLogger.Log(LogLevel.Error, "error message");
        RollingFileLogger.Shutdown();

        string logContent = await File.ReadAllTextAsync(LogPath());
        await Assert.That(logContent).Contains("[ERR]");
    }

    [Test]
    public async Task LogError_IncludesExceptionDetails()
    {
        var testException = new InvalidOperationException("something broke");
        RollingFileLogger.LogError("operation failed", testException);
        RollingFileLogger.Shutdown();

        string logContent = await File.ReadAllTextAsync(LogPath());
        await Assert.That(logContent).Contains("[ERR]");
        await Assert.That(logContent).Contains("operation failed");
        await Assert.That(logContent).Contains("InvalidOperationException");
        await Assert.That(logContent).Contains("something broke");
    }

    [Test]
    public async Task FileRotation_CreatesOldLogWhenSizeExceeds2MB()
    {
        string logPath = LogPath();
        string oldLogPath = OldLogPath();

        // Seed log.txt to exactly 2 MB so the next write triggers rotation
        await WritePaddedLogFile(logPath, 2 * 1024 * 1024);

        long sizeBeforeRotation = new FileInfo(logPath).Length;
        await Assert.That(sizeBeforeRotation).IsGreaterThanOrEqualTo(2 * 1024 * 1024);

        // This write should trigger rotation
        RollingFileLogger.Log(LogLevel.Info, "trigger rotation");
        RollingFileLogger.Shutdown();

        await Assert.That(File.Exists(oldLogPath)).IsTrue();
        await Assert.That(File.Exists(logPath)).IsTrue();

        // The new log.txt should be small (just the trigger message)
        long newLogSize = new FileInfo(logPath).Length;
        await Assert.That(newLogSize).IsLessThan(1024);
    }

    [Test]
    public async Task FileRotation_OldLogOverwrittenOnSubsequentRotation()
    {
        string logPath = LogPath();
        string oldLogPath = OldLogPath();

        // First rotation: seed file at 2 MB then trigger
        await WritePaddedLogFile(logPath, 2 * 1024 * 1024);

        RollingFileLogger.Log(LogLevel.Info, "first rotation marker");
        RollingFileLogger.Shutdown();

        await Assert.That(File.Exists(oldLogPath)).IsTrue();

        // Append padding so it exceeds 2 MB again, preserving the marker.
        RollingFileLogger.SetLogDirectory(tempDirectory);
        await AppendPaddingToFile(logPath, 2 * 1024 * 1024);

        RollingFileLogger.Log(LogLevel.Info, "second rotation marker");
        RollingFileLogger.Shutdown();

        string secondOldContent = await File.ReadAllTextAsync(oldLogPath);
        await Assert.That(secondOldContent).Contains("first rotation marker");
    }

    [Test]
    public async Task ConcurrentWrites_AllMessagesAreWritten()
    {
        int threadCount = 10;
        int messagesPerThread = 100;
        int expectedTotalMessages = threadCount * messagesPerThread;

        var tasks = new Task[threadCount];
        for (int i = 0; i < threadCount; i++)
        {
            int threadIndex = i;
            tasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < messagesPerThread; j++)
                {
                    RollingFileLogger.Log(LogLevel.Info, $"Thread{threadIndex}_Msg{j}");
                }
            });
        }

        await Task.WhenAll(tasks);
        RollingFileLogger.Shutdown();

        string[] logLines = await File.ReadAllLinesAsync(LogPath());
        int nonEmptyLineCount = logLines.Count(line => !string.IsNullOrWhiteSpace(line));

        await Assert.That(nonEmptyLineCount).IsEqualTo(expectedTotalMessages);
    }

    [Test]
    public async Task LogLevelInfo_WritesInfPrefix()
    {
        RollingFileLogger.Log(LogLevel.Info, "info level check");
        RollingFileLogger.Shutdown();

        string logContent = await File.ReadAllTextAsync(LogPath());
        await Assert.That(logContent).Contains("[INF]");
        await Assert.That(logContent).Contains("info level check");
    }

    private string LogPath() => Path.Combine(tempDirectory, "log.txt");
    private string OldLogPath() => Path.Combine(tempDirectory, "log.old.txt");

    private static async Task WritePaddedLogFile(string path, int targetSizeBytes)
    {
        var paddingBytes = new byte[targetSizeBytes];
        Array.Fill(paddingBytes, (byte)'X');
        for (int i = 79; i < paddingBytes.Length; i += 80)
            paddingBytes[i] = (byte)'\n';

        await File.WriteAllBytesAsync(path, paddingBytes);
    }

    private static async Task AppendPaddingToFile(string path, int targetSizeBytes)
    {
        long currentSize = File.Exists(path) ? new FileInfo(path).Length : 0;
        int bytesToAdd = (int)Math.Max(0, targetSizeBytes - currentSize);
        if (bytesToAdd == 0) return;

        var paddingBytes = new byte[bytesToAdd];
        Array.Fill(paddingBytes, (byte)'X');
        for (int i = 79; i < paddingBytes.Length; i += 80)
            paddingBytes[i] = (byte)'\n';

        await File.AppendAllTextAsync(path, System.Text.Encoding.ASCII.GetString(paddingBytes));
    }
}
