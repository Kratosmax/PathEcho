using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using PathEcho.Core.Backup;
using PathEcho.Core.Sync;

namespace PathEcho.Core.Restore;

public sealed class SnapshotRestoreService
{
    private static readonly JsonSerializerOptions SerializerOptions = new();

    private readonly IFileOccupancyService _occupancyService;

    public SnapshotRestoreService(IFileOccupancyService occupancyService)
    {
        _occupancyService = occupancyService;
    }

    public async Task<RestoreResult> RestoreAsync(
        RestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        var snapshotRoot = NormalizeRoot(request.SnapshotDirectory);
        var targetRoot = NormalizeRoot(request.TargetDirectory);
        var filesRoot = Path.Combine(snapshotRoot, "files");
        var manifest = await ReadManifestAsync(snapshotRoot, cancellationToken).ConfigureAwait(false);
        var include = CompilePatterns(request.IncludePatterns);
        var exclude = CompilePatterns(request.ExcludePatterns);
        var entries = await SelectEntriesAsync(
            manifest.Files,
            filesRoot,
            targetRoot,
            request.Mode,
            include,
            exclude,
            cancellationToken).ConfigureAwait(false);

        var targetPaths = entries.Select(entry => SafeCombine(targetRoot, entry.RelativePath)).ToArray();
        if (request.Mode == RestoreMode.CleanDirectory && Directory.Exists(targetRoot))
        {
            targetPaths = Directory.EnumerateFiles(targetRoot, "*", SearchOption.AllDirectories)
                .Concat(targetPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        await HandleOccupiedFilesAsync(targetPaths, request.OccupiedFileAction, cancellationToken).ConfigureAwait(false);
        return request.Mode == RestoreMode.CleanDirectory
            ? await RestoreWholeDirectoryAsync(entries, filesRoot, targetRoot, cancellationToken).ConfigureAwait(false)
            : await RestoreSelectedFilesAsync(entries, filesRoot, targetRoot, cancellationToken).ConfigureAwait(false);
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
        string filesRoot,
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
            await StageEntriesAsync(entries, filesRoot, stageRoot, cancellationToken).ConfigureAwait(false);
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
        string filesRoot,
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
            await StageEntriesAsync(entries, filesRoot, stagedRoot, cancellationToken).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = SafeCombine(targetRoot, entry.RelativePath);
                var staged = SafeCombine(stagedRoot, entry.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string? rollback = null;
                if (File.Exists(target))
                {
                    rollback = SafeCombine(rollbackRoot, entry.RelativePath);
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
        string filesRoot,
        string stageRoot,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(stageRoot);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = SafeCombine(filesRoot, entry.RelativePath);
            var destination = SafeCombine(stageRoot, entry.RelativePath);
            await AtomicFileOperations.CopyAsync(source, destination, cancellationToken).ConfigureAwait(false);
            File.SetAttributes(destination, FileAttributes.Normal);
            var actualHash = await ComputeHashAsync(destination, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualHash, entry.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"快照文件校验失败：{entry.RelativePath}");
            }

            File.SetLastWriteTimeUtc(destination, new DateTime(entry.LastWriteUtcTicks, DateTimeKind.Utc));
        }
    }

    private static async Task<IReadOnlyList<SnapshotFileEntry>> SelectEntriesAsync(
        IReadOnlyList<SnapshotFileEntry> entries,
        string filesRoot,
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
                var target = SafeCombine(targetRoot, entry.RelativePath);
                if (File.Exists(target) &&
                    string.Equals(await ComputeHashAsync(target, cancellationToken).ConfigureAwait(false), entry.Sha256, StringComparison.Ordinal))
                {
                    continue;
                }
            }

            var source = SafeCombine(filesRoot, entry.RelativePath);
            if (!File.Exists(source))
            {
                throw new InvalidDataException($"快照缺少文件：{entry.RelativePath}");
            }

            selected.Add(entry);
        }

        return selected;
    }

    private static async Task<SnapshotManifest> ReadManifestAsync(string snapshotRoot, CancellationToken cancellationToken)
    {
        var path = Path.Combine(snapshotRoot, "manifest.json");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        return await JsonSerializer.DeserializeAsync<SnapshotManifest>(stream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("快照清单无效。");
    }

    private static IReadOnlyList<Regex> CompilePatterns(IEnumerable<string> patterns) => patterns
        .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
        .Select(pattern => new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        .ToArray();

    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static string NormalizeRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("目录不能为空。");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string SafeCombine(string root, string relativePath)
    {
        var combined = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("恢复文件路径超出目标目录。");
        }

        return combined;
    }

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
