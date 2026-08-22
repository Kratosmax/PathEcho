using System.Security.Cryptography;

namespace PathEcho.Core.Sync;

public sealed record FileStamp(long Length, long LastWriteUtcTicks, string Sha256)
{
    public static async Task<FileStamp> CreateAsync(string path, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks, Convert.ToHexString(hash));
    }

    public bool ContentEquals(FileStamp? other) => other is not null && Length == other.Length && Sha256 == other.Sha256;
}

public sealed record SyncBaselineEntry(FileStamp? Left, FileStamp? Right);

public sealed record SyncBaseline(IReadOnlyDictionary<string, SyncBaselineEntry> Files)
{
    public static SyncBaseline Empty { get; } = new(
        new Dictionary<string, SyncBaselineEntry>(StringComparer.OrdinalIgnoreCase));
}
