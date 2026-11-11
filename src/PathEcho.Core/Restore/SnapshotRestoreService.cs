using System.Text.RegularExpressions;
using PathEcho.Core.Backup;
using PathEcho.Core.IO;
using PathEcho.Core.Sync;

namespace PathEcho.Core.Restore;

public sealed class SnapshotRestoreService
{
    private readonly IFileOccupancyService _occupancyService;
    private readonly Action<RestoreFaultPoint, string>? _fault;

    public SnapshotRestoreService(IFileOccupancyService occupancyService)
        : this(occupancyService, null)
    {
    }

    internal SnapshotRestoreService(
        IFileOccupancyService occupancyService,
        Action<RestoreFaultPoint, string>? fault)
    {
        _occupancyService = occupancyService;
        _fault = fault;
    }

    public async Task<RestoreResult> RestoreAsync(
        RestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        var snapshotRoot = SafePath.NormalizeDirectory(request.SnapshotDirectory, "快照目录不能为空。");
        var targetRoot = SafePath.NormalizeDirectory(request.TargetDirectory, "目标目录不能为空。");
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNoReparsePoints(targetRoot);
        var profileRoot = SnapshotContent.GetProfileRoot(snapshotRoot);
        if (SafePath.IsSameOrNested(targetRoot, profileRoot) || SafePath.IsSameOrNested(profileRoot, targetRoot))
        {
            throw new InvalidDataException("回档目标目录不能与快照备份目录重叠。");
        }

        var manifest = await SnapshotContent.ReadManifestAsync(snapshotRoot, cancellationToken).ConfigureAwait(false);
        ValidateManifestPaths(manifest.Files, targetRoot);
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
            targetPaths = EnumerateTreeFiles(targetRoot)
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
        cancellationToken.ThrowIfCancellationRequested();
        // User-selected force mode never ends a process; transaction failure still preserves recovery data.
        if (action == OccupiedFileAction.ForceAttempt) return;
        var occupied = await _occupancyService.FindAsync(paths, cancellationToken).ConfigureAwait(false);
        if (occupied.Count == 0)
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

    private async Task<RestoreResult> RestoreWholeDirectoryAsync(
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
        var removedFiles = 0;
        try
        {
            await StageEntriesAsync(entries, snapshotRoot, stageRoot, cancellationToken).ConfigureAwait(false);
            _fault?.Invoke(RestoreFaultPoint.Staged, stageRoot);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoReparsePoints(targetRoot);
            if (Directory.Exists(targetRoot))
            {
                // Count while rollback is still possible. No fallible accounting after commit.
                removedFiles = EnumerateTreeFiles(targetRoot).Count();
                Directory.Move(targetRoot, rollbackRoot);
                targetMoved = true;
            }

            _fault?.Invoke(RestoreFaultPoint.WholeTargetMoved, rollbackRoot);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stageRoot, targetRoot);
        }
        catch (Exception failure)
        {
            try
            {
                if (targetMoved)
                {
                    _fault?.Invoke(RestoreFaultPoint.WholeRollback, rollbackRoot);
                    EnsureNoReparsePoints(targetRoot);
                    // Move must fail if another target appeared; keep both recovery copies.
                    Directory.Move(rollbackRoot, targetRoot);
                }
            }
            catch (Exception rollbackFailure)
            {
                throw RecoveryFailure(rollbackRoot, failure, rollbackFailure);
            }

            CleanupAfterFailure(stageRoot, failure);
            throw;
        }

        // Directory.Move above is the commit point. Cleanup must never enter rollback.
        return CompleteRestore(entries.Count, removedFiles, rollbackRoot, targetRoot);
    }

