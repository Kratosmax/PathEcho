using System.Diagnostics;
using System.IO.Enumeration;
using PathEcho.Core.Models;
using PathEcho.Core.Sync;

internal static class ScannerOptimizationTests
{
    public static async Task RunAsync(string testRoot)
    {
        var root = Path.Combine(testRoot, "scanner-optimization");
        Directory.CreateDirectory(root);
        await TestFiltersAsync(Path.Combine(root, "filters"));
        await TestRenameAndDeleteAsync(Path.Combine(root, "changes"));
        await TestCacheLifetimeAsync(Path.Combine(root, "cache"));
        await TestReparseSkipAsync(Path.Combine(root, "reparse"));
        await TestCancellationAsync(Path.Combine(root, "cancel"));
    }

    private static async Task TestFiltersAsync(string root)
    {
        string[] paths = ["slot.sav", "cache/data.bin", "cache/deep/keep.sav", "cache2/keep.sav",
            "profiles/a/cache/x.sav", "profiles/a/save.sav", "profiles/ab/save.sav", "nested/deep/slot.sav"];
        foreach (var path in paths)
        {
            await WriteAsync(root, path, path);
        }

        string[] patterns = ["cache/*", "cache/**", "cache", "cache/", "cache/*.bin", "*/cache/*",
            "profiles/?/*", "CACHE\\*", "*", "*.sav", "cache*", "?*/c?che/*"];
        var scanner = new DirectoryScanner();
        foreach (var pattern in patterns)
        {
            foreach (var includes in new[] { Array.Empty<string>(), new[] { "*.sav" }, new[] { "cache/deep/*", "profiles/*" } })
            {
                var rules = new SyncFilterRules { IncludePatterns = includes, ExcludePatterns = [pattern] };
                var actual = await scanner.ScanAsync(root, filters: rules);
                var expected = paths.Where(path =>
                    (includes.Length == 0 || includes.Any(include => Matches(include, path))) && !Matches(pattern, path));
                Check(actual.Keys.Select(Normalize).Order().SequenceEqual(expected.Order()), $"glob result mismatch: {pattern}");
            }
        }

        var excluded = new SyncFilterRules { IncludePatterns = ["cache/deep/*"], ExcludePatterns = ["cache/*"] };
        Check(excluded.ExcludesDirectory("cache"), "complete subtree exclusion was not recognized");
        Check(!excluded.ExcludesDirectory("cache2"), "directory prefix boundary was lost");
        Check(!new SyncFilterRules { ExcludePatterns = ["cache/*.bin"] }.ExcludesDirectory("cache"), "partial exclusion pruned saves");
        Check(!new SyncFilterRules { ExcludePatterns = ["cache"] }.ExcludesDirectory("cache"), "exact directory name pruned descendants");
        var onlyExcluded = await scanner.ScanAsync(root, filters: excluded);
        Check(onlyExcluded.Count == 0, "include incorrectly overrode exclude");
        Check((await scanner.ScanAsync(root)).Count == paths.Length, "changed filters reused a filtered directory listing");
    }

    private static async Task TestRenameAndDeleteAsync(string root)
    {
        await WriteAsync(root, "folder/old.sav", "AAAA");
        await WriteAsync(root, "folder/deleted.sav", "gone");
        await WriteAsync(root, "folder2/keep.sav", "keep");
        var scanner = new DirectoryScanner();
        var before = await scanner.ScanAsync(root);
        var oldStamp = before[Path.Combine("folder", "old.sav")];
        File.Move(Path.Combine(root, "folder", "old.sav"), Path.Combine(root, "folder", "new.sav"));
        File.Delete(Path.Combine(root, "folder", "deleted.sav"));
        await WriteAsync(root, "folder/new.sav", "BBBB");
        File.SetLastWriteTimeUtc(Path.Combine(root, "folder", "new.sav"), new DateTime(oldStamp.LastWriteUtcTicks, DateTimeKind.Utc));
        scanner.InvalidatePath(root, Path.Combine(root, "folder"));
        var after = await scanner.ScanAsync(root + Path.DirectorySeparatorChar);
        Check(after.Count == 2 && after.ContainsKey(Path.Combine("folder", "new.sav")) && after.ContainsKey(Path.Combine("folder2", "keep.sav")), "rename/delete or sibling preservation failed");
        Check(after[Path.Combine("folder", "new.sav")].Sha256 != oldStamp.Sha256, "changed renamed file reused old hash");
        var renamedHash = after[Path.Combine("folder", "new.sav")].Sha256;
        await WriteAsync(root, "folder/new.sav", "CCCC");
        File.SetLastWriteTimeUtc(Path.Combine(root, "folder", "new.sav"), new DateTime(oldStamp.LastWriteUtcTicks, DateTimeKind.Utc));
        scanner.InvalidatePath(root, Path.Combine(root, "folder", "new.sav"));
        var invalidated = await scanner.ScanAsync(root);
        Check(invalidated[Path.Combine("folder", "new.sav")].Sha256 != renamedHash, "same stamp invalidation failed");
    }

