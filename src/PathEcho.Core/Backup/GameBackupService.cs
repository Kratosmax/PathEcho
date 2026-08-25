using PathEcho.Core.Models;

namespace PathEcho.Core.Backup;

public sealed class GameBackupService
{
    private readonly GameBackupProfile _profile;
    private readonly string _defaultBackupDirectory;
    private readonly string _backupDirectory;
    private readonly IBackupSnapshotStore _store;
    private readonly IBackupStagingStore _stagingStore;
    private readonly BackupRetryOptions _retryOptions;
    private readonly CancellationToken _lifetimeToken;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset? _lastImportantBackupAtUtc;

    public GameBackupService(
        GameBackupProfile profile,
        string defaultBackupDirectory,
        IBackupSnapshotStore? store = null,
        IBackupStagingStore? stagingStore = null,
        BackupRetryOptions? retryOptions = null,
        CancellationToken lifetimeToken = default)
    {
        profile.Validate();
        _profile = profile;
        _defaultBackupDirectory = Path.GetFullPath(defaultBackupDirectory);
        _backupDirectory = profile.ResolveBackupDirectory(defaultBackupDirectory);
        _store = store ?? new SnapshotStore();
        _stagingStore = stagingStore ?? new BackupStagingStore();
        _retryOptions = retryOptions ?? BackupRetryOptions.Default;
        _retryOptions.Validate();
        _lifetimeToken = lifetimeToken;
    }

    public async Task<SnapshotCreationResult?> CreateAsync(
        BackupTrigger trigger,
        CancellationToken cancellationToken = default)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeToken);
        var effectiveCancellation = linkedCancellation.Token;
        await _gate.WaitAsync(effectiveCancellation).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (trigger == BackupTrigger.ImportantFileChanged &&
                _lastImportantBackupAtUtc is { } last &&
                now - last < _profile.MinimumBackupInterval)
            {
                return null;
            }

            var triggerName = GetTriggerName(trigger);
            SnapshotCreationResult result;
            try
            {
                result = await _store.CreateAsync(
                    _profile.Id,
                    _profile.SaveDirectory,
                    _backupDirectory,
                    triggerName,
                    effectiveCancellation).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRetryable(exception, effectiveCancellation))
            {
                result = await CreateFromStagingAsync(triggerName, effectiveCancellation).ConfigureAwait(false);
            }

            await RetryAsync(
                BackupRetryStage.PruningBackup,
                null,
                token => _store.PruneAsync(
                    _profile.Id,
                    _backupDirectory,
                    _profile.RetainedVersions,
                    token),
                effectiveCancellation).ConfigureAwait(false);
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

    private async Task<SnapshotCreationResult> CreateFromStagingAsync(
        string triggerName,
        CancellationToken cancellationToken)
    {
        var transactionDirectory = Path.Combine(
            _defaultBackupDirectory,
            "temp",
            _profile.Id.ToString("N"),
            Guid.NewGuid().ToString("N"));
        var stagingCompleted = false;
        var succeeded = false;
        try
        {
            var stagedSource = await RetryAsync(
                BackupRetryStage.ReadingSource,
                transactionDirectory,
                token => _stagingStore.CreateAsync(_profile.SaveDirectory, transactionDirectory, token),
                cancellationToken).ConfigureAwait(false);
            stagingCompleted = true;

            var result = await RetryAsync(
                BackupRetryStage.WritingBackup,
                transactionDirectory,
                token => _store.CreateAsync(
                    _profile.Id,
                    stagedSource,
                    _backupDirectory,
                    triggerName,
                    token),
                cancellationToken).ConfigureAwait(false);
            succeeded = true;
            return result;
        }
        finally
        {
            if (succeeded || !stagingCompleted)
            {
                _stagingStore.DeleteIfPresent(transactionDirectory);
            }
        }
    }

    private async Task<T> RetryAsync<T>(
        BackupRetryStage stage,
        string? stagingDirectory,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var failedAttempts = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRetryable(exception, cancellationToken))
            {
                failedAttempts++;
                if (failedAttempts % _retryOptions.AttemptsPerPrompt == 0)
                {
                    var prompt = new BackupRetryPrompt(
                        _profile.Name,
                        stage,
                        failedAttempts,
                        stagingDirectory,
                        exception);
                    var shouldContinue = _retryOptions.ConfirmContinueAsync is not null &&
                        await _retryOptions.ConfirmContinueAsync(prompt, cancellationToken).ConfigureAwait(false);
                    if (!shouldContinue)
                    {
                        var preservedDirectory = stage == BackupRetryStage.WritingBackup ? stagingDirectory : null;
                        var location = preservedDirectory is null ? string.Empty : $"\n临时副本：{preservedDirectory}";
                        var message = stage == BackupRetryStage.PruningBackup
                            ? $"新备份已创建，但清理旧版本连续失败 {failedAttempts} 次，已停止清理。"
                            : $"备份连续失败 {failedAttempts} 次，已停止重试。{location}";
                        throw new BackupRetryStoppedException(
                            message,
                            preservedDirectory,
                            exception);
                    }
                }

                await Task.Delay(_retryOptions.Delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsRetryable(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        exception is IOException or UnauthorizedAccessException;

    private static string GetTriggerName(BackupTrigger trigger) => trigger switch
    {
        BackupTrigger.Scheduled => "定时备份",
        BackupTrigger.ImportantFileChanged => "重点文件变动",
        BackupTrigger.ChangedFiles => "文件变动",
        BackupTrigger.ProcessExited => "游戏退出",
        _ => "手动备份",
    };
}
