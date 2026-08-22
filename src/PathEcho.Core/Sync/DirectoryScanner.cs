namespace PathEcho.Core.Sync;

public sealed class DirectoryScanner
{
    private readonly Dictionary<string, Dictionary<string, FileStamp>> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyDictionary<string, FileStamp>> ScanAsync(string root, CancellationToken cancellationToken = default)
    {
        root = Path.GetFullPath(root);
        var files = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root))
        {
            return files;
        }

        _cache.TryGetValue(root, out var cachedFiles);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, path);
            var info = new FileInfo(path);
            if (cachedFiles is not null &&
                cachedFiles.TryGetValue(relative, out var cached) &&
                cached.Length == info.Length &&
                cached.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks)
            {
                files[relative] = cached;
            }
            else
            {
                files[relative] = await FileStamp.CreateAsync(path, cancellationToken).ConfigureAwait(false);
            }
        }

        _cache[root] = files;
        return files;
    }

    public void Invalidate(string root) => _cache.Remove(Path.GetFullPath(root));
}
