using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PathEcho.Core.Backup;
using PathEcho.Core.Restore;

internal static class RestoreSafetyTests
{
    internal static async Task RunAsync(string testRoot)
    {
        var root = Path.Combine(testRoot, "restore-safety");
        foreach (var mode in new[] { RestoreMode.CleanDirectory, RestoreMode.FilteredFiles, RestoreMode.ChangedFiles })
        {
            var modeRoot = Path.Combine(root, mode.ToString());
            await TestCommitFailureAsync(Path.Combine(modeRoot, "commit-failure"), mode);
            await TestRollbackFailureAsync(Path.Combine(modeRoot, "rollback-failure"), mode);
            await TestPartialCleanupAsync(Path.Combine(modeRoot, "cleanup-io"), mode, false);
            await TestPartialCleanupAsync(Path.Combine(modeRoot, "cleanup-access"), mode, true);
            await TestCleanupEnumerationAsync(Path.Combine(modeRoot, "cleanup-enumeration"), mode);
            await TestCancellationAsync(Path.Combine(modeRoot, "cancellation"), mode);
            await TestMissingTargetRollbackAsync(Path.Combine(modeRoot, "missing-target"), mode);
        }

        await TestOccupancyUnavailableAsync(Path.Combine(root, "occupancy-unavailable"));
        await TestInvalidPathsAsync(Path.Combine(root, "invalid-paths"));
        await TestLinksAsync(Path.Combine(root, "links"));
    }

    private static async Task TestOccupancyUnavailableAsync(string root)
    {
        var fixture = await CreateFixtureAsync(root);
        var occupancy = new UnavailableOccupancy();
        var service = new SnapshotRestoreService(occupancy);
        await ThrowsAsync<System.ComponentModel.Win32Exception>(() => service.RestoreAsync(Request(fixture, RestoreMode.ChangedFiles)));
        AssertOriginal(fixture);
        Assert(occupancy.Terminations == 0, "A failed occupancy inspection attempted to terminate a process.");
        var result = await service.RestoreAsync(Request(fixture, RestoreMode.ChangedFiles) with { OccupiedFileAction = OccupiedFileAction.ForceAttempt });
        AssertRestored(fixture, RestoreMode.ChangedFiles);
        Assert(result.RestoredFiles == 3 && occupancy.Calls == 1 && occupancy.Terminations == 0,
            "Explicit force mode still required unavailable RestartManager or terminated a process.");
    }

    private sealed class UnavailableOccupancy : IFileOccupancyService
    {
        internal int Calls; internal int Terminations;
        public Task<IReadOnlyList<OccupiedFile>> FindAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        { Calls++; throw new System.ComponentModel.Win32Exception(29, "isolated native inspection fault"); }
        public Task TerminateAsync(IReadOnlyList<LockingProcess> processes, CancellationToken cancellationToken = default)
        { Terminations++; throw new InvalidOperationException("Must never terminate."); }
    }

    private static async Task TestCommitFailureAsync(string root, RestoreMode mode)
    {
        var fixture = await CreateFixtureAsync(root);
        var reached = false;
        var service = Service((point, path) =>
        {
            if ((mode == RestoreMode.CleanDirectory && point == RestoreFaultPoint.WholeTargetMoved) ||
                (mode != RestoreMode.CleanDirectory && point == RestoreFaultPoint.SelectedTargetMoved && Path.GetFileName(path) == "b.sav"))
            {
                reached = true;
                throw new IOException("injected installation failure");
            }
        });
        await ThrowsAsync<IOException>(() => service.RestoreAsync(Request(fixture, mode)));
        Assert(reached, "Commit failure was not injected at the mutation boundary.");
        AssertOriginal(fixture);
        AssertNoTransactions(root);
    }

