using PathEcho.Core.Models;
using PathEcho.Core.IO;

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

    public void InvalidatePaths(SyncTaskDefinition task, IEnumerable<string> changedPaths)
    {
        var leftRoot = SyncTaskDefinition.NormalizeRoot(task.LeftPath);
        var rightRoot = SyncTaskDefinition.NormalizeRoot(task.RightPath);
        foreach (var changedPath in changedPaths)
        {
            if (SafePath.TryGetRelativePath(leftRoot, changedPath, out _))
            {
                _scanner.InvalidatePath(leftRoot, changedPath);
            }

            if (SafePath.TryGetRelativePath(rightRoot, changedPath, out _))
            {
                _scanner.InvalidatePath(rightRoot, changedPath);
            }
        }
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

            var left = await _scanner.ScanAsync(leftRoot, cancellationToken, task.Filters).ConfigureAwait(false);
            var right = await _scanner.ScanAsync(rightRoot, cancellationToken, task.Filters).ConfigureAwait(false);
            var plan = _planner.CreatePlan(task, left, right, baseline);
            var finalLeft = new Dictionary<string, FileStamp>(left, StringComparer.OrdinalIgnoreCase);
            var finalRight = new Dictionary<string, FileStamp>(right, StringComparer.OrdinalIgnoreCase);
            var copied = 0;
            var deleted = 0;
            var conflicts = 0;
            var requiresFinalScan = false;

            foreach (var action in plan.Actions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var leftPath = SafeCombine(leftRoot, action.RelativePath);
                var rightPath = SafeCombine(rightRoot, action.RelativePath);
                switch (action.Kind)
                {
                    case SyncActionKind.CopyLeftToRight:
                        await AtomicFileOperations.CopyAsync(leftPath, rightPath, cancellationToken).ConfigureAwait(false);
                        var rightStamp = await FileStamp.CreateAsync(rightPath, cancellationToken).ConfigureAwait(false);
                        finalRight[action.RelativePath] = rightStamp;
                        _scanner.SetCachedPath(rightRoot, action.RelativePath, rightStamp);
                        copied++;
                        break;
                    case SyncActionKind.CopyRightToLeft:
                        await AtomicFileOperations.CopyAsync(rightPath, leftPath, cancellationToken).ConfigureAwait(false);
                        var leftStamp = await FileStamp.CreateAsync(leftPath, cancellationToken).ConfigureAwait(false);
                        finalLeft[action.RelativePath] = leftStamp;
                        _scanner.SetCachedPath(leftRoot, action.RelativePath, leftStamp);
                        copied++;
                        break;
                    case SyncActionKind.DeleteLeft:
                        await DeleteAsync(task, action.RelativePath, leftPath, cancellationToken).ConfigureAwait(false);
                        _scanner.InvalidatePath(leftRoot, leftPath);
                        finalLeft.Remove(action.RelativePath);
                        deleted++;
                        break;
                    case SyncActionKind.DeleteRight:
                        await DeleteAsync(task, action.RelativePath, rightPath, cancellationToken).ConfigureAwait(false);
                        _scanner.InvalidatePath(rightRoot, rightPath);
                        finalRight.Remove(action.RelativePath);
                        deleted++;
                        break;
                    case SyncActionKind.KeepBothConflict:
                        await PreserveConflictAsync(action.RelativePath, leftPath, rightPath, cancellationToken).ConfigureAwait(false);
                        _scanner.Invalidate(leftRoot);
                        _scanner.Invalidate(rightRoot);
                        requiresFinalScan = true;
                        conflicts++;
                        break;
                }
            }

            if (requiresFinalScan)
            {
                finalLeft = new Dictionary<string, FileStamp>(
                    await _scanner.ScanAsync(leftRoot, cancellationToken, task.Filters).ConfigureAwait(false),
                    StringComparer.OrdinalIgnoreCase);
                finalRight = new Dictionary<string, FileStamp>(
                    await _scanner.ScanAsync(rightRoot, cancellationToken, task.Filters).ConfigureAwait(false),
                    StringComparer.OrdinalIgnoreCase);
            }

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

    public async Task<SyncPreviewResult> PreviewAsync(
        SyncTaskDefinition task,
        SyncBaseline baseline,
        CancellationToken cancellationToken = default)
    {
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            task.Validate();
            var leftRoot = SyncTaskDefinition.NormalizeRoot(task.LeftPath);
            var rightRoot = SyncTaskDefinition.NormalizeRoot(task.RightPath);
            if (!Directory.Exists(leftRoot) || !Directory.Exists(rightRoot))
            {
                throw new DirectoryNotFoundException("同步预演要求左右目录都已存在。");
            }

            _scanner.Invalidate(leftRoot);
            _scanner.Invalidate(rightRoot);
            var left = await _scanner.ScanAsync(leftRoot, cancellationToken, task.Filters).ConfigureAwait(false);
            var right = await _scanner.ScanAsync(rightRoot, cancellationToken, task.Filters).ConfigureAwait(false);
            return new SyncPreviewResult(_planner.CreatePlan(task, left, right, baseline).Actions);
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
        return SafePath.CombineUnderRoot(root, relative, "文件路径超出同步目录。");
    }
}
