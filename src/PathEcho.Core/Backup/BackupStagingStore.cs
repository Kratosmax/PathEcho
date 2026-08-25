using System.Text.Json;
using PathEcho.Core.Sync;

namespace PathEcho.Core.Backup;

public interface IBackupStagingStore
{
    Task<string> CreateAsync(string sourceDirectory, string transactionDirectory, CancellationToken cancellationToken);

    void DeleteIfPresent(string transactionDirectory);
}

public sealed class BackupStagingStore : IBackupStagingStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly DirectoryScanner _scanner = new();

    public async Task<string> CreateAsync(
        string sourceDirectory,
        string transactionDirectory,
        CancellationToken cancellationToken)
    {
        DeleteIfPresent(transactionDirectory);
        var filesDirectory = Path.Combine(transactionDirectory, "files");

        try
        {
            var sourceFiles = await _scanner.ScanAsync(sourceDirectory, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(filesDirectory);
            foreach (var relativePath in sourceFiles.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await AtomicFileOperations.CopyAsync(
                    Path.Combine(sourceDirectory, relativePath),
                    Path.Combine(filesDirectory, relativePath),
                    cancellationToken).ConfigureAwait(false);
            }

            var stagedFiles = await _scanner.ScanAsync(filesDirectory, cancellationToken).ConfigureAwait(false);
            if (!HaveSameContents(sourceFiles, stagedFiles))
            {
                throw new IOException("临时存档副本校验失败，源文件可能仍在变化。");
            }

            var marker = new BackupStagingMarker(
                DateTimeOffset.UtcNow,
                Path.GetFullPath(sourceDirectory),
                sourceFiles.Count);
            await using var markerStream = new FileStream(
                Path.Combine(transactionDirectory, "staging.json"),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                true);
            await JsonSerializer.SerializeAsync(markerStream, marker, SerializerOptions, cancellationToken).ConfigureAwait(false);
            await markerStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return filesDirectory;
        }
        catch
        {
            DeleteIfPresent(transactionDirectory);
            throw;
        }
    }

    public void DeleteIfPresent(string transactionDirectory) =>
        DirectoryTree.DeleteIfPresent(transactionDirectory);

    private static bool HaveSameContents(
        IReadOnlyDictionary<string, FileStamp> expected,
        IReadOnlyDictionary<string, FileStamp> actual)
    {
        if (expected.Count != actual.Count)
        {
            return false;
        }

        return expected.All(pair => actual.TryGetValue(pair.Key, out var actualStamp) &&
            pair.Value.Length == actualStamp.Length &&
            string.Equals(pair.Value.Sha256, actualStamp.Sha256, StringComparison.Ordinal));
    }

    private sealed record BackupStagingMarker(DateTimeOffset CreatedAtUtc, string SourceDirectory, int FileCount);
}
