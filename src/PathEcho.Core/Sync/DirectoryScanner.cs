using PathEcho.Core.IO;

namespace PathEcho.Core.Sync;

public sealed class DirectoryScanner
{
    private readonly Dictionary<string, Dictionary<string, FileStamp>> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyDictionary<string, FileStamp>> ScanAsync(
        string root,
        CancellationToken cancellationToken = default,
        PathEcho.Core.Models.SyncFilterRules? filters = null,
        bool forceHash = false)
    {
        root = Path.GetFullPath(root);
        var files = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root))
        {
            return files;
        }

        _cache.TryGetValue(root, out var cachedFiles);
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
        };
        foreach (var path in Directory.EnumerateFiles(root, "*", enumerationOptions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, path);
            if (filters is not null && !filters.Includes(relative))
            {
                continue;
            }

            var info = new FileInfo(path);
            if (!forceHash &&
                cachedFiles is not null &&
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

    public void InvalidatePath(string root, string changedPath)
    {
        root = Path.GetFullPath(root);
        if (!_cache.TryGetValue(root, out var cachedFiles))
        {
            return;
        }

        if (!SafePath.TryGetRelativePath(root, Path.GetFullPath(changedPath), out var relativePath))
        {
            _cache.Remove(root);
            return;
        }

        var prefix = Path.TrimEndingDirectorySeparator(relativePath) + Path.DirectorySeparatorChar;
        foreach (var key in cachedFiles.Keys
                     .Where(key => string.Equals(key, relativePath, StringComparison.OrdinalIgnoreCase) ||
                         key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            cachedFiles.Remove(key);
        }
    }

    public void SetCachedPath(string root, string relativePath, FileStamp stamp)
    {
        root = Path.GetFullPath(root);
        if (_cache.TryGetValue(root, out var cachedFiles))
        {
            cachedFiles[relativePath] = stamp;
        }
    }
}
