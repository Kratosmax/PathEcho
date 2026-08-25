using System.Security.Cryptography;
using System.Text.Json;
using PathEcho.Core.Sync;

namespace PathEcho.Core.Backup;

public sealed class SnapshotStore : IBackupSnapshotStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };
    private readonly DirectoryScanner _scanner = new();
    private readonly HashSet<string> _verifiedObjects = new(StringComparer.OrdinalIgnoreCase);

    public async Task<SnapshotCreationResult> CreateAsync(
        Guid profileId,
        string sourceDirectory,
        string backupDirectory,
        string trigger,
        CancellationToken cancellationToken = default)
    {
        var sourceRoot = NormalizeRoot(sourceDirectory);
        var backupRoot = NormalizeRoot(backupDirectory);
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

        var createdAt = DateTimeOffset.UtcNow;
        var snapshotName = $"{createdAt:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}";
        var pendingRoot = Path.Combine(snapshotsRoot, $".{snapshotName}.pending");
        var finalRoot = Path.Combine(snapshotsRoot, snapshotName);
        var filesRoot = Path.Combine(pendingRoot, "files");
        Directory.CreateDirectory(filesRoot);

        var entries = new List<SnapshotFileEntry>();
        var newObjects = 0;
        var reusedObjects = 0;
        var hardLinks = 0;
        var copiedViews = 0;

        try
        {
            var scannedFiles = await _scanner.ScanAsync(sourceRoot, cancellationToken).ConfigureAwait(false);
            foreach (var pair in scannedFiles.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = pair.Key;
                var sourcePath = SafeCombine(sourceRoot, relativePath);
                var stamp = pair.Value;
                var objectPath = GetObjectPath(objectsRoot, stamp.Sha256);
                var objectExists = File.Exists(objectPath);
                if (objectExists && !_verifiedObjects.Contains(objectPath))
                {
                    objectExists = string.Equals(
                        await ComputeHashAsync(objectPath, cancellationToken).ConfigureAwait(false),
                        stamp.Sha256,
                        StringComparison.Ordinal);
                    if (!objectExists)
                    {
                        File.SetAttributes(objectPath, FileAttributes.Normal);
                        File.Delete(objectPath);
                    }
                }

                if (!objectExists)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);
                    await AtomicFileOperations.CopyAsync(sourcePath, objectPath, cancellationToken).ConfigureAwait(false);
                    var storedHash = await ComputeHashAsync(objectPath, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(storedHash, stamp.Sha256, StringComparison.Ordinal))
                    {
                        File.Delete(objectPath);
                        throw new IOException($"备份对象校验失败：{relativePath}");
                    }

                    newObjects++;
                }
                else
                {
                    reusedObjects++;
                }

                _verifiedObjects.Add(objectPath);

                var viewPath = SafeCombine(filesRoot, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(viewPath)!);
                if (HardLink.TryCreate(viewPath, objectPath))
                {
                    hardLinks++;
                }
                else
                {
                    await AtomicFileOperations.CopyAsync(objectPath, viewPath, cancellationToken).ConfigureAwait(false);
                    copiedViews++;
                }

                File.SetLastWriteTimeUtc(viewPath, new DateTime(stamp.LastWriteUtcTicks, DateTimeKind.Utc));
                File.SetAttributes(viewPath, File.GetAttributes(viewPath) | FileAttributes.ReadOnly);
                entries.Add(new SnapshotFileEntry
                {
                    RelativePath = relativePath,
                    Sha256 = stamp.Sha256,
                    Length = stamp.Length,
                    LastWriteUtcTicks = stamp.LastWriteUtcTicks,
                });
            }

            var manifest = new SnapshotManifest
            {
                ProfileId = profileId,
                CreatedAtUtc = createdAt,
                Trigger = trigger,
                Files = entries.OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
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

        return new SnapshotCreationResult(
            finalRoot,
            entries.Count,
            newObjects,
            reusedObjects,
            hardLinks,
            copiedViews);
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

        var profileRoot = Path.Combine(NormalizeRoot(backupDirectory), profileId.ToString("N"));
        var snapshotsRoot = Path.Combine(profileRoot, "snapshots");
        if (!Directory.Exists(snapshotsRoot))
        {
            return 0;
        }

        var snapshots = Directory.EnumerateDirectories(snapshotsRoot)
            .Where(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal))
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();
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
        var snapshotsRoot = Path.Combine(NormalizeRoot(backupDirectory), profileId.ToString("N"), "snapshots");
        if (!Directory.Exists(snapshotsRoot))
        {
            return Array.Empty<SnapshotVersion>();
        }

        var versions = new List<SnapshotVersion>();
        foreach (var directory in Directory.EnumerateDirectories(snapshotsRoot)
                     .Where(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            await using var stream = File.OpenRead(manifestPath);
            var manifest = await JsonSerializer.DeserializeAsync<SnapshotManifest>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidDataException($"备份清单无效：{manifestPath}");
            versions.Add(new SnapshotVersion(directory, manifest));
        }

        return versions.OrderByDescending(version => version.Manifest.CreatedAtUtc).ToArray();
    }

    private static async Task GarbageCollectObjectsAsync(string profileRoot, CancellationToken cancellationToken)
    {
        var snapshotsRoot = Path.Combine(profileRoot, "snapshots");
        var usedHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var manifestPath in Directory.EnumerateFiles(snapshotsRoot, "manifest.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = File.OpenRead(manifestPath);
            var manifest = await JsonSerializer.DeserializeAsync<SnapshotManifest>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidDataException($"备份清单无效：{manifestPath}");
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

    private static string GetObjectPath(string objectsRoot, string hash) =>
        Path.Combine(objectsRoot, hash[..2], $"{hash}.blob");

    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
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

    private static void ValidateSeparateRoots(string sourceRoot, string backupRoot)
    {
        if (IsSameOrNested(sourceRoot, backupRoot) || IsSameOrNested(backupRoot, sourceRoot))
        {
            throw new InvalidOperationException("存档目录和备份目录不能相同或互相包含。");
        }
    }

    private static bool IsSameOrNested(string parent, string candidate)
    {
        var relative = Path.GetRelativePath(parent, candidate);
        return relative == "." ||
            (!relative.Equals("..", StringComparison.Ordinal) &&
             !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
             !Path.IsPathRooted(relative));
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
            throw new InvalidDataException("备份文件路径超出目标目录。");
        }

        return combined;
    }

}
