using System.Diagnostics;
using PathEcho.Core.Models;
using PathEcho.Core.Sync;
using PathEcho.Core.Storage;

internal static class SyncRootSafetyTests
{
    internal static async Task RunAsync(string testRoot)
    {
        foreach (var mode in new[] { SyncMode.LeftToRight, SyncMode.RightToLeft, SyncMode.Bidirectional })
        {
            var root = Path.Combine(testRoot, "sync-root-safety", mode.ToString());
            var left = Path.Combine(root, "left"); var right = Path.Combine(root, "right");
            Directory.CreateDirectory(left); Directory.CreateDirectory(right);
            await File.WriteAllTextAsync(Path.Combine(left, "slot.sav"), "LEFT");
            await File.WriteAllTextAsync(Path.Combine(right, "slot.sav"), "RIGHT");
            var task = new SyncTaskDefinition
            {
                LeftPath = left,
                RightPath = right,
                Mode = mode,
                DeletionMode = DeletionMode.Propagate,
                ConflictPolicy = ConflictPolicy.KeepBoth
            };
            var engine = new SyncEngine(Path.Combine(root, "vault"));
            var baseline = (await engine.RunAsync(task, SyncBaseline.Empty)).Baseline;
            var missing = mode == SyncMode.RightToLeft ? right : left;
            var survivor = mode == SyncMode.RightToLeft ? left : right;
            var before = Directory.GetFiles(survivor).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
            Directory.Move(missing, missing + "-offline");
            var failed = false;
            try { await engine.RunAsync(task, baseline, forceFullScan: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { failed = true; }
            if (!failed || Directory.Exists(missing)) throw new InvalidOperationException("Missing sync source was recreated and treated as empty.");
            foreach (var pair in before)
            {
                var bytes = await File.ReadAllBytesAsync(Path.Combine(survivor, pair.Key!));
                if (!pair.Value.SequenceEqual(bytes)) throw new InvalidOperationException("Offline source removed or replaced surviving files.");
            }
            if (Directory.GetFiles(survivor).Length != before.Count) throw new InvalidOperationException("Offline source propagated deletion.");
        }
        await TestTargetJunctionAsync(Path.Combine(testRoot, "sync-root-safety", "target-junction"));
        await TestBaselineFailureAsync(Path.Combine(testRoot, "sync-root-safety", "baseline-failure"));
        await TestMonitorMissingSourceAsync(Path.Combine(testRoot, "sync-root-safety", "monitor-source"));
        var oneWayRoot = Path.Combine(testRoot, "sync-root-safety", "new-destination");
        var source = Path.Combine(oneWayRoot, "source"); var destination = Path.Combine(oneWayRoot, "destination");
        Directory.CreateDirectory(source); await File.WriteAllTextAsync(Path.Combine(source, "file.txt"), "COPY");
        await new SyncEngine(Path.Combine(oneWayRoot, "vault")).RunAsync(new SyncTaskDefinition { LeftPath = source, RightPath = destination }, SyncBaseline.Empty);
        if (await File.ReadAllTextAsync(Path.Combine(destination, "file.txt")) != "COPY") throw new InvalidOperationException("A valid new one-way destination can no longer be created.");
    }
    private static async Task TestMonitorMissingSourceAsync(string root)
    {
        var left = Path.Combine(root, "missing-source"); var right = Path.Combine(root, "destination");
        Directory.CreateDirectory(right); await File.WriteAllTextAsync(Path.Combine(right, "survivor.sav"), "KEEP");
        var task = new SyncTaskDefinition { LeftPath = left, RightPath = right, DeletionMode = DeletionMode.Propagate };
        var engine = new SyncEngine(Path.Combine(root, "vault"));
        await using var monitor = new SyncTaskMonitor(task, engine, new SyncBaselineStore(Path.Combine(root, "baselines")));
        var failed = false;
        try { await monitor.StartAsync(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { failed = true; }
        if (!failed || Directory.Exists(left) || await File.ReadAllTextAsync(Path.Combine(right, "survivor.sav")) != "KEEP")
            throw new InvalidOperationException("Watcher startup bypassed the source-root safety preflight.");
    }
    private static async Task TestBaselineFailureAsync(string root)
    {
        Directory.CreateDirectory(root);
        var runner = new SyncTaskRunner((_, _) => Task.FromResult(SyncBaseline.Empty),
            (_, _, _) => throw new IOException("injected baseline write fault"),
            (_, _, _, _) => Task.FromResult(new SyncRunResult(3, 2, 1, SyncBaseline.Empty)));
        SyncBaselinePersistenceException? error = null;
        try { await runner.RunAsync(new SyncTaskDefinition(), false); }
        catch (SyncBaselinePersistenceException exception) { error = exception; }
        if (error?.AppliedResult.CopiedFiles != 3 || error.AppliedResult.DeletedFiles != 2)
            throw new InvalidOperationException("Baseline failure hid already-applied file operation counts.");
        var id = Guid.NewGuid(); var directory = Path.Combine(root, "state"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, id.ToString("N") + ".json"); await File.WriteAllTextAsync(path, "{");
        var unchanged = await File.ReadAllBytesAsync(path); var observed = false;
        try { await new SyncBaselineStore(directory).LoadAsync(id); }
        catch (InvalidDataException exception) { observed = exception.Message.Contains(path, StringComparison.Ordinal); }
        var after = await File.ReadAllBytesAsync(path);
        if (!observed || !unchanged.SequenceEqual(after)) throw new InvalidOperationException("Corrupt baseline silently reset or lost its recovery location.");
    }

    private static async Task TestTargetJunctionAsync(string root)
    {
        var left = Path.Combine(root, "left"); var right = Path.Combine(root, "right"); var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(Path.Combine(left, "sub")); Directory.CreateDirectory(right); Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(left, "sub", "file.txt"), "NEW");
        var external = Path.Combine(outside, "file.txt"); await File.WriteAllTextAsync(external, "ONLY_ORIGINAL");
        var link = Path.Combine(right, "sub");
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, Arguments = $"/d /c mklink /J \"{link}\" \"{outside}\"" };
            using var process = Process.Start(start)!; await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException("Could not create sync junction fixture.");
        }
        else Directory.CreateSymbolicLink(link, outside);
        try
        {
            var failed = false;
            try { await new SyncEngine(Path.Combine(root, "vault")).RunAsync(new SyncTaskDefinition { LeftPath = left, RightPath = right, DeletionMode = DeletionMode.Ignore }, SyncBaseline.Empty); }
            catch (InvalidDataException) { failed = true; }
            if (!failed || await File.ReadAllTextAsync(external) != "ONLY_ORIGINAL") throw new InvalidOperationException("Sync followed target junction and overwrote an external file.");
            var copyFailed = false;
            try { await AtomicFileOperations.CopyAsync(Path.Combine(left, "sub", "file.txt"), Path.Combine(link, "file.txt")); }
            catch (InvalidDataException) { copyFailed = true; }
            if (!copyFailed || await File.ReadAllTextAsync(external) != "ONLY_ORIGINAL") throw new InvalidOperationException("Atomic copy bypassed path ancestor validation.");
        }
        finally { Directory.Delete(link); }
    }

}
