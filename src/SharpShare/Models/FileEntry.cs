namespace SharpShare.Models;

public readonly struct FileEntry
{
    public required string RelativePath { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTime LastModifiedUtc { get; init; }
}
