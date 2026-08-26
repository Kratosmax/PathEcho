using System.Text.Json;
using PathEcho.Core.IO;
using PathEcho.Core.Sync;

namespace PathEcho.Core.Backup;

public static class SnapshotContent
{
    private static readonly JsonSerializerOptions SerializerOptions = new();

    public static async Task<SnapshotManifest> ReadManifestAsync(
        string snapshotDirectory,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(snapshotDirectory, "manifest.json");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        return await JsonSerializer.DeserializeAsync<SnapshotManifest>(stream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException($"备份清单无效：{path}");
    }

    public static string ResolveFile(string snapshotDirectory, SnapshotFileEntry entry)
    {
        var profileRoot = GetProfileRoot(snapshotDirectory);
        var objectPath = GetObjectPath(profileRoot, entry.Sha256);
        if (File.Exists(objectPath))
        {
            return objectPath;
        }

        var legacyPath = SafePath.CombineUnderRoot(
            Path.Combine(snapshotDirectory, "files"),
            entry.RelativePath,
            "快照文件路径超出快照目录。");
        return File.Exists(legacyPath)
            ? legacyPath
            : throw new InvalidDataException($"快照缺少文件：{entry.RelativePath}");
    }

    public static async Task<string> ResolveVerifiedFileAsync(
        string snapshotDirectory,
        SnapshotFileEntry entry,
        CancellationToken cancellationToken = default)
    {
        var profileRoot = GetProfileRoot(snapshotDirectory);
        var objectPath = GetObjectPath(profileRoot, entry.Sha256);
        if (await HasExpectedHashAsync(objectPath, entry.Sha256, cancellationToken).ConfigureAwait(false))
        {
            return objectPath;
        }

        var legacyPath = SafePath.CombineUnderRoot(
            Path.Combine(snapshotDirectory, "files"),
            entry.RelativePath,
            "快照文件路径超出快照目录。");
        if (await HasExpectedHashAsync(legacyPath, entry.Sha256, cancellationToken).ConfigureAwait(false))
        {
            return legacyPath;
        }

        throw new InvalidDataException($"快照文件缺失或校验失败：{entry.RelativePath}");
    }

    public static async Task MaterializeBrowseCopyAsync(
        string snapshotDirectory,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        var manifest = await ReadManifestAsync(snapshotDirectory, cancellationToken).ConfigureAwait(false);
        DirectoryTree.DeleteIfPresent(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);
        try
        {
            foreach (var entry in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = await ResolveVerifiedFileAsync(snapshotDirectory, entry, cancellationToken)
                    .ConfigureAwait(false);
                var destination = SafePath.CombineUnderRoot(
                    destinationDirectory,
                    entry.RelativePath,
                    "浏览副本路径超出目标目录。");
                await AtomicFileOperations.CopyAsync(source, destination, cancellationToken).ConfigureAwait(false);
                var actualHash = await ContentHash.ComputeAsync(destination, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actualHash, entry.Sha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"浏览副本校验失败：{entry.RelativePath}");
                }

                File.SetAttributes(destination, FileAttributes.Normal);
                File.SetLastWriteTimeUtc(destination, new DateTime(entry.LastWriteUtcTicks, DateTimeKind.Utc));
            }
        }
        catch
        {
            DirectoryTree.DeleteIfPresent(destinationDirectory);
            throw;
        }
    }

    public static string GetObjectPath(string profileRoot, string hash)
    {
        if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("快照对象哈希格式无效。");
        }

        return Path.Combine(profileRoot, "objects", hash[..2], $"{hash}.blob");
    }

    public static string GetProfileRoot(string snapshotDirectory)
    {
        var snapshotsRoot = Directory.GetParent(Path.GetFullPath(snapshotDirectory))
            ?? throw new InvalidDataException("快照目录缺少 snapshots 父目录。");
        return snapshotsRoot.Parent?.FullName
            ?? throw new InvalidDataException("快照目录缺少游戏备份根目录。");
    }

    private static async Task<bool> HasExpectedHashAsync(
        string path,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        return string.Equals(
            await ContentHash.ComputeAsync(path, cancellationToken).ConfigureAwait(false),
            expectedHash,
            StringComparison.Ordinal);
    }
}
