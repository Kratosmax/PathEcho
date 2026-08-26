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

public sealed record SnapshotRetentionPolicy(
    int RecentVersions,
    int HourlyVersions,
    int DailyVersions)
{
    public void Validate()
    {
        if (RecentVersions < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(RecentVersions), "至少保留一个最近版本。");
        }

        if (HourlyVersions < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(HourlyVersions), "每小时锚点数量不能小于零。");
        }

        if (DailyVersions < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(DailyVersions), "每日锚点数量不能小于零。");
        }
    }
}

public static class SnapshotRetentionPlanner
{
    public static IReadOnlySet<string> Select(
        IReadOnlyCollection<SnapshotVersion> versions,
        SnapshotRetentionPolicy policy)
    {
        policy.Validate();
        var ordered = versions
            .OrderByDescending(version => version.Manifest.CreatedAtUtc)
            .ToArray();
        var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        retained.UnionWith(ordered
            .Take(policy.RecentVersions)
            .Select(version => version.SnapshotDirectory));
        retained.UnionWith(SelectAnchors(
            ordered,
            policy.HourlyVersions,
            createdAt => (createdAt.UtcDateTime.Year, createdAt.UtcDateTime.Month, createdAt.UtcDateTime.Day, createdAt.UtcDateTime.Hour)));
        retained.UnionWith(SelectAnchors(
            ordered,
            policy.DailyVersions,
            createdAt => (createdAt.UtcDateTime.Year, createdAt.UtcDateTime.Month, createdAt.UtcDateTime.Day)));
        return retained;
    }

    private static IEnumerable<string> SelectAnchors<TKey>(
        IReadOnlyCollection<SnapshotVersion> versions,
        int maximumBuckets,
        Func<DateTimeOffset, TKey> bucketSelector)
        where TKey : notnull
    {
        if (maximumBuckets == 0)
        {
            return Array.Empty<string>();
        }

        return versions
            .GroupBy(version => bucketSelector(version.Manifest.CreatedAtUtc))
            .Take(maximumBuckets)
            .Select(group => group.First().SnapshotDirectory);
    }
}