    private static async Task TestRollbackFailureAsync(string root, RestoreMode mode)
    {
        var fixture = await CreateFixtureAsync(root);
        var failedRollback = false;
        var service = Service((point, path) =>
        {
            if ((mode == RestoreMode.CleanDirectory && point == RestoreFaultPoint.WholeTargetMoved) ||
                (mode != RestoreMode.CleanDirectory && point == RestoreFaultPoint.SelectedTargetMoved && Path.GetFileName(path) == "b.sav"))
            {
                throw new IOException("injected installation failure");
            }

            if ((mode == RestoreMode.CleanDirectory && point == RestoreFaultPoint.WholeRollback) ||
                (mode != RestoreMode.CleanDirectory && point == RestoreFaultPoint.SelectedRollback && Path.GetFileName(path) == "a.sav"))
            {
                failedRollback = true;
                throw new IOException("injected rollback failure");
            }
        });
        var failure = await ThrowsAsync<IOException>(() => service.RestoreAsync(Request(fixture, mode)));
        Assert(failedRollback && failure.InnerException is AggregateException, "Rollback failure lost either failure cause.");
        var recoveryRoot = Directory.GetDirectories(root, mode == RestoreMode.CleanDirectory ? ".pathecho-rollback-*" : ".pathecho-file-restore-*").Single();
        Assert(failure.Message.Contains(recoveryRoot, StringComparison.Ordinal), "Rollback failure omitted its recovery path.");
        var original = mode == RestoreMode.CleanDirectory ? Path.Combine(recoveryRoot, "a.sav") : Path.Combine(recoveryRoot, "rollback", "a.sav");
        Assert(File.ReadAllText(original) == "original-a", "Failed rollback deleted the only original.");
        if (mode == RestoreMode.CleanDirectory)
        {
            var stageRoot = Directory.GetDirectories(root, ".pathecho-restore-*").Single();
            Assert(File.ReadAllText(Path.Combine(stageRoot, "b.sav")) == "snapshot-b", "Failed whole rollback deleted staged recovery content.");
        }
        else
        {
            Assert(File.ReadAllText(Path.Combine(fixture.Target, "a.sav")) == "snapshot-a", "Failed selected rollback deleted the installed target.");
            Assert(File.ReadAllText(Path.Combine(fixture.Target, "b.sav")) == "original-b", "One failed rollback prevented other originals from being restored.");
        }
    }

    private static async Task TestPartialCleanupAsync(string root, RestoreMode mode, bool accessDenied)
    {
        var fixture = await CreateFixtureAsync(root);
        string? firstDeleted = null;
        var cleanupCalls = 0;
        var service = Service((point, path) =>
        {
            if (point != RestoreFaultPoint.CleanupFile) return;
            cleanupCalls++;
            if (cleanupCalls == 1) { firstDeleted = path; return; }
            Assert(firstDeleted is not null && !File.Exists(firstDeleted), "Cleanup did not actually delete an original before the injected failure.");
            if (accessDenied) throw new UnauthorizedAccessException("injected partial cleanup denial");
            throw new IOException("injected partial cleanup failure");
        });
        var result = await service.RestoreAsync(Request(fixture, mode));
        Assert(cleanupCalls == 2 && result.PreservedRollbackDirectory is not null && Directory.Exists(result.PreservedRollbackDirectory),
            "Partial cleanup was not reported as a retained recovery directory.");
        AssertRestored(fixture, mode);
        Assert(result.RestoredFiles == 3 && result.RemovedFiles == (mode == RestoreMode.CleanDirectory ? 3 : 0), "Cleanup changed committed restore accounting.");
        Assert(Directory.EnumerateFiles(result.PreservedRollbackDirectory!, "*", SearchOption.AllDirectories).Any(), "Cleanup failure reported an empty residue.");
    }

    private static async Task TestCleanupEnumerationAsync(string root, RestoreMode mode)
    {
        var fixture = await CreateFixtureAsync(root);
        var reached = false;
        var service = Service((point, _) =>
        {
            if (point != RestoreFaultPoint.CleanupEnumerate) return;
            reached = true;
            AssertRestored(fixture, mode);
            throw new IOException("injected cleanup enumeration failure");
        });
        var result = await service.RestoreAsync(Request(fixture, mode));
        Assert(reached && result.PreservedRollbackDirectory is not null, "Post-commit enumeration failure was not reported as cleanup residue.");
        AssertRestored(fixture, mode);
    }

    private static async Task TestCancellationAsync(string root, RestoreMode mode)
    {
        foreach (var boundary in new[] { "before", "staged", "during", "after" })
        {
            var fixture = await CreateFixtureAsync(Path.Combine(root, boundary));
            using var cancellation = new CancellationTokenSource();
            var reached = boundary == "before";
            if (boundary == "before") cancellation.Cancel();
            var service = Service((point, _) =>
            {
                var cancel = (boundary == "staged" && point == RestoreFaultPoint.Staged) ||
                    (boundary == "during" && point == (mode == RestoreMode.CleanDirectory ? RestoreFaultPoint.WholeTargetMoved : RestoreFaultPoint.SelectedFileCommitted)) ||
                    (boundary == "after" && point == RestoreFaultPoint.Committed);
                if (cancel) { reached = true; cancellation.Cancel(); }
            });
            if (boundary == "after")
            {
                var result = await service.RestoreAsync(Request(fixture, mode), cancellation.Token);
                Assert(result.PreservedRollbackDirectory is null, "Late cancellation prevented normal cleanup after commit.");
                AssertRestored(fixture, mode);
            }
            else
            {
                await ThrowsAsync<OperationCanceledException>(() => service.RestoreAsync(Request(fixture, mode), cancellation.Token));
                AssertOriginal(fixture);
            }

            Assert(reached, $"Cancellation boundary was not reached: {boundary}.");
            AssertNoTransactions(fixture.Root);
        }
    }

