namespace PathEcho.Core.Backup;

public interface IBackupSnapshotStore
{
    Task<SnapshotCreationResult> CreateAsync(
        Guid profileId,
        string sourceDirectory,
        string backupDirectory,
        string trigger,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? changedPaths = null);

    Task<int> PruneAsync(
        Guid profileId,
        string backupDirectory,
        int retainedVersions,
        CancellationToken cancellationToken = default);
}
