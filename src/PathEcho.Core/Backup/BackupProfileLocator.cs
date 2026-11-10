namespace PathEcho.Core.Backup;

internal sealed class BackupProfileLocator
{
    private static readonly TimeSpan SearchLifetime = TimeSpan.FromSeconds(30);
    private readonly Dictionary<string, (DateTimeOffset Expires, string[] Paths)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    internal void Invalidate()
    {
        lock (_gate) { _cache.Clear(); }
    }

    internal string[] Find(string root, IReadOnlySet<Guid> requestedIds, bool includeOtherProfiles, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsRegularDirectory(root)) return [];
        var direct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIfProfile(root);
        if (includeOtherProfiles)
        {
            foreach (var directory in SafeDirectories(root, cancellationToken))
            {
                if (Guid.TryParse(Path.GetFileName(directory), out _)) AddIfProfile(directory);
            }
        }
        foreach (var id in requestedIds)
        {
            AddIfProfile(Path.Combine(root, id.ToString("N")));
            AddIfProfile(Path.Combine(root, id.ToString("D")));
        }

        var key = root + "|" + string.Join(",", requestedIds.Order());
        string[] located;
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow &&
                cached.Paths.All(path => IsSafePath(root, path) && IsRegularDirectory(Path.Combine(path, "snapshots"))))
            {
                located = cached.Paths;
            }
            else
            {
                located = null!;
            }
        }
        if (located is null)
        {
            located = SearchKnownProfiles(root, requestedIds, cancellationToken).ToArray();
            lock (_gate)
            {
                if (_cache.Count >= 8 && !_cache.ContainsKey(key))
                {
                    _cache.Remove(_cache.MinBy(pair => pair.Value.Expires).Key);
                }
                _cache[key] = (DateTimeOffset.UtcNow + SearchLifetime, located);
            }
        }
        foreach (var directory in located) AddIfProfile(directory);
        return direct.ToArray();

        void AddIfProfile(string directory)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSafePath(root, directory) && IsRegularDirectory(Path.Combine(directory, "snapshots")))
            {
                direct.Add(directory);
            }
        }
    }

    // Fresh, bounded discovery for reports. The legacy cached Find entry remains compatible.
    internal static IReadOnlyList<string> FindForScan(
        string root,
        IReadOnlySet<Guid> requestedIds,
        bool includeOtherProfiles,
        HistoryTraversalBudget budget,
        Action<SnapshotInspection> issue,
        Action<string, string>? probe = null)
    {
        var result = new List<string>();
        var traversalFailed = false;
        void RecordIssue(SnapshotInspection inspection)
        {
            if (inspection.ReasonCode != HistoryReasonCodes.SOURCE_UNASSOCIATED) traversalFailed = true;
            issue(inspection);
        }
        var foundIds = new HashSet<Guid>();
        var pending = new Stack<(string Path, int Depth)>();
        if (!budget.Visit(root) || !CheckScanDirectory(root, "root.checked", RecordIssue, true, probe)) return result;
        pending.Push((root, 0));
        while (pending.TryPop(out var item))
        {
            budget.CheckCancellation();
            var snapshots = Path.Combine(item.Path, "snapshots");
            if (!budget.Visit(snapshots)) break;
            if (CheckScanDirectory(snapshots, "source.located", RecordIssue, false, probe))
            {
                var hasId = Guid.TryParse(Path.GetFileName(item.Path), out var id);
                if ((hasId && requestedIds.Contains(id)) ||
                    (includeOtherProfiles && item.Depth <= 1 && hasId) ||
                    (item.Depth == 0 && !hasId))
                {
                    result.Add(item.Path);
                    if (hasId) foundIds.Add(id);
                }
                else
                {
                    issue(new SnapshotInspection
                    {
                        SnapshotDirectory = item.Path,
                        SourceProfileId = hasId ? id : null,
                        Stage = "source.located",
                        ReasonCode = HistoryReasonCodes.SOURCE_UNASSOCIATED
                    });
                }
                // Never discover through objects, snapshots, or another profile's payload.
                continue;
            }
            foreach (var child in EnumerateForScan(item.Path, budget, RecordIssue, "source.located"))
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith(".", StringComparison.Ordinal) ||
                    name.Equals("objects", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("snapshots", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("temp", StringComparison.OrdinalIgnoreCase)) continue;
                if (CheckScanDirectory(child, "source.located", RecordIssue, false, probe)) pending.Push((child, item.Depth + 1));
            }
            if (budget.LimitReached) break;
        }
        if (!budget.LimitReached && !traversalFailed)
        {
            foreach (var id in requestedIds.Where(id => !foundIds.Contains(id)))
            {
                // A non-GUID root is validated by each manifest instead of its directory name.
                if (result.Any(path => !Guid.TryParse(Path.GetFileName(path), out _))) break;
                issue(new SnapshotInspection
                {
                    SnapshotDirectory = root,
                    SourceProfileId = id,
                    Stage = "source.located",
                    ReasonCode = HistoryReasonCodes.SOURCE_NOT_FOUND
                });
            }
        }
        return result;
    }

    internal static bool CheckScanDirectory(string path, string stage,
        Action<SnapshotInspection> issue, bool root, Action<string, string>? probe = null)
    {
        try
        {
            probe?.Invoke(stage, path);
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0) throw new DirectoryNotFoundException();
            if (new DirectoryInfo(path).LinkTarget is not null)
            {
                issue(new SnapshotInspection { SnapshotDirectory = path, Stage = stage, ReasonCode = HistoryReasonCodes.LINK_SKIPPED });
                return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!root && (exception is FileNotFoundException or DirectoryNotFoundException)) return false;
            issue(new SnapshotInspection
            {
                SnapshotDirectory = path,
                Stage = stage,
                ReasonCode = exception is UnauthorizedAccessException
                    ? (root ? HistoryReasonCodes.ROOT_ACCESS_DENIED : HistoryReasonCodes.OBJECT_ACCESS_DENIED)
                    : exception is FileNotFoundException or DirectoryNotFoundException
                        ? HistoryReasonCodes.ROOT_NOT_FOUND : HistoryReasonCodes.IO_ERROR,
                ErrorType = exception.GetType().Name,
                HResult = exception.HResult
            });
            return false;
        }
    }

    internal static IEnumerable<string> EnumerateForScan(string directory, HistoryTraversalBudget budget,
        Action<SnapshotInspection> issue, string stage = "snapshot.enumerated")
    {
        IEnumerator<string> iterator;
        try { iterator = Directory.EnumerateDirectories(directory).GetEnumerator(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RecordEnumerationError(exception);
            yield break;
        }
        using (iterator)
        {
            while (true)
            {
                budget.CheckCancellation();
                string child;
                try
                {
                    if (!iterator.MoveNext()) yield break;
                    child = iterator.Current;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    RecordEnumerationError(exception);
                    yield break;
                }
                if (!budget.Visit(child)) yield break;
                yield return child;
            }
        }
        void RecordEnumerationError(Exception exception) => issue(new SnapshotInspection
        {
            SnapshotDirectory = directory,
            Stage = stage,
            ReasonCode = exception is UnauthorizedAccessException ? HistoryReasonCodes.OBJECT_ACCESS_DENIED : HistoryReasonCodes.IO_ERROR,
            ErrorType = exception.GetType().Name,
            HResult = exception.HResult,
        });
    }

    // Metadata-only fallback for explicitly known identities. Never infer game identity from names.
    private static IEnumerable<string> SearchKnownProfiles(string root, IReadOnlySet<Guid> ids, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var visited = 0;
        while (visited++ < 10000 && pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsRegularDirectory(directory)) continue;
            if (IsRegularDirectory(Path.Combine(directory, "snapshots")))
            {
                if (Guid.TryParse(Path.GetFileName(directory), out var id) && ids.Contains(id)) yield return directory;
                continue; // Content objects and snapshot payloads are not discovery paths.
            }
            foreach (var child in SafeDirectories(directory, cancellationToken))
            {
                var name = Path.GetFileName(child);
                if (!name.StartsWith(".", StringComparison.Ordinal) &&
                    !name.Equals("objects", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("snapshots", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("temp", StringComparison.OrdinalIgnoreCase) && visited + pending.Count < 10000) pending.Push(child);
            }
        }
    }

    private static bool IsSafePath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return false;
        var current = root;
        if (!IsRegularDirectory(current)) return false;
        if (relative == ".") return true;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (!IsRegularDirectory(current)) return false;
        }
        return true;
    }

    private static string[] SafeDirectories(string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsRegularDirectory(directory)) return [];
        try { return Directory.EnumerateDirectories(directory).Take(10000).ToArray(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return []; }
    }

    // Cloud placeholders may be reparse points without being path-redirecting links.
    // LinkTarget identifies symbolic links and junctions without rejecting cloud tags wholesale.
    internal static bool IsRegularDirectory(string directory)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            return info.Exists && (info.Attributes & FileAttributes.Directory) != 0 && info.LinkTarget is null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }
}

// Shared by discovery and snapshot enumeration, including pending traversal work.
internal sealed class HistoryTraversalBudget(int maximumDirectories, CancellationToken cancellationToken)
{
    private readonly HashSet<string> _visited = new(StringComparer.OrdinalIgnoreCase);
    internal bool LimitReached { get; private set; }
    internal void CheckCancellation() => cancellationToken.ThrowIfCancellationRequested();
    internal bool Visit(string path)
    {
        CheckCancellation();
        if (_visited.Contains(path)) return true;
        if (_visited.Count >= maximumDirectories) { LimitReached = true; return false; }
        _visited.Add(path);
        return true;
    }
}
