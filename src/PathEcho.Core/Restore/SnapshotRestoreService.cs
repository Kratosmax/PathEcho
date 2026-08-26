using System.Text.RegularExpressions;
using PathEcho.Core.Backup;
using PathEcho.Core.IO;
using PathEcho.Core.Sync;

namespace PathEcho.Core.Restore;

public sealed class SnapshotRestoreService
{
    private readonly IFileOccupancyService _occupancyService;

    public SnapshotRestoreService(IFileOccupancyService occupancyService)
    {
        _occupancyService = occupancyService;
    }

    public async Task<RestoreResult> RestoreAsync(
        RestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        var snapshotRoot = SafePath.NormalizeDirectory(request.SnapshotDirectory, "快照目录不能为空。");
        var targetRoot = SafePath.NormalizeDirectory(request.TargetDirectory, "目标目录不能为空。");
        var manifest = await SnapshotContent.ReadManifestAsync(snapshotRoot, cancellationToken).ConfigureAwait(false);
        var include = CompilePatterns(request.IncludePatterns);
        var exclude = CompilePatterns(request.ExcludePatterns);
        var entries = await SelectEntriesAsync(
            manifest.Files,
            snapshotRoot,
            targetRoot,
            request.Mode,
            include,
            exclude,
            cancellationToken).ConfigureAwait(false);

        var targetPaths = entries.Select(entry => CombineUnderRoot(targetRoot, entry.RelativePath)).ToArray();
        if (request.Mode == RestoreMode.CleanDirectory && Directory.Exists(targetRoot))
        {
            targetPaths = Directory.EnumerateFiles(targetRoot, "*", SearchOption.AllDirectories)
                .Concat(targetPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        await HandleOccupiedFilesAsync(targetPaths, request.OccupiedFileAction, cancellationToken).ConfigureAwait(false);
        return request.Mode == RestoreMode.CleanDirectory
            ? await RestoreWholeDirectoryAsync(entries, snapshotRoot, targetRoot, cancellationToken).ConfigureAwait(false)
            : await RestoreSelectedFilesAsync(entries, snapshotRoot, targetRoot, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleOccupiedFilesAsync(
        IReadOnlyList<string> paths,
        OccupiedFileAction action,
        CancellationToken cancellationToken)
    {
        var occupied = await _occupancyService.FindAsync(paths, cancellationToken).ConfigureAwait(false);
        if (occupied.Count == 0 || action == OccupiedFileAction.ForceAttempt)
        {
            return;
        }

        if (action == OccupiedFileAction.Cancel)
        {
            throw new FilesOccupiedException(occupied);
        }

        var processes = occupied.SelectMany(file => file.Processes)
            .DistinctBy(process => (process.ProcessId, process.StartedAtUtc))
            .ToArray();
        if (processes.Any(process => !process.CanTerminate))
        {
            throw new FilesOccupiedException(occupied);
        }

        await _occupancyService.TerminateAsync(processes, cancellationToken).ConfigureAwait(false);
        var remaining = await _occupancyService.FindAsync(paths, cancellationToken).ConfigureAwait(false);
        if (remaining.Count > 0)
        {
            throw new FilesOccupiedException(remaining);
        }
    }

    private static async Task<RestoreResult> RestoreWholeDirectoryAsync(
        IReadOnlyList<SnapshotFileEntry> entries,
        string snapshotRoot,
        string targetRoot,
        CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(targetRoot) ?? throw new InvalidOperationException("存档目录缺少父目录。");
        Directory.CreateDirectory(parent);
        var token = Guid.NewGuid().ToString("N");
        var stageRoot = Path.Combine(parent, $".pathecho-restore-{token}");
        var rollbackRoot = Path.Combine(parent, $".pathecho-rollback-{token}");
        var targetMoved = false;
        try
        {
            await StageEntriesAsync(entries, snapshotRoot, stageRoot, cancellationToken).ConfigureAwait(false);
            if (Directory.Exists(targetRoot))
            {
                Directory.Move(targetRoot, rollbackRoot);
                targetMoved = true;
            }

            Directory.Move(stageRoot, targetRoot);
            var removedFiles = targetMoved
                ? Directory.EnumerateFiles(rollbackRoot, "*", SearchOption.AllDirectories).Count()
                : 0;
            string? preservedRollback = null;
            try
            {
                DeleteTree(rollbackRoot);
            }
            catch (IOException)
            {
                preservedRollback = rollbackRoot;
            }
            catch (UnauthorizedAccessException)
            {
                preservedRollback = rollbackRoot;
            }

            return new RestoreResult(entries.Count, removedFiles, preservedRollback);
        }
        catch
        {
            if (targetMoved && !Directory.Exists(targetRoot) && Directory.Exists(rollbackRoot))
            {
                Directory.Move(rollbackRoot, targetRoot);
            }

            DeleteTree(stageRoot);
            throw;
        }
    }

    private static async Task<RestoreResult> RestoreSelectedFilesAsync(
        IReadOnlyList<SnapshotFileEntry> entries,
        string snapshotRoot,
        string targetRoot,
        CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(targetRoot) ?? throw new InvalidOperationException("存档目录缺少父目录。");
        Directory.CreateDirectory(parent);
        var transactionRoot = Path.Combine(parent, $".pathecho-file-restore-{Guid.NewGuid():N}");
        var stagedRoot = Path.Combine(transactionRoot, "staged");
        var rollbackRoot = Path.Combine(transactionRoot, "rollback");
        var committed = new List<(string Target, string? Rollback)>();
        try
        {
            await StageEntriesAsync(entries, snapshotRoot, stagedRoot, cancellationToken).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = CombineUnderRoot(targetRoot, entry.RelativePath);
                var staged = CombineUnderRoot(stagedRoot, entry.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string? rollback = null;
                if (File.Exists(target))
                {
                    rollback = CombineUnderRoot(rollbackRoot, entry.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(rollback)!);
                    File.Move(target, rollback);
                }

                try
                {
                    File.Move(staged, target);
                    committed.Add((target, rollback));
                }
                catch
                {
                    if (rollback is not null && File.Exists(rollback))
                    {
                        File.Move(rollback, target);
                    }

                    throw;
                }
            }

            DeleteTree(transactionRoot);
            return new RestoreResult(entries.Count, 0, null);
        }
        catch
        {
            for (var index = committed.Count - 1; index >= 0; index--)
            {
                var item = committed[index];
                if (File.Exists(item.Target))
                {
                    File.SetAttributes(item.Target, FileAttributes.Normal);
                    File.Delete(item.Target);
                }

                if (item.Rollback is not null && File.Exists(item.Rollback))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(item.Target)!);
                    File.Move(item.Rollback, item.Target);
                }
            }

            DeleteTree(transactionRoot);
            throw;
        }
    }

    private static async Task StageEntriesAsync(
        IReadOnlyList<SnapshotFileEntry> entries,
        string snapshotRoot,
        string stageRoot,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(stageRoot);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = await SnapshotContent.ResolveVerifiedFileAsync(snapshotRoot, entry, cancellationToken)
                .ConfigureAwait(false);
            var destination = CombineUnderRoot(stageRoot, entry.RelativePath);
            await AtomicFileOperations.CopyAsync(source, destination, cancellationToken).ConfigureAwait(false);
            File.SetAttributes(destination, FileAttributes.Normal);
            var actualHash = await ContentHash.ComputeAsync(destination, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualHash, entry.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"快照文件校验失败：{entry.RelativePath}");
            }

            File.SetLastWriteTimeUtc(destination, new DateTime(entry.LastWriteUtcTicks, DateTimeKind.Utc));
        }
    }