    private async Task<RestoreResult> RestoreSelectedFilesAsync(
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
        var journal = new List<(string Target, string? Rollback, bool Installed)>();
        var createdDirectories = new List<string>();
        try
        {
            await StageEntriesAsync(entries, snapshotRoot, stagedRoot, cancellationToken).ConfigureAwait(false);
            _fault?.Invoke(RestoreFaultPoint.Staged, stagedRoot);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = CombineUnderRoot(targetRoot, entry.RelativePath);
                var staged = CombineUnderRoot(stagedRoot, entry.RelativePath);
                EnsureNoReparsePoints(target);
                var targetParent = Path.GetDirectoryName(target)!;
                for (var missing = targetParent; !Directory.Exists(missing); missing = Path.GetDirectoryName(missing)!)
                {
                    createdDirectories.Add(missing);
                }

                Directory.CreateDirectory(targetParent);
                string? rollback = null;
                if (File.Exists(target))
                {
                    rollback = CombineUnderRoot(rollbackRoot, entry.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(rollback)!);
                    File.Move(target, rollback);
                }

                // Journal the original before installing the staged file, including install failures.
                journal.Add((target, rollback, false));
                _fault?.Invoke(RestoreFaultPoint.SelectedTargetMoved, target);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(staged, target);
                journal[^1] = (target, rollback, true);
                _fault?.Invoke(RestoreFaultPoint.SelectedFileCommitted, target);
            }

            // Cancellation is honored through the final installation, before cleanup can destroy originals.
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception failure)
        {
            var rollbackFailures = new List<Exception>();
            for (var index = journal.Count - 1; index >= 0; index--)
            {
                var item = journal[index];
                try
                {
                    _fault?.Invoke(RestoreFaultPoint.SelectedRollback, item.Target);
                    EnsureNoReparsePoints(item.Target);
                    if (item.Rollback is not null)
                    {
                        // Never delete a new target unless its original is still recoverable.
                        // Move with overwrite preserves the rollback source when replacement fails.
                        EnsureNoReparsePoints(item.Rollback);
                        File.Move(item.Rollback, item.Target, true);
                    }
                    else if (item.Installed)
                    {
                        File.Delete(item.Target);
                    }
                }
                catch (Exception rollbackFailure)
                {
                    rollbackFailures.Add(rollbackFailure);
                }
            }

            if (rollbackFailures.Count == 0)
            {
                foreach (var directory in createdDirectories.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(path => path.Length))
                {
                    try
                    {
                        EnsureNoReparsePoints(directory);
                        Directory.Delete(directory);
                    }
                    catch (Exception rollbackFailure)
                    {
                        rollbackFailures.Add(rollbackFailure);
                    }
                }
            }

            if (rollbackFailures.Count > 0)
            {
                throw RecoveryFailure(transactionRoot, failure, new AggregateException(rollbackFailures));
            }

            CleanupAfterFailure(transactionRoot, failure);
            throw;
        }

        // All files are installed. Partial deletion of rollback files is irreversible.
        return CompleteRestore(entries.Count, 0, transactionRoot, targetRoot);
    }

    private RestoreResult CompleteRestore(int restoredFiles, int removedFiles, string cleanupRoot, string targetRoot)
    {
        try
        {
            _fault?.Invoke(RestoreFaultPoint.Committed, targetRoot);
            DeleteTree(cleanupRoot);
            return new RestoreResult(restoredFiles, removedFiles, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // Cleanup is best effort after commit, even if cancellation arrives here.
            return new RestoreResult(restoredFiles, removedFiles, cleanupRoot);
        }
    }

    private void CleanupAfterFailure(string path, Exception failure)
    {
        try
        {
            DeleteTree(path);
        }
        catch (Exception cleanupFailure)
        {
            throw RecoveryFailure(path, failure, cleanupFailure);
        }
    }

    private static IOException RecoveryFailure(string path, Exception failure, Exception recoveryFailure) =>
        new($"回档未完成，恢复副本已保留，请勿删除此目录：{path}", new AggregateException(failure, recoveryFailure));

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

            var target = CombineUnderRoot(targetRoot, entry.RelativePath);
            EnsureNoReparsePoints(target);
            if (mode == RestoreMode.ChangedFiles)
            {
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

    private static void ValidateManifestPaths(IReadOnlyList<SnapshotFileEntry> entries, string targetRoot)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var relative = entry.RelativePath;
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
                relative.Split(new[] { '/', '\\' }).Any(part =>
                    string.IsNullOrEmpty(part) || part is "." or ".." ||
                    part.EndsWith(' ') || part.EndsWith('.') ||
                    part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.Contains(':') ||
                    Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase)))
            {
                throw new InvalidDataException("快照包含无效的恢复文件路径。");
            }

            if (!paths.Add(CombineUnderRoot(targetRoot, relative)))
            {
                throw new InvalidDataException("快照包含重复的恢复文件路径。");
            }
        }
    }

    private static void EnsureNoReparsePoints(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"回档路径包含链接或重解析点：{current}");
                }
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private static IEnumerable<string> EnumerateTreeFiles(string path)
    {
        EnsureNoReparsePoints(path);
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            EnsureNoReparsePoints(child);
            if ((File.GetAttributes(child) & FileAttributes.Directory) != 0)
            {
                foreach (var file in EnumerateTreeFiles(child))
                {
                    yield return file;
                }
            }
            else
            {
                yield return child;
            }
        }
    }

    private void DeleteTree(string path)
    {
        EnsureNoReparsePoints(path);
        if (!Directory.Exists(path))
        {
            return;
        }

        _fault?.Invoke(RestoreFaultPoint.CleanupEnumerate, path);
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            EnsureNoReparsePoints(child);
            if ((File.GetAttributes(child) & FileAttributes.Directory) != 0)
            {
                DeleteTree(child);
            }
            else
            {
                _fault?.Invoke(RestoreFaultPoint.CleanupFile, child);
                File.SetAttributes(child, FileAttributes.Normal);
                File.Delete(child);
            }
        }

        Directory.Delete(path);
    }
}
