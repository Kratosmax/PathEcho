using System.Text.Json;
using PathEcho.Core.IO;
using PathEcho.Core.Sync;

namespace PathEcho.Core.Backup;

public sealed class SnapshotStore : IBackupSnapshotStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly DirectoryScanner _scanner = new();
    private readonly HashSet<string> _verifiedObjects = new(StringComparer.OrdinalIgnoreCase);

    public async Task<SnapshotCreationResult> CreateAsync(
        Guid profileId,
        string sourceDirectory,
        string backupDirectory,
        string trigger,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? changedPaths = null)
    {
        var sourceRoot = SafePath.NormalizeDirectory(sourceDirectory, "存档目录不能为空。");
        var backupRoot = SafePath.NormalizeDirectory(backupDirectory, "备份目录不能为空。");
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException($"存档目录不存在：{sourceRoot}");
        }

        var profileRoot = Path.Combine(backupRoot, profileId.ToString("N"));
        ValidateSeparateRoots(sourceRoot, profileRoot);
        var objectsRoot = Path.Combine(profileRoot, "objects");
        var snapshotsRoot = Path.Combine(profileRoot, "snapshots");
        Directory.CreateDirectory(objectsRoot);
        Directory.CreateDirectory(snapshotsRoot);

        var entries = await BuildCurrentEntriesAsync(
            sourceRoot,
            snapshotsRoot,
            changedPaths,
            cancellationToken).ConfigureAwait(false);
        var createdAt = DateTimeOffset.UtcNow;
        var snapshotName = $"{createdAt:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}";
        var pendingRoot = Path.Combine(snapshotsRoot, $".{snapshotName}.pending");
        var finalRoot = Path.Combine(snapshotsRoot, snapshotName);
        Directory.CreateDirectory(pendingRoot);
        var newObjects = 0;
        var reusedObjects = 0;

        try
        {
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = SafePath.CombineUnderRoot(
                    sourceRoot,
                    entry.RelativePath,
                    "备份文件路径超出存档目录。");
                var objectPath = SnapshotContent.GetObjectPath(profileRoot, entry.Sha256);
                if (await EnsureObjectAsync(sourcePath, objectPath, entry.Sha256, cancellationToken).ConfigureAwait(false))
                {
                    newObjects++;
                }
                else
                {
                    reusedObjects++;
                }
            }

            var manifest = new SnapshotManifest
            {
                SchemaVersion = 2,
                ProfileId = profileId,
                CreatedAtUtc = createdAt,
                Trigger = trigger,
                Files = entries,
            };
            await WriteManifestAsync(Path.Combine(pendingRoot, "manifest.json"), manifest, cancellationToken)
                .ConfigureAwait(false);
            Directory.Move(pendingRoot, finalRoot);
        }
        catch
        {
            DirectoryTree.DeleteIfPresent(pendingRoot);
            throw;
        }

        await MigrateLegacyViewsAsync(profileRoot, cancellationToken).ConfigureAwait(false);
        return new SnapshotCreationResult(finalRoot, entries.Count, newObjects, reusedObjects, 0, 0);
    }

    public async Task<int> PruneAsync(
        Guid profileId,
        string backupDirectory,
        int retainedVersions,
        CancellationToken cancellationToken = default)
    {
        if (retainedVersions < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(retainedVersions), "至少保留一个备份版本。");
        }

        var profileRoot = Path.Combine(
            SafePath.NormalizeDirectory(backupDirectory, "备份目录不能为空。"),
            profileId.ToString("N"));
        var snapshotsRoot = Path.Combine(profileRoot, "snapshots");
        if (!Directory.Exists(snapshotsRoot))
        {
            return 0;
        }

        var snapshots = EnumerateSnapshotDirectories(snapshotsRoot).ToArray();
        var removed = 0;
        foreach (var snapshot in snapshots.Skip(retainedVersions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectoryTree.DeleteIfPresent(snapshot);
            removed++;
        }

        await GarbageCollectObjectsAsync(profileRoot, cancellationToken).ConfigureAwait(false);
        return removed;
    }

    public async Task<IReadOnlyList<SnapshotVersion>> ListAsync(
        Guid profileId,
        string backupDirectory,
        CancellationToken cancellationToken = default)
    {
        var snapshotsRoot = Path.Combine(
            SafePath.NormalizeDirectory(backupDirectory, "备份目录不能为空。"),
            profileId.ToString("N"),
            "snapshots");
        if (!Directory.Exists(snapshotsRoot))
        {
            return Array.Empty<SnapshotVersion>();
        }

        var versions = new List<SnapshotVersion>();
        foreach (var directory in EnumerateSnapshotDirectories(snapshotsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            versions.Add(new SnapshotVersion(
                directory,
                await SnapshotContent.ReadManifestAsync(directory, cancellationToken).ConfigureAwait(false)));
        }

        return versions.OrderByDescending(version => version.Manifest.CreatedAtUtc).ToArray();
    }

    private async Task<IReadOnlyList<SnapshotFileEntry>> BuildCurrentEntriesAsync(
        string sourceRoot,
        string snapshotsRoot,
        IReadOnlyCollection<string>? changedPaths,
        CancellationToken cancellationToken)
    {
        if (changedPaths is null || changedPaths.Count == 0)
        {
            return await ScanAllEntriesAsync(sourceRoot, cancellationToken).ConfigureAwait(false);
        }

        var latestDirectory = EnumerateSnapshotDirectories(snapshotsRoot).FirstOrDefault();
        if (latestDirectory is null)
        {
            return await ScanAllEntriesAsync(sourceRoot, cancellationToken).ConfigureAwait(false);
        }

        var latest = await SnapshotContent.ReadManifestAsync(latestDirectory, cancellationToken).ConfigureAwait(false);
        var entries = latest.Files.ToDictionary(
            entry => entry.RelativePath,
            StringComparer.OrdinalIgnoreCase);
        foreach (var rawPath in changedPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = rawPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            if (relativePath == "." || Path.IsPathRooted(relativePath))
            {
                return await ScanAllEntriesAsync(sourceRoot, cancellationToken).ConfigureAwait(false);
            }

            var fullPath = SafePath.CombineUnderRoot(sourceRoot, relativePath, "变化文件路径超出存档目录。");
            RemovePathAndChildren(entries, relativePath);
            if (File.Exists(fullPath))
            {
                entries[relativePath] = await CreateEntryAsync(fullPath, relativePath, cancellationToken).ConfigureAwait(false);
            }
            else if (Directory.Exists(fullPath))
            {
                var subtree = await _scanner.ScanAsync(
                    fullPath,
                    cancellationToken,
                    forceHash: true).ConfigureAwait(false);
                foreach (var pair in subtree)
                {
                    var childRelative = Path.Combine(relativePath, pair.Key);
                    entries[childRelative] = ToEntry(childRelative, pair.Value);
                }
            }
        }

        return entries.Values.OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<IReadOnlyList<SnapshotFileEntry>> ScanAllEntriesAsync(
        string sourceRoot,
        CancellationToken cancellationToken)
    {
        var files = await _scanner.ScanAsync(
            sourceRoot,
            cancellationToken,
            forceHash: true).ConfigureAwait(false);
        return files
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => ToEntry(pair.Key, pair.Value))
            .ToArray();
    }

    private static async Task<SnapshotFileEntry> CreateEntryAsync(
        string path,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var stamp = await FileStamp.CreateAsync(path, cancellationToken).ConfigureAwait(false);
        return ToEntry(relativePath, stamp);
    }

    private static SnapshotFileEntry ToEntry(string relativePath, FileStamp stamp) => new()
    {
        RelativePath = relativePath,
        Sha256 = stamp.Sha256,
        Length = stamp.Length,
        LastWriteUtcTicks = stamp.LastWriteUtcTicks,
    };

    private static void RemovePathAndChildren(
        IDictionary<string, SnapshotFileEntry> entries,
        string relativePath)
    {
        var prefix = Path.TrimEndingDirectorySeparator(relativePath) + Path.DirectorySeparatorChar;
        foreach (var key in entries.Keys
                     .Where(key => string.Equals(key, relativePath, StringComparison.OrdinalIgnoreCase) ||
                         key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            entries.Remove(key);
        }
    }

    private async Task<bool> EnsureObjectAsync(
        string sourcePath,
        string objectPath,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        if (File.Exists(objectPath))
        {
            if (_verifiedObjects.Contains(objectPath) ||
                string.Equals(
                    await ContentHash.ComputeAsync(objectPath, cancellationToken).ConfigureAwait(false),
                    expectedHash,
                    StringComparison.Ordinal))
            {
                _verifiedObjects.Add(objectPath);
                return false;
            }

            File.SetAttributes(objectPath, FileAttributes.Normal);
            File.Delete(objectPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);
        await AtomicFileOperations.CopyAsync(sourcePath, objectPath, cancellationToken).ConfigureAwait(false);
        var storedHash = await ContentHash.ComputeAsync(objectPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(storedHash, expectedHash, StringComparison.Ordinal))
        {
            File.Delete(objectPath);
            throw new IOException($"备份对象校验失败：{Path.GetFileName(sourcePath)}");
        }

        _verifiedObjects.Add(objectPath);
        return true;
    }

    private async Task MigrateLegacyViewsAsync(string profileRoot, CancellationToken cancellationToken)
    {
        var snapshotsRoot = Path.Combine(profileRoot, "snapshots");
        foreach (var snapshotDirectory in EnumerateSnapshotDirectories(snapshotsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filesRoot = Path.Combine(snapshotDirectory, "files");
            if (!Directory.Exists(filesRoot))
            {
                continue;
            }

            try
            {
                var manifest = await SnapshotContent.ReadManifestAsync(snapshotDirectory, cancellationToken)
                    .ConfigureAwait(false);
                var valid = true;
                foreach (var entry in manifest.Files)
                {
                    var objectPath = SnapshotContent.GetObjectPath(profileRoot, entry.Sha256);
                    if (!File.Exists(objectPath) ||
                        (!_verifiedObjects.Contains(objectPath) &&
                         !string.Equals(
                             await ContentHash.ComputeAsync(objectPath, cancellationToken).ConfigureAwait(false),
                             entry.Sha256,
                             StringComparison.Ordinal)))
                    {
                        valid = false;
                        break;
                    }

                    _verifiedObjects.Add(objectPath);
                }

                if (!valid)
                {
                    continue;
                }

                DirectoryTree.DeleteIfPresent(filesRoot);
                if (manifest.SchemaVersion < 2)
                {
                    await ReplaceManifestAsync(
                        Path.Combine(snapshotDirectory, "manifest.json"),
                        manifest with { SchemaVersion = 2 },
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                // Leave a legacy view intact unless every referenced object was verified.
            }
        }
    }

    private static IEnumerable<string> EnumerateSnapshotDirectories(string snapshotsRoot) =>
        Directory.EnumerateDirectories(snapshotsRoot)
            .Where(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal))
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal);

    private static async Task GarbageCollectObjectsAsync(string profileRoot, CancellationToken cancellationToken)
    {
        var snapshotsRoot = Path.Combine(profileRoot, "snapshots");
        var usedHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var snapshotDirectory in EnumerateSnapshotDirectories(snapshotsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = await SnapshotContent.ReadManifestAsync(snapshotDirectory, cancellationToken).ConfigureAwait(false);
            usedHashes.UnionWith(manifest.Files.Select(file => file.Sha256));
        }

        var objectsRoot = Path.Combine(profileRoot, "objects");
        if (!Directory.Exists(objectsRoot))
        {
            return;
        }

        foreach (var objectPath in Directory.EnumerateFiles(objectsRoot, "*.blob", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!usedHashes.Contains(Path.GetFileNameWithoutExtension(objectPath)))
            {
                File.SetAttributes(objectPath, FileAttributes.Normal);
                File.Delete(objectPath);
            }
        }
    }

    private static async Task WriteManifestAsync(
        string path,
        SnapshotManifest manifest,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
        await JsonSerializer.SerializeAsync(stream, manifest, SerializerOptions, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReplaceManifestAsync(
        string path,
        SnapshotManifest manifest,
        CancellationToken cancellationToken)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await WriteManifestAsync(temporary, manifest, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void ValidateSeparateRoots(string sourceRoot, string backupRoot)
    {
        if (SafePath.IsSameOrNested(sourceRoot, backupRoot) ||
            SafePath.IsSameOrNested(backupRoot, sourceRoot))
        {
            throw new InvalidOperationException("存档目录和备份目录不能相同或互相包含。");
        }
    }
}
