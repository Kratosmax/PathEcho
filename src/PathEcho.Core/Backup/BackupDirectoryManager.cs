using System.Security.Cryptography;
using System.Text.Json;
using PathEcho.Core.Sync;

namespace PathEcho.Core.Backup;

public sealed record DiscoveredBackup(
    Guid ProfileId,
    string ProfileDirectory,
    int VersionCount,
    DateTimeOffset? LatestBackupAtUtc);

public sealed class BackupDirectoryManager
{
    private static readonly JsonSerializerOptions SerializerOptions = new();

    public async Task EnsureWritableAsync(string backupDirectory, CancellationToken cancellationToken = default)
    {
        var root = NormalizeRoot(backupDirectory);
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
        var oldRoot = NormalizeRoot(oldBackupDirectory);
        var newRoot = NormalizeRoot(newBackupDirectory);
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
            await CopyAndVerifyTreeAsync(source, stage, cancellationToken).ConfigureAwait(false);
            Directory.Move(stage, destination);
            DeleteTree(source);
            return true;
        }
        catch
        {
            DeleteTree(stage);
            throw;
        }
    }

    public async Task<DiscoveredBackup> ImportAsync(
        DiscoveredBackup discovered,
        string targetBackupDirectory,
        CancellationToken cancellationToken = default)
    {
        var targetRoot = NormalizeRoot(targetBackupDirectory);
        var destination = Path.Combine(targetRoot, discovered.ProfileId.ToString("N"));
        if (Directory.Exists(destination))
        {
            throw new IOException("目标备份目录已存在该游戏，未覆盖任何备份。");
        }

        Directory.CreateDirectory(targetRoot);
        var stage = Path.Combine(targetRoot, $".{discovered.ProfileId:N}-{Guid.NewGuid():N}.importing");
        try
        {
            await CopyAndVerifyTreeAsync(discovered.ProfileDirectory, stage, cancellationToken).ConfigureAwait(false);
            Directory.Move(stage, destination);
            return discovered with { ProfileDirectory = destination };
        }
        catch
        {
            DeleteTree(stage);
            throw;
        }
    }

    public async Task<IReadOnlyList<DiscoveredBackup>> DiscoverAsync(
        string searchDirectory,
        CancellationToken cancellationToken = default)
    {
        var root = NormalizeRoot(searchDirectory);
        if (!Directory.Exists(root))
        {
            return Array.Empty<DiscoveredBackup>();
        }

        var grouped = new Dictionary<Guid, List<(string Manifest, SnapshotManifest Data)>>();
        foreach (var manifestPath in Directory.EnumerateFiles(root, "manifest.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(manifestPath);
                var manifest = await JsonSerializer.DeserializeAsync<SnapshotManifest>(stream, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                if (manifest is null)
                {
                    continue;
                }

                if (!grouped.TryGetValue(manifest.ProfileId, out var manifests))
                {
                    manifests = new List<(string, SnapshotManifest)>();
                    grouped.Add(manifest.ProfileId, manifests);
                }

                manifests.Add((manifestPath, manifest));
            }
            catch (JsonException)
            {
            }
            catch (InvalidDataException)
            {
            }
        }

        return grouped.Select(pair =>
        {
            var firstManifest = pair.Value[0].Manifest;
            var snapshotsRoot = Directory.GetParent(Path.GetDirectoryName(firstManifest)!)?.FullName
                ?? throw new InvalidDataException("无法识别备份目录结构。");
            var profileRoot = Directory.GetParent(snapshotsRoot)?.FullName
                ?? throw new InvalidDataException("无法识别游戏备份根目录。");
            return new DiscoveredBackup(
                pair.Key,
                profileRoot,
                pair.Value.Count,
                pair.Value.Max(item => item.Data.CreatedAtUtc));
        }).OrderByDescending(item => item.LatestBackupAtUtc).ToArray();
    }

    private static async Task CopyAndVerifyTreeAsync(
        string sourceRoot,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceRoot, source);
            var destination = SafeCombine(destinationRoot, relative);
            await AtomicFileOperations.CopyAsync(source, destination, cancellationToken).ConfigureAwait(false);
            var sourceHash = await ComputeHashAsync(source, cancellationToken).ConfigureAwait(false);
            var destinationHash = await ComputeHashAsync(destination, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(sourceHash, destinationHash, StringComparison.Ordinal))
            {
                throw new IOException($"移动备份校验失败：{relative}");
            }

            File.SetAttributes(destination, File.GetAttributes(source));
        }
    }

    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static void ValidateSeparateRoots(string first, string second)
    {
        if (IsSameOrNested(first, second) || IsSameOrNested(second, first))
        {
            throw new InvalidOperationException("新旧备份目录不能相同或互相包含。");
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
            throw new InvalidOperationException("备份目录不能为空。");
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