    private static async Task TestCacheLifetimeAsync(string root)
    {
        var scanner = new DirectoryScanner();
        for (var i = 0; i < 24; i++)
        {
            var child = Path.Combine(root, i.ToString());
            await WriteAsync(child, "slot.sav", i.ToString());
            Check((await scanner.ScanAsync(child)).Count == 1, "cache eviction lost scan results");
            Check(scanner.CachedRootCount <= DirectoryScanner.MaximumCachedRoots, "root cache grew without a bound");
        }

        var last = Path.Combine(root, "23");
        if (OperatingSystem.IsWindows())
        {
            await scanner.ScanAsync(last.ToUpperInvariant());
            Check(scanner.CachedRootCount == DirectoryScanner.MaximumCachedRoots, "case variants duplicated root cache");
        }

        scanner.Invalidate(last);
        Check(scanner.CachedRootCount == DirectoryScanner.MaximumCachedRoots - 1 && scanner.CachedFileCount == scanner.CachedRootCount, "explicit invalidation accounting failed");
        await scanner.ScanAsync(last);
        var fake = new FileStamp(0, 0, "test");
        for (var i = 0; i <= DirectoryScanner.MaximumCachedFiles; i++)
        {
            scanner.SetCachedPath(last, "cached-" + i, fake);
        }

        Check(scanner.CachedFileCount <= DirectoryScanner.MaximumCachedFiles, "SetCachedPath exceeded total cache budget");
        Check((await scanner.ScanAsync(last)).Count == 1, "evicted cache affected file enumeration");
        await scanner.ScanAsync(last, forceHash: true);
        var roots = scanner.CachedRootCount;
        for (var i = 0; i < 24; i++)
        {
            await scanner.ScanAsync(Path.Combine(root, i.ToString()), forceHash: true);
            Check(scanner.CachedRootCount <= roots, "forceHash retained temporary roots");
        }

        Check(scanner.CachedRootCount == 0 && scanner.CachedFileCount == 0, "forced scans did not release previous root caches");
        await scanner.ScanAsync(last);
        Directory.Delete(last, true);
        Check((await scanner.ScanAsync(last)).Count == 0 && scanner.CachedRootCount == 0, "missing root retained stale cache");
    }

    private static async Task TestReparseSkipAsync(string root)
    {
        var source = Path.Combine(root, "source");
        var target = Path.Combine(root, "target");
        await WriteAsync(source, "local.sav", "local");
        await WriteAsync(target, "external.sav", "external");
        var link = Path.Combine(source, "linked");
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            start.Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"";
            using var process = Process.Start(start) ?? throw new InvalidOperationException("junction process did not start");
            await process.WaitForExitAsync();
            Check(process.ExitCode == 0, "could not create test junction");
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }

        try
        {
            Check((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "test fixture is not a reparse point");
            var actual = await new DirectoryScanner().ScanAsync(source);
            Check(actual.Count == 1 && actual.ContainsKey("local.sav"), "scanner traversed a reparse directory");
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    private static async Task TestCancellationAsync(string root)
    {
        Directory.CreateDirectory(root);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await ExpectCanceledAsync(new DirectoryScanner().ScanAsync(root, canceled.Token));
        await ExpectCanceledAsync(new DirectoryScanner().ScanAsync(Path.Combine(root, "missing"), canceled.Token));
        await WriteAsync(root, "large.sav", new string('x', 4 * 1024 * 1024));
        var scanner = new DirectoryScanner();
        using var during = new CancellationTokenSource();
        var pending = scanner.ScanAsync(root, during.Token);
        during.Cancel();
        await ExpectCanceledAsync(pending);
        Check(scanner.CachedRootCount == 0 && scanner.CachedFileCount == 0, "partial canceled scan was cached");
        Check((await scanner.ScanAsync(root)).Count == 1, "scan could not recover after cancellation");
    }

    private static async Task ExpectCanceledAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        throw new InvalidOperationException("scanner ignored cancellation");
    }

    private static async Task WriteAsync(string root, string relative, string contents)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents);
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
    private static bool Matches(string pattern, string path) => FileSystemName.MatchesSimpleExpression(Normalize(pattern), Normalize(path), true);
    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