    private static async Task TestMissingTargetRollbackAsync(string root, RestoreMode mode)
    {
        var fixture = await CreateFixtureAsync(root);
        Directory.Delete(fixture.Target, true);
        var service = Service((point, _) =>
        {
            if (point == (mode == RestoreMode.CleanDirectory ? RestoreFaultPoint.WholeTargetMoved : RestoreFaultPoint.SelectedFileCommitted))
                throw new IOException("injected new-target failure");
        });
        await ThrowsAsync<IOException>(() => service.RestoreAsync(Request(fixture, mode)));
        Assert(!Directory.Exists(fixture.Target), "Rollback created a target directory that did not originally exist.");
        AssertNoTransactions(root);
    }

    private static async Task TestInvalidPathsAsync(string root)
    {
        var fixture = await CreateFixtureAsync(root);
        var manifest = JsonSerializer.Deserialize<SnapshotManifest>(await File.ReadAllTextAsync(Path.Combine(fixture.Snapshot, "manifest.json")))!;
        foreach (var relative in new[] { "../outside.sav", "folder/../../outside.sav", "a.sav:stream", "folder/.. /outside.sav", "NUL.sav", ".", @"C:\outside.sav" })
        {
            var invalid = manifest with { Files = new[] { manifest.Files[0] with { RelativePath = relative } } };
            await File.WriteAllTextAsync(Path.Combine(fixture.Snapshot, "manifest.json"), JsonSerializer.Serialize(invalid));
            await ThrowsAsync<InvalidDataException>(() => Service().RestoreAsync(Request(fixture, RestoreMode.FilteredFiles)));
            AssertOriginal(fixture);
        }

        var duplicate = manifest with { Files = new[] { manifest.Files[1], manifest.Files[1] with { RelativePath = "A.sav" } } };
        await File.WriteAllTextAsync(Path.Combine(fixture.Snapshot, "manifest.json"), JsonSerializer.Serialize(duplicate));
        await ThrowsAsync<InvalidDataException>(() => Service().RestoreAsync(Request(fixture, RestoreMode.CleanDirectory)));
        await File.WriteAllTextAsync(Path.Combine(fixture.Snapshot, "manifest.json"), JsonSerializer.Serialize(manifest));
        foreach (var target in new[] { root, Path.Combine(fixture.Snapshot, "files") })
        {
            await ThrowsAsync<InvalidDataException>(() => Service().RestoreAsync(Request(fixture, RestoreMode.CleanDirectory) with { TargetDirectory = target }));
        }

        AssertOriginal(fixture);
        AssertNoTransactions(root);
    }

