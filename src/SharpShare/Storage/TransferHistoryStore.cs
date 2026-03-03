using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpShare.Storage;

public sealed class TransferHistoryEntry
{
    public required DateTime TimestampUtc { get; init; }
    public required string FileName { get; init; }
    public required string Direction { get; init; }
    public required long FileSizeBytes { get; init; }
    public required double DurationSeconds { get; init; }
    public required double AverageSpeedBytesPerSec { get; init; }
    public required string Status { get; init; }
    public string? ErrorMessage { get; init; }
    public required string PeerAddress { get; init; }
}

[JsonSerializable(typeof(List<TransferHistoryEntry>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class TransferHistoryJsonContext : JsonSerializerContext
{
}

public static class TransferHistoryStore
{
    private const int MaxEntries = 5000;

    private static string historyFilePath = Path.Combine(
        AppContext.BaseDirectory, "transfer_history.json");

    internal static void SetHistoryFilePath(string path)
    {
        historyFilePath = path;
    }

    public static List<TransferHistoryEntry> Load()
    {
        if (!File.Exists(historyFilePath))
        {
            return new List<TransferHistoryEntry>();
        }

        try
        {
            byte[] fileBytes = File.ReadAllBytes(historyFilePath);
            var entries = JsonSerializer.Deserialize(
                fileBytes, TransferHistoryJsonContext.Default.ListTransferHistoryEntry);
            return entries ?? new List<TransferHistoryEntry>();
        }
        catch
        {
            return new List<TransferHistoryEntry>();
        }
    }

    public static void Append(TransferHistoryEntry entry)
    {
        var entries = Load();
        entries.Add(entry);

        if (entries.Count > MaxEntries)
        {
            int excessCount = entries.Count - MaxEntries;
            entries.RemoveRange(0, excessCount);
        }

        Save(entries);
    }

    public static List<TransferHistoryEntry> GetAll()
    {
        var entries = Load();
        entries.Sort((a, b) => b.TimestampUtc.CompareTo(a.TimestampUtc));
        return entries;
    }

    public static void Clear()
    {
        if (File.Exists(historyFilePath))
        {
            File.Delete(historyFilePath);
        }
    }

    private static void Save(List<TransferHistoryEntry> entries)
    {
        string? directory = Path.GetDirectoryName(historyFilePath);
        if (directory is not null && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempFilePath = $"{historyFilePath}.tmp";
        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(
            entries, TransferHistoryJsonContext.Default.ListTransferHistoryEntry);
        File.WriteAllBytes(tempFilePath, jsonBytes);
        File.Move(tempFilePath, historyFilePath, overwrite: true);
    }
}
