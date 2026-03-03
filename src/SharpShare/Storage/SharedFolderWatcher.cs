using SharpShare.Models;

namespace SharpShare.Storage;

public sealed class SharedFolderWatcher : IDisposable
{
    private readonly string sharedFolderPath;
    private readonly FileSystemWatcher? fileSystemWatcher;
    private Timer? debounceTimer;
    private readonly object debounceLock = new();
    private bool disposed;

    private const int DebounceDelayMs = 2000;

    public event Action? FilesChanged;

    public SharedFolderWatcher(string folderPath)
    {
        sharedFolderPath = Path.GetFullPath(folderPath);

        if (!Directory.Exists(sharedFolderPath))
        {
            Directory.CreateDirectory(sharedFolderPath);
        }

        try
        {
            fileSystemWatcher = new FileSystemWatcher(sharedFolderPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                             | NotifyFilters.DirectoryName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            fileSystemWatcher.Created += OnFileSystemChange;
            fileSystemWatcher.Changed += OnFileSystemChange;
            fileSystemWatcher.Deleted += OnFileSystemChange;
            fileSystemWatcher.Renamed += OnFileSystemRenamed;
        }
        catch (Exception ex)
        {
            RollingFileLogger.Log(LogLevel.Warning, $"FileSystemWatcher could not be started: {ex.Message}");
            fileSystemWatcher = null;
        }
    }

    public List<FileEntry> EnumerateFiles()
    {
        var entries = new List<FileEntry>();

        if (!Directory.Exists(sharedFolderPath))
        {
            return entries;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(sharedFolderPath, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            RollingFileLogger.Log(LogLevel.Warning, $"Failed to enumerate shared folder: {ex.Message}");
            return entries;
        }

        foreach (string absolutePath in files)
        {
            try
            {
                var fileInfo = new FileInfo(absolutePath);
                string relativePath = Path.GetRelativePath(sharedFolderPath, absolutePath);

                entries.Add(new FileEntry
                {
                    RelativePath = relativePath,
                    SizeBytes = fileInfo.Length,
                    LastModifiedUtc = fileInfo.LastWriteTimeUtc
                });
            }
            catch (Exception ex)
            {
                RollingFileLogger.Log(LogLevel.Warning, $"Skipping unreadable file '{absolutePath}': {ex.Message}");
            }
        }

        return entries;
    }

    /// <summary>
    /// Returns true if the relative path is safe (no traversal, no absolute paths, no null bytes). Use this to validate
    /// paths received from a remote peer before touching the filesystem.
    /// </summary>
    public static bool IsPathSafe(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return false;
        }

        if (relativePath.Contains('\0'))
        {
            return false;
        }

        // Reject absolute paths: drive letters (e.g. "C:"), leading slash or backslash
        if (relativePath.Length >= 2 && relativePath[1] == ':')
        {
            return false;
        }

        if (relativePath[0] is '/' or '\\')
        {
            return false;
        }

        // Reject ".." segments in any slash style
        string[] segments = relativePath.Split('/', '\\');
        foreach (string segment in segments)
        {
            if (segment == "..")
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Resolves a validated relative path against the shared folder root. Throws if the resolved path escapes the
    /// shared folder.
    /// </summary>
    public static string GetFullPath(string sharedFolderRoot, string relativePath)
    {
        string normalizedRoot = Path.GetFullPath(sharedFolderRoot);
        if (!normalizedRoot.EndsWith(Path.DirectorySeparatorChar))
        {
            normalizedRoot += Path.DirectorySeparatorChar;
        }

        string resolvedPath = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));

        if (!resolvedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Path traversal detected: '{relativePath}' escapes the shared folder.");
        }

        return resolvedPath;
    }

    private void OnFileSystemChange(object sender, FileSystemEventArgs e) => ResetDebounceTimer();

    private void OnFileSystemRenamed(object sender, RenamedEventArgs e) => ResetDebounceTimer();

    private void ResetDebounceTimer()
    {
        lock (debounceLock)
        {
            if (disposed)
            {
                return;
            }

            debounceTimer?.Dispose();
            debounceTimer = new Timer(OnDebounceElapsed, null, DebounceDelayMs, Timeout.Infinite);
        }
    }

    private void OnDebounceElapsed(object? state)
    {
        // Fire on a ThreadPool thread (Timer callbacks already run on the pool, not the FSW thread)
        FilesChanged?.Invoke();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        fileSystemWatcher?.Dispose();

        lock (debounceLock)
        {
            debounceTimer?.Dispose();
            debounceTimer = null;
        }
    }
}
