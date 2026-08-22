using PathEcho.Core.Models;

namespace PathEcho.Core.Backup;

public sealed class GameBackupService
{
    private readonly GameBackupProfile _profile;
    private readonly string _backupDirectory;
    private readonly SnapshotStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset? _lastImportantBackupAtUtc;

    public GameBackupService(GameBackupProfile profile, string defaultBackupDirectory, SnapshotStore? store = null)
    {
        profile.Validate();
        _profile = profile;
        _backupDirectory = profile.ResolveBackupDirectory(defaultBackupDirectory);
        _store = store ?? new SnapshotStore();
    }

    public async Task<SnapshotCreationResult?> CreateAsync(
        BackupTrigger trigger,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (trigger == BackupTrigger.ImportantFileChanged &&
                _lastImportantBackupAtUtc is { } last &&
                now - last < _profile.MinimumBackupInterval)
            {
                return null;
            }

            var result = await _store.CreateAsync(
                _profile.Id,
                _profile.SaveDirectory,
                _backupDirectory,
                GetTriggerName(trigger),
                cancellationToken).ConfigureAwait(false);
            await _store.PruneAsync(
                _profile.Id,
                _backupDirectory,
                _profile.RetainedVersions,
                cancellationToken).ConfigureAwait(false);
            if (trigger == BackupTrigger.ImportantFileChanged)
            {
                _lastImportantBackupAtUtc = now;
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string GetTriggerName(BackupTrigger trigger) => trigger switch
    {
        BackupTrigger.Scheduled => "定时备份",
        BackupTrigger.ImportantFileChanged => "重点文件变动",
        BackupTrigger.ChangedFiles => "文件变动",
        BackupTrigger.ProcessExited => "游戏退出",
        _ => "手动备份",
    };
}
