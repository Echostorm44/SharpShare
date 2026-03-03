using System.Globalization;
using System.Threading.Channels;

namespace SharpShare.Storage;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

public static class RollingFileLogger
{
    private const int MaxLogFileSizeBytes = 2 * 1024 * 1024; // 2 MB
    private const int ChannelCapacity = 1000;

    private static string logDirectory = AppContext.BaseDirectory;
    private static string logFilePath = Path.Combine(logDirectory, "log.txt");
    private static string oldLogFilePath = Path.Combine(logDirectory, "log.old.txt");

    private static Channel<string>? messageChannel;
    private static Task? writerTask;
    private static readonly object initLock = new();
    private static volatile bool initialized;

    /// <summary>
    /// Overrides the log directory. Must be called before the first Log call or after Shutdown. Exposed as internal for
    /// test isolation.
    /// </summary>
    internal static void SetLogDirectory(string directory)
    {
        lock (initLock)
        {
            if (initialized)
            {
                throw new InvalidOperationException("Cannot change log directory while logger is running. Call Shutdown() first.");
            }

            logDirectory = directory;
            logFilePath = Path.Combine(directory, "log.txt");
            oldLogFilePath = Path.Combine(directory, "log.old.txt");
        }
    }

    private static void EnsureInitialized()
    {
        if (initialized)
        {
            return;
        }

        lock (initLock)
        {
            if (initialized)
            {
                return;
            }

            var channelOptions = new BoundedChannelOptions(ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            };

            messageChannel = Channel.CreateBounded<string>(channelOptions);
            writerTask = Task.Run(ProcessWriteQueue);
            initialized = true;
        }
    }

    public static void Log(LogLevel level, string message)
    {
        EnsureInitialized();

        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        string levelTag = level switch
        {
            LogLevel.Debug => "DEBUG",
            LogLevel.Info => "INFO ",
            LogLevel.Warning => "WARN ",
            LogLevel.Error => "ERROR",
            _ => "WHAT?"
        };

        string formattedLine = $"[{timestamp}] [{levelTag}] {message}";

        // Fire-and-forget write to channel; callers don't await
        messageChannel!.Writer.TryWrite(formattedLine);
    }

    public static void LogError(string message, Exception? exception = null)
    {
        string fullMessage = exception is null
            ? message
            : $"{message} | {exception.GetType().Name}: {exception.Message}";

        Log(LogLevel.Error, fullMessage);
    }

    /// <summary>
    /// Completes the write channel and waits for all queued messages to flush to disk.
    /// </summary>
    public static void Shutdown()
    {
        lock (initLock)
        {
            if (!initialized)
            {
                return;
            }

            initialized = false;

            messageChannel!.Writer.TryComplete();
            writerTask!.GetAwaiter().GetResult();

            messageChannel = null;
            writerTask = null;
        }
    }

    private static async Task ProcessWriteQueue()
    {
        var reader = messageChannel!.Reader;

        await foreach (string line in reader.ReadAllAsync())
        {
            RollFileIfNeeded();
            await File.AppendAllTextAsync(logFilePath, $"{line}{Environment.NewLine}");
        }
    }

    private static void RollFileIfNeeded()
    {
        if (!File.Exists(logFilePath))
        {
            return;
        }

        var fileInfo = new FileInfo(logFilePath);
        if (fileInfo.Length < MaxLogFileSizeBytes)
        {
            return;
        }

        // Overwrite old log, then start fresh
        File.Copy(logFilePath, oldLogFilePath, overwrite: true);
        File.Delete(logFilePath);
    }

    internal static string LogFilePath => logFilePath;
    internal static string OldLogFilePath => oldLogFilePath;
}
