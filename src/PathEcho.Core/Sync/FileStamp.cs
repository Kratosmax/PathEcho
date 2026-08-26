using PathEcho.Core.IO;

namespace PathEcho.Core.Sync;

public sealed record FileStamp(long Length, long LastWriteUtcTicks, string Sha256)
{
    public static async Task<FileStamp> CreateAsync(string path, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            info.Refresh();
            var lengthBefore = info.Length;
            var writeTimeBefore = info.LastWriteTimeUtc.Ticks;
            var hash = await ContentHash.ComputeAsync(path, cancellationToken).ConfigureAwait(false);
            info.Refresh();
            if (lengthBefore == info.Length && writeTimeBefore == info.LastWriteTimeUtc.Ticks)
            {
                return new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks, hash);
            }
        }

        throw new IOException($"文件在读取期间持续变化：{path}");
    }

    public bool ContentEquals(FileStamp? other) => other is not null && Length == other.Length && Sha256 == other.Sha256;
}

public sealed record SyncBaselineEntry(FileStamp? Left, FileStamp? Right);

public sealed record SyncBaseline(IReadOnlyDictionary<string, SyncBaselineEntry> Files)
{
    public static SyncBaseline Empty { get; } = new(
        new Dictionary<string, SyncBaselineEntry>(StringComparer.OrdinalIgnoreCase));
}
