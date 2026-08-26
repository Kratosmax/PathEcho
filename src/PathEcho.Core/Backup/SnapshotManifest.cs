namespace PathEcho.Core.Backup;

public sealed record SnapshotManifest
{
    public int SchemaVersion { get; init; } = 2;

    public required Guid ProfileId { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required string Trigger { get; init; }

    public required IReadOnlyList<SnapshotFileEntry> Files { get; init; }
}

public sealed record SnapshotFileEntry
{
    public required string RelativePath { get; init; }

    public required string Sha256 { get; init; }

    public required long Length { get; init; }

    public required long LastWriteUtcTicks { get; init; }
}

public sealed record SnapshotCreationResult(
    string SnapshotDirectory,
    int FileCount,
    int NewObjectCount,
    int ReusedObjectCount,
    int HardLinkedFileCount,
    int CopiedViewFileCount);

public sealed record SnapshotVersion(string SnapshotDirectory, SnapshotManifest Manifest);
