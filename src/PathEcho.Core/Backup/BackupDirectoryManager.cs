using System.Text.Json;
using PathEcho.Core.IO;
using PathEcho.Core.Sync;

namespace PathEcho.Core.Backup;

public sealed record DiscoveredBackup(
    Guid ProfileId,
    string ProfileDirectory,
    int VersionCount,
    DateTimeOffset? LatestBackupAtUtc);

public sealed class BackupDirectoryManager
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public async Task EnsureWritableAsync(string backupDirectory, CancellationToken cancellationToken = default)
    {
        var root = SafePath.NormalizeDirectory(backupDirectory, "备份目录不能为空。");
        Directory.CreateDirectory(root);
        var probe = Path.Combine(root, $".pathecho-write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            await using var stream = new FileStream(
                probe,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync("PathEcho"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            if (File.Exists(probe))
            {
                File.Delete(probe);
            }
        }
    }

    public async Task<bool> MoveProfileAsync(
        Guid profileId,
        string oldBackupDirectory,
        string newBackupDirectory,
        CancellationToken cancellationToken = default)
    {
        var oldRoot = SafePath.NormalizeDirectory(oldBackupDirectory, "原备份目录不能为空。");
        var newRoot = SafePath.NormalizeDirectory(newBackupDirectory, "新备份目录不能为空。");
        if (string.Equals(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var source = Path.Combine(oldRoot, profileId.ToString("N"));
        if (!Directory.Exists(source))
        {
            return false;
        }

        ValidateSeparateRoots(oldRoot, newRoot);
        Directory.CreateDirectory(newRoot);
        var destination = Path.Combine(newRoot, profileId.ToString("N"));
        if (Directory.Exists(destination))
        {
            throw new IOException($"新备份目录已存在该游戏的备份：{destination}");
        }

        try
        {
            Directory.Move(source, destination);
            return true;
        }
        catch (IOException) when (!Directory.Exists(destination))
        {
        }

        var stage = Path.Combine(newRoot, $".{profileId:N}-{Guid.NewGuid():N}.moving");
        try
        {
            await CopyAndVerifyProfileAsync(source, stage, cancellationToken).ConfigureAwait(false);
            Directory.Move(stage, destination);
            DirectoryTree.DeleteIfPresent(source);
            return true;
        }
        catch
        {
            DirectoryTree.DeleteIfPresent(stage);
            throw;
        }
    }

    public async Task<DiscoveredBackup> ImportAsync(
        DiscoveredBackup discovered,
        string targetBackupDirectory,
        CancellationToken cancellationToken = default)
    {
        var targetRoot = SafePath.NormalizeDirectory(targetBackupDirectory, "目标备份目录不能为空。");
        var destination = Path.Combine(targetRoot, discovered.ProfileId.ToString("N"));
        if (Directory.Exists(destination))
        {
            throw new IOException("目标备份目录已存在该游戏，未覆盖任何备份。");
        }

        Directory.CreateDirectory(targetRoot);
        var stage = Path.Combine(targetRoot, $".{discovered.ProfileId:N}-{Guid.NewGuid():N}.importing");
        try
        {
            await CopyAndVerifyProfileAsync(discovered.ProfileDirectory, stage, cancellationToken).ConfigureAwait(false);
            Directory.Move(stage, destination);
            return discovered with { ProfileDirectory = destination };
        }
        catch
        {
            DirectoryTree.DeleteIfPresent(stage);
            throw;
        }
    }

    public async Task<IReadOnlyList<DiscoveredBackup>> DiscoverAsync(
        string searchDirectory,
        CancellationToken cancellationToken = default)
    {
        var root = SafePath.NormalizeDirectory(searchDirectory, "搜索目录不能为空。");
        if (!Directory.Exists(root))
        {
            return Array.Empty<DiscoveredBackup>();
        }

        var grouped = new Dictionary<string, List<SnapshotManifest>>(StringComparer.OrdinalIgnoreCase);
        var roots = new Dictionary<string, (Guid ProfileId, string ProfileRoot)>(StringComparer.OrdinalIgnoreCase);
        foreach (var manifestPath in Directory.EnumerateFiles(root, "manifest.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var snapshotRoot = Path.GetDirectoryName(manifestPath)
                    ?? throw new InvalidDataException("快照清单缺少父目录。");
                var snapshotsRoot = Directory.GetParent(snapshotRoot)
                    ?? throw new InvalidDataException("快照目录缺少 snapshots 父目录。");
                if (!snapshotsRoot.Name.Equals("snapshots", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var profileRoot = snapshotsRoot.Parent?.FullName
                    ?? throw new InvalidDataException("快照目录缺少游戏备份根目录。");
                var manifest = await SnapshotContent.ReadManifestAsync(snapshotRoot, cancellationToken).ConfigureAwait(false);
                var key = $"{manifest.ProfileId:N}|{Path.GetFullPath(profileRoot)}";
                if (!grouped.TryGetValue(key, out var manifests))
                {
                    manifests = new List<SnapshotManifest>();
                    grouped.Add(key, manifests);
                    roots.Add(key, (manifest.ProfileId, profileRoot));
                }

                manifests.Add(manifest);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
            }
        }

        return grouped.Select(pair => new DiscoveredBackup(
                roots[pair.Key].ProfileId,
                roots[pair.Key].ProfileRoot,
                pair.Value.Count,
                pair.Value.Max(item => item.CreatedAtUtc)))
            .OrderByDescending(item => item.LatestBackupAtUtc)
            .ToArray();
    }

    private static async Task CopyAndVerifyProfileAsync(
        string sourceProfileRoot,
        string destinationProfileRoot,
        CancellationToken cancellationToken)
    {
        var sourceSnapshotsRoot = Path.Combine(sourceProfileRoot, "snapshots");
        if (!Directory.Exists(sourceSnapshotsRoot))
        {
            throw new InvalidDataException("备份缺少 snapshots 目录。");
        }

        var copiedObjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sourceSnapshot in Directory.EnumerateDirectories(sourceSnapshotsRoot)
                     .Where(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = await SnapshotContent.ReadManifestAsync(sourceSnapshot, cancellationToken).ConfigureAwait(false);
            foreach (var entry in manifest.Files)
            {
                if (!copiedObjects.Add(entry.Sha256))
                {
                    continue;
                }

                var source = await SnapshotContent.ResolveVerifiedFileAsync(sourceSnapshot, entry, cancellationToken)
                    .ConfigureAwait(false);

                var destination = SnapshotContent.GetObjectPath(destinationProfileRoot, entry.Sha256);
                await AtomicFileOperations.CopyAsync(source, destination, cancellationToken).ConfigureAwait(false);
                var destinationHash = await ContentHash.ComputeAsync(destination, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(destinationHash, entry.Sha256, StringComparison.Ordinal))
                {
                    throw new IOException($"移动备份对象校验失败：{entry.RelativePath}");
                }
            }

            var destinationSnapshot = Path.Combine(
                destinationProfileRoot,
                "snapshots",
                Path.GetFileName(sourceSnapshot));
            Directory.CreateDirectory(destinationSnapshot);
            await WriteManifestAsync(
                Path.Combine(destinationSnapshot, "manifest.json"),
                manifest with { SchemaVersion = 2 },
                cancellationToken).ConfigureAwait(false);
        }

        if (!Directory.EnumerateDirectories(sourceSnapshotsRoot)
                .Any(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("备份中没有可导入的快照。");
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

    private static void ValidateSeparateRoots(string first, string second)
    {
        if (SafePath.IsSameOrNested(first, second) || SafePath.IsSameOrNested(second, first))
        {
            throw new InvalidOperationException("新旧备份目录不能相同或互相包含。");
        }
    }
}
