using PathEcho.Core.Models;

namespace PathEcho.Core.Sync;

public sealed class SyncEngine
{
    private readonly DirectoryScanner _scanner = new();
    private readonly SyncPlanner _planner = new();
    private readonly DeletionVault _deletionVault;
    private readonly SemaphoreSlim _runGate = new(1, 1);

    public SyncEngine(string deletionVaultRoot)
    {
        _deletionVault = new DeletionVault(deletionVaultRoot);
    }

    public Task<SyncRunResult> RunAsync(
        SyncTaskDefinition task,
        SyncBaseline baseline,
        CancellationToken cancellationToken = default) =>
        RunAsync(task, baseline, false, cancellationToken);

    public async Task<SyncRunResult> RunAsync(
        SyncTaskDefinition task,
        SyncBaseline baseline,
        bool forceFullScan,
        CancellationToken cancellationToken = default)
    {
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        task.Validate();
        var leftRoot = SyncTaskDefinition.NormalizeRoot(task.LeftPath);
        var rightRoot = SyncTaskDefinition.NormalizeRoot(task.RightPath);
        Directory.CreateDirectory(leftRoot);
        Directory.CreateDirectory(rightRoot);
        if (forceFullScan)
        {
            _scanner.Invalidate(leftRoot);
            _scanner.Invalidate(rightRoot);
        }

        var left = await _scanner.ScanAsync(leftRoot, cancellationToken).ConfigureAwait(false);
        var right = await _scanner.ScanAsync(rightRoot, cancellationToken).ConfigureAwait(false);
        var plan = _planner.CreatePlan(task, left, right, baseline);
        var copied = 0;
        var deleted = 0;
        var conflicts = 0;

        foreach (var action in plan.Actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var leftPath = SafeCombine(leftRoot, action.RelativePath);
            var rightPath = SafeCombine(rightRoot, action.RelativePath);
            switch (action.Kind)
            {
                case SyncActionKind.CopyLeftToRight:
                    await AtomicFileOperations.CopyAsync(leftPath, rightPath, cancellationToken).ConfigureAwait(false);
                    copied++;
                    break;
                case SyncActionKind.CopyRightToLeft:
                    await AtomicFileOperations.CopyAsync(rightPath, leftPath, cancellationToken).ConfigureAwait(false);
                    copied++;
                    break;
                case SyncActionKind.DeleteLeft:
                    await DeleteAsync(task, action.RelativePath, leftPath, cancellationToken).ConfigureAwait(false);
                    deleted++;
                    break;
                case SyncActionKind.DeleteRight:
                    await DeleteAsync(task, action.RelativePath, rightPath, cancellationToken).ConfigureAwait(false);
                    deleted++;
                    break;
                case SyncActionKind.KeepBothConflict:
                    await PreserveConflictAsync(action.RelativePath, leftPath, rightPath, cancellationToken).ConfigureAwait(false);
                    conflicts++;
                    break;
            }
        }

        var finalLeft = await _scanner.ScanAsync(leftRoot, cancellationToken).ConfigureAwait(false);
        var finalRight = await _scanner.ScanAsync(rightRoot, cancellationToken).ConfigureAwait(false);
        var keys = finalLeft.Keys.Concat(finalRight.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
        var entries = keys.ToDictionary(
            key => key,
            key => new SyncBaselineEntry(finalLeft.GetValueOrDefault(key), finalRight.GetValueOrDefault(key)),
            StringComparer.OrdinalIgnoreCase);
        return new SyncRunResult(copied, deleted, conflicts, new SyncBaseline(entries));
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async Task DeleteAsync(SyncTaskDefinition task, string relativePath, string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return;
        }

        if (task.DeletionMode == DeletionMode.BackupThenPropagate)
        {
            await _deletionVault.BackupAsync(task.Id, relativePath, path, cancellationToken).ConfigureAwait(false);
        }

        File.Delete(path);
    }

    private static async Task PreserveConflictAsync(
        string relativePath,
        string leftPath,
        string rightPath,
        CancellationToken cancellationToken)
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        if (File.Exists(leftPath))
        {
            await AtomicFileOperations.CopyAsync(leftPath, AddConflictSuffix(rightPath, "left", stamp), cancellationToken).ConfigureAwait(false);
        }

        if (File.Exists(rightPath))
        {
            await AtomicFileOperations.CopyAsync(rightPath, AddConflictSuffix(leftPath, "right", stamp), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string AddConflictSuffix(string path, string side, string stamp)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        return Path.Combine(directory, $"{fileName}.conflict-{side}-{stamp}{extension}");
    }

    private static string SafeCombine(string root, string relative)
    {
        var combined = Path.GetFullPath(Path.Combine(root, relative));
        var expectedPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("文件路径超出同步目录。");
        }

        return combined;
    }
}