    private static async Task TestLinksAsync(string root)
    {
        var fixture = await CreateFixtureAsync(root);
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "added.sav"), "outside-original");
        var link = Path.Combine(fixture.Target, "new");
        await CreateDirectoryLinkAsync(link, outside);
        try
        {
            foreach (var mode in new[] { RestoreMode.CleanDirectory, RestoreMode.FilteredFiles, RestoreMode.ChangedFiles })
            {
                await ThrowsAsync<IOException>(() => Service().RestoreAsync(Request(fixture, mode)));
                Assert(File.ReadAllText(Path.Combine(outside, "added.sav")) == "outside-original", "Restore followed a target junction outside its boundary.");
            }
        }
        finally { Directory.Delete(link); }

        // Introduce a junction after commit to exercise the cleanup guard itself.
        string? cleanupLink = null;
        var service = Service((point, _) =>
        {
            if (point != RestoreFaultPoint.Committed) return;
            var rollback = Directory.GetDirectories(root, ".pathecho-rollback-*").Single();
            cleanupLink = Path.Combine(rollback, "redirected");
            CreateDirectoryLinkAsync(cleanupLink, outside).GetAwaiter().GetResult();
        });
        try
        {
            var result = await service.RestoreAsync(Request(fixture, RestoreMode.CleanDirectory));
            Assert(result.PreservedRollbackDirectory is not null, "Cleanup followed or silently removed a newly introduced junction.");
            AssertRestored(fixture, RestoreMode.CleanDirectory);
            Assert(File.ReadAllText(Path.Combine(outside, "added.sav")) == "outside-original", "Cleanup changed linked content outside its recovery directory.");
        }
        finally { if (cleanupLink is not null && Directory.Exists(cleanupLink)) Directory.Delete(cleanupLink); }
    }

    private static async Task CreateDirectoryLinkAsync(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        { UseShellExecute = false, CreateNoWindow = true, Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"" })
            ?? throw new InvalidOperationException("Could not create isolated restore junction fixture.");
        await process.WaitForExitAsync();
        Assert(process.ExitCode == 0, "Could not create isolated restore junction fixture.");
    }

    private sealed record Fixture(string Root, string Snapshot, string Target);

    private static async Task<Fixture> CreateFixtureAsync(string root)
    {
        var snapshot = Path.Combine(root, "backup", "profile", "snapshots", "version");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "a.sav"), "original-a");
        await File.WriteAllTextAsync(Path.Combine(target, "b.sav"), "original-b");
        await File.WriteAllTextAsync(Path.Combine(target, "extra.sav"), "original-extra");
        var entries = new List<SnapshotFileEntry>();
        foreach (var (relative, content) in new[] { ("new/added.sav", "snapshot-new"), ("a.sav", "snapshot-a"), ("b.sav", "snapshot-b") })
        {
            var path = Path.Combine(snapshot, "files", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content);
            var bytes = Encoding.UTF8.GetBytes(content);
            entries.Add(new SnapshotFileEntry { RelativePath = relative, Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), LastWriteUtcTicks = DateTime.UtcNow.Ticks });
        }

        var manifest = new SnapshotManifest { ProfileId = Guid.NewGuid(), CreatedAtUtc = DateTimeOffset.UtcNow, Trigger = "isolated restore safety", Files = entries };
        await File.WriteAllTextAsync(Path.Combine(snapshot, "manifest.json"), JsonSerializer.Serialize(manifest));
        return new Fixture(root, snapshot, target);
    }

    private static RestoreRequest Request(Fixture fixture, RestoreMode mode) => new()
    {
        SnapshotDirectory = fixture.Snapshot,
        TargetDirectory = fixture.Target,
        Mode = mode,
        IncludePatterns = new[] { ".*" },
    };

    private static SnapshotRestoreService Service(Action<RestoreFaultPoint, string>? fault = null) => new(new EmptyOccupancy(), fault);

    private sealed class EmptyOccupancy : IFileOccupancyService
    {
        public Task<IReadOnlyList<OccupiedFile>> FindAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OccupiedFile>>(Array.Empty<OccupiedFile>());
        public Task TerminateAsync(IReadOnlyList<LockingProcess> processes, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static void AssertOriginal(Fixture fixture)
    {
        Assert(File.ReadAllText(Path.Combine(fixture.Target, "a.sav")) == "original-a", "Failed restore changed original a.");
        Assert(File.ReadAllText(Path.Combine(fixture.Target, "b.sav")) == "original-b", "Failed restore changed original b.");
        Assert(File.ReadAllText(Path.Combine(fixture.Target, "extra.sav")) == "original-extra", "Failed restore changed an unselected original.");
        Assert(!Directory.Exists(Path.Combine(fixture.Target, "new")), "Failed restore retained a newly created directory.");
    }

    private static void AssertRestored(Fixture fixture, RestoreMode mode)
    {
        Assert(File.ReadAllText(Path.Combine(fixture.Target, "a.sav")) == "snapshot-a", "Committed target a was lost or rolled back during cleanup.");
        Assert(File.ReadAllText(Path.Combine(fixture.Target, "b.sav")) == "snapshot-b", "Committed target b was lost or rolled back during cleanup.");
        Assert(File.ReadAllText(Path.Combine(fixture.Target, "new", "added.sav")) == "snapshot-new", "Committed new target was deleted.");
        Assert(File.Exists(Path.Combine(fixture.Target, "extra.sav")) == (mode != RestoreMode.CleanDirectory), "Restore mode changed unselected file semantics.");
    }

    private static void AssertNoTransactions(string root) =>
        Assert(!Directory.EnumerateDirectories(root, ".pathecho-*").Any(), "Successful rollback or cleanup retained an unreported transaction.");

    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T exception) { return exception; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
