using PathEcho.Core.IO;

namespace PathEcho.Core.Sync;

public sealed class DirectoryScanner
{
    internal const int MaximumCachedRoots = 8;
    internal const int MaximumCachedFiles = 50_000;

    private readonly Dictionary<string, Dictionary<string, FileStamp>> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _cacheOrder = new();
    private int _cachedFileCount;

    internal int CachedRootCount => _cache.Count;
    internal int CachedFileCount => _cachedFileCount;

    public async Task<IReadOnlyDictionary<string, FileStamp>> ScanAsync(
        string root,
        CancellationToken cancellationToken = default,
        PathEcho.Core.Models.SyncFilterRules? filters = null,
        bool forceHash = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        root = NormalizeRoot(root);
        var files = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        if (forceHash || !Directory.Exists(root))
        {
            Invalidate(root);
            if (!Directory.Exists(root))
            {
                return files;
            }
        }

        _cache.TryGetValue(root, out var cachedFiles);
        var enumerationOptions = new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
        };
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", enumerationOptions))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(root, entry.FullName);
                if (entry is DirectoryInfo)
                {
                    if (filters is null || !filters.ExcludesDirectory(relative))
                    {
                        directories.Push(entry.FullName);
                    }

                    continue;
                }

                if (filters is not null && !filters.Includes(relative))
                {
                    continue;
                }

                var info = (FileInfo)entry;
                if (cachedFiles is not null &&
                    cachedFiles.TryGetValue(relative, out var cached) &&
                    cached.Length == info.Length &&
                    cached.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks)
                {
                    files[relative] = cached;
                }
                else
                {
                    files[relative] = await FileStamp.CreateAsync(entry.FullName, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!forceHash)
        {
            Cache(root, files);
        }

        return files;
    }

    public void Invalidate(string root)
    {
        root = NormalizeRoot(root);
        if (_cache.Remove(root, out var removed))
        {
            _cachedFileCount -= removed.Count;
            _cacheOrder.Remove(_cacheOrder.First(cachedRoot => string.Equals(cachedRoot, root, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private void Cache(string root, Dictionary<string, FileStamp> files)
    {
        Invalidate(root);
        // A large scan still returns every result; only its optional hash cache is dropped.
        if (files.Count > MaximumCachedFiles)
        {
            return;
        }

        while (_cache.Count >= MaximumCachedRoots || _cachedFileCount + files.Count > MaximumCachedFiles)
        {
            Invalidate(_cacheOrder.First!.Value);
        }

        _cache[root] = files;
        _cacheOrder.AddLast(root);
        _cachedFileCount += files.Count;
    }

    private static string NormalizeRoot(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    public void InvalidatePath(string root, string changedPath)
    {
        root = NormalizeRoot(root);
        if (!_cache.TryGetValue(root, out var cachedFiles))
        {
            return;
        }

        if (!SafePath.TryGetRelativePath(root, Path.GetFullPath(changedPath), out var relativePath))
        {
            Invalidate(root);
            return;
        }

        var prefix = Path.TrimEndingDirectorySeparator(relativePath) + Path.DirectorySeparatorChar;
        foreach (var key in cachedFiles.Keys
                     .Where(key => string.Equals(key, relativePath, StringComparison.OrdinalIgnoreCase) ||
                         key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            cachedFiles.Remove(key);
            _cachedFileCount--;
        }
    }

    public void SetCachedPath(string root, string relativePath, FileStamp stamp)
    {
        root = NormalizeRoot(root);
        if (_cache.TryGetValue(root, out var cachedFiles))
        {
            if (!cachedFiles.ContainsKey(relativePath))
            {
                if (_cachedFileCount >= MaximumCachedFiles)
                {
                    Invalidate(root);
                    return;
                }

                _cachedFileCount++;
            }

            cachedFiles[relativePath] = stamp;
        }
    }
}