    private static async Task<IReadOnlyList<SnapshotFileEntry>> SelectEntriesAsync(
        IReadOnlyList<SnapshotFileEntry> entries,
        string snapshotRoot,
        string targetRoot,
        RestoreMode mode,
        IReadOnlyList<Regex> include,
        IReadOnlyList<Regex> exclude,
        CancellationToken cancellationToken)
    {
        var selected = new List<SnapshotFileEntry>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (exclude.Any(pattern => pattern.IsMatch(entry.RelativePath)))
            {
                continue;
            }

            if (mode == RestoreMode.FilteredFiles &&
                (include.Count == 0 || !include.Any(pattern => pattern.IsMatch(entry.RelativePath))))
            {
                continue;
            }

            if (mode == RestoreMode.ChangedFiles)
            {
                var target = CombineUnderRoot(targetRoot, entry.RelativePath);
                if (File.Exists(target) &&
                    string.Equals(await ContentHash.ComputeAsync(target, cancellationToken).ConfigureAwait(false), entry.Sha256, StringComparison.Ordinal))
                {
                    continue;
                }
            }

            _ = await SnapshotContent.ResolveVerifiedFileAsync(snapshotRoot, entry, cancellationToken)
                .ConfigureAwait(false);

            selected.Add(entry);
        }

        return selected;
    }

    private static IReadOnlyList<Regex> CompilePatterns(IEnumerable<string> patterns) => patterns
        .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
        .Select(pattern => new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        .ToArray();

    private static string CombineUnderRoot(string root, string relativePath) =>
        SafePath.CombineUnderRoot(root, relativePath, "恢复文件路径超出目标目录。");

    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, true);
    }
}
