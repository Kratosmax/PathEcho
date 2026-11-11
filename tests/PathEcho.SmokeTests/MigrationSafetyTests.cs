using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using PathEcho.Core.Backup;

internal static class MigrationSafetyTests
{
    internal static async Task RunAsync(string testRoot)
    {
        var root = Path.Combine(testRoot, "migration-safety");
        await PrepareAndRollbackAsync(Path.Combine(root, "rollback"));
        await CommitAsync(Path.Combine(root, "commit"));
        await PrepareAndRollbackAsync(Path.Combine(root, "rollback-structured"), true);
        await CommitAsync(Path.Combine(root, "commit-structured"), true);
        foreach (var point in new[] { BackupProfileMoveFaultPoint.CopiedObject,
                     BackupProfileMoveFaultPoint.BeforeInstall, BackupProfileMoveFaultPoint.Installed })
        {
            await PrepareFailureAsync(Path.Combine(root, "failure-" + point), point);
            await CancellationAsync(Path.Combine(root, "cancel-" + point), point);
        }
        await CorruptCopyAsync(Path.Combine(root, "corrupt-copy"));
        await PreCanceledAsync(Path.Combine(root, "pre-canceled"));
        await CleanupFailureAsync(Path.Combine(root, "cleanup-io"), false);
        await CleanupFailureAsync(Path.Combine(root, "cleanup-access"), true);
        await RollbackFailureAsync(Path.Combine(root, "rollback-failed"));
        await PrepareCleanupFailureAsync(Path.Combine(root, "prepare-cleanup-failed"));
        await ConflictAsync(Path.Combine(root, "conflict-dir"), false, false);
        await ConflictAsync(Path.Combine(root, "conflict-file"), true, false);
        await ConflictAsync(Path.Combine(root, "conflict-race"), false, true);
        await LegacyWrapperAsync(Path.Combine(root, "legacy-success"), false);
        await LegacyWrapperAsync(Path.Combine(root, "legacy-warning"), true);
        await NoMoveAsync(Path.Combine(root, "no-move"));
        await RedirectedDestinationAsync(Path.Combine(root, "redirect-destination"));
        await RedirectedPrepareFailureAsync(Path.Combine(root, "redirect-prepare-failure"));
    }

    private static async Task RedirectedPrepareFailureAsync(string root)
    {
        var f = await FixtureAsync(root); var retained = f.NewRoot + "-retained"; var injected = false;
        var manager = Manager((point, _) =>
        {
            if (point != BackupProfileMoveFaultPoint.Installed) return;
            Directory.Move(f.NewRoot, retained);
            if (OperatingSystem.IsWindows())
            {
                var start = new ProcessStartInfo("cmd.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    Arguments = $"/d /c mklink /J \"{f.NewRoot}\" \"{f.OldRoot}\""
                };
                using var process = Process.Start(start)!; process.WaitForExit();
                Assert(process.ExitCode == 0, "Could not create prepare-failure junction fixture.");
            }
            else Directory.CreateSymbolicLink(f.NewRoot, f.OldRoot);
            injected = true; throw new IOException("injected after ancestor replacement");
        });
        try
        {
            await ThrowsAsync<BackupProfileMoveRollbackException>(() => manager.PrepareMoveProfileAsync(f.Id, f.OldRoot, f.NewRoot));
            Assert(injected, "Prepare ancestor fault was not reached.");
            AssertOriginal(f); await AssertUsableAsync(Path.Combine(retained, f.Id.ToString("N")));
        }
        finally { if (Directory.Exists(f.NewRoot)) Directory.Delete(f.NewRoot); }
    }

    private static async Task RedirectedDestinationAsync(string root)
    {
        var f = await FixtureAsync(root);
        var inner = Path.Combine(f.Source, "inner"); Directory.CreateDirectory(inner);
        var redirected = Path.Combine(root, "redirected-new-root");
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                Arguments = $"/d /c mklink /J \"{redirected}\" \"{inner}\""
            };
            using var process = Process.Start(start)!; await process.WaitForExitAsync();
            Assert(process.ExitCode == 0, "Could not create destination junction fixture.");
        }
        else Directory.CreateSymbolicLink(redirected, inner);
        try
        {
            await ThrowsAsync<InvalidDataException>(() => new BackupDirectoryManager().PrepareMoveProfileAsync(f.Id, f.OldRoot, redirected));
            AssertOriginal(f);
            Assert(!Directory.EnumerateFileSystemEntries(inner).Any(), "Aliased nested destination received data before validation.");
        }
        finally { Directory.Delete(redirected); }
        var prepared = await PrepareAsync(f, new BackupDirectoryManager());
        var retainedRoot = f.NewRoot + "-original";
        Directory.Move(f.NewRoot, retainedRoot);
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                Arguments = $"/d /c mklink /J \"{f.NewRoot}\" \"{inner}\""
            };
            using var process = Process.Start(start)!; await process.WaitForExitAsync();
            Assert(process.ExitCode == 0, "Could not create changed ancestor fixture.");
        }
        else Directory.CreateSymbolicLink(f.NewRoot, inner);
        try
        {
            Assert(prepared.Commit() == f.Source, "Commit deleted its source after the destination ancestor was redirected.");
            AssertOriginal(f);
            await AssertUsableAsync(Path.Combine(retainedRoot, f.Id.ToString("N")));
        }
        finally { Directory.Delete(f.NewRoot); }
    }

    private static async Task PrepareAndRollbackAsync(string root, bool structured = false)
    {
        var f = await FixtureAsync(root, structured);
        var move = await PrepareAsync(f, new BackupDirectoryManager());
        Assert(move.SourceDirectory == f.Source && move.DestinationDirectory == f.Destination, "Lease paths are not the full profile paths.");
        AssertOriginal(f);
        await AssertUsableAsync(f.Destination);
        move.Rollback();
        move.Rollback();
        AssertOriginal(f);
        Assert(!Directory.Exists(f.Destination), "Rollback retained its installed target.");
        AssertNoStage(f);
        // A normal retry must no longer be blocked by a stale destination.
        var retry = await PrepareAsync(f, new BackupDirectoryManager());
        move.Rollback(); // The old completed lease no longer owns this newly installed destination.
        await AssertUsableAsync(f.Destination);
        retry.Rollback();
        AssertOriginal(f);
    }

    private static async Task CommitAsync(string root, bool structured = false)
    {
        var f = await FixtureAsync(root, structured);
        var move = await PrepareAsync(f, new BackupDirectoryManager());
        AssertOriginal(f);
        Assert(move.Commit() is null, "Successful commit reported residual data.");
        Assert(!Directory.Exists(f.Source), "Commit retained its source.");
        await AssertUsableAsync(f.Destination);
        Assert(move.Commit() is null, "Repeated commit changed its result.");
        move.Rollback();
        await AssertUsableAsync(f.Destination);
        AssertNoStage(f);
    }

    private static async Task PrepareFailureAsync(string root, BackupProfileMoveFaultPoint target)
    {
        var f = await FixtureAsync(root);
        var reached = false;
        var manager = Manager((point, _) =>
        {
            if (point == target) { reached = true; throw new IOException("injected prepare failure"); }
        });
        await ThrowsAsync<IOException>(() => PrepareAsync(f, manager));
        Assert(reached, "Prepare fault was not reached.");
        AssertOriginal(f);
        Assert(!Directory.Exists(f.Destination), "Failed prepare retained its target.");
        AssertNoStage(f);
    }

    private static async Task CorruptCopyAsync(string root)
    {
        var f = await FixtureAsync(root);
        var manager = Manager((point, path) =>
        {
            if (point == BackupProfileMoveFaultPoint.CopiedObject) File.WriteAllText(path, "corrupt");
        });
        await ThrowsAsync<IOException>(() => PrepareAsync(f, manager));
        AssertOriginal(f);
        Assert(!Directory.Exists(f.Destination), "Unverified content was installed.");
        AssertNoStage(f);
    }

    private static async Task CancellationAsync(string root, BackupProfileMoveFaultPoint target)
    {
        var f = await FixtureAsync(root);
        using var cancel = new CancellationTokenSource();
        var reached = false;
        var manager = Manager((point, _) => { if (point == target) { reached = true; cancel.Cancel(); } });
        await ThrowsAsync<OperationCanceledException>(() => manager.PrepareMoveProfileAsync(f.Id, f.OldRoot, f.NewRoot, cancel.Token));
        Assert(reached, "Cancellation boundary was not reached.");
        AssertOriginal(f);
        Assert(!Directory.Exists(f.Destination), "Canceled prepare retained its target.");
        AssertNoStage(f);
    }

    private static async Task PreCanceledAsync(string root)
    {
        var f = await FixtureAsync(root);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => new BackupDirectoryManager().PrepareMoveProfileAsync(f.Id, f.OldRoot, f.NewRoot, cancel.Token));
        AssertOriginal(f);
        Assert(!Directory.Exists(f.NewRoot), "Pre-cancellation created the target root.");
    }

    private static async Task CleanupFailureAsync(string root, bool accessDenied)
    {
        var f = await FixtureAsync(root);
        var visitedFiles = 0;
        var manager = Manager((point, path) =>
        {
            if (point == BackupProfileMoveFaultPoint.CleanupEntry && File.Exists(path) && ++visitedFiles == 2)
            {
                if (accessDenied) throw new UnauthorizedAccessException("injected partial cleanup denial");
                throw new IOException("injected partial cleanup failure");
            }
        });
        var move = await PrepareAsync(f, manager);
        AssertOriginal(f);
        var retained = move.Commit();
        Assert(visitedFiles == 2 && retained == f.Source && Directory.Exists(retained), "Cleanup did not report its exact retained profile root.");
        Assert(Directory.GetFiles(f.Source, "*", SearchOption.AllDirectories).Length < f.Original.Count,
            "Cleanup fault did not occur after a real source deletion.");
        await AssertUsableAsync(f.Destination);
        move.Rollback(); // Cleanup warning must never reopen the destructive rollback path.
        await AssertUsableAsync(f.Destination);
        Assert(move.Commit() == retained && visitedFiles == 2, "Repeated commit retried destructive cleanup.");
    }

    private static async Task RollbackFailureAsync(string root)
    {
        var f = await FixtureAsync(root);
        var visitedFiles = 0;
        var manager = Manager((point, path) =>
        {
            if (point == BackupProfileMoveFaultPoint.RollbackEntry && File.Exists(path) && ++visitedFiles == 2)
                throw new IOException("injected rollback failure");
        });
        var move = await PrepareAsync(f, manager);
        var failure = await ThrowsAsync<BackupProfileMoveRollbackException>(() => { move.Rollback(); return Task.CompletedTask; });
        Assert(failure.SourceDirectory == f.Source && failure.RetainedDirectory == f.Destination &&
               failure.Message.Contains(f.Destination, StringComparison.Ordinal), "Rollback omitted its precise recovery paths.");
        AssertOriginal(f);
        Assert(Directory.Exists(f.Destination), "Failed rollback concealed the remaining destination.");
        manager.MoveFaultHook = null;
        move.Rollback();
        AssertOriginal(f);
        Assert(!Directory.Exists(f.Destination), "Rollback retry failed to remove its residual target.");
    }

    private static async Task PrepareCleanupFailureAsync(string root)
    {
        var f = await FixtureAsync(root);
        var manager = Manager((point, _) =>
        {
            if (point is BackupProfileMoveFaultPoint.CopiedObject or BackupProfileMoveFaultPoint.RollbackEntry)
                throw new IOException("injected prepare and cleanup failure");
        });
        var failure = await ThrowsAsync<BackupProfileMoveRollbackException>(() => PrepareAsync(f, manager));
        var stage = Directory.GetDirectories(f.NewRoot, "*.moving").Single();
        Assert(failure.RetainedDirectory == stage && failure.SourceDirectory == f.Source &&
               failure.InnerException is AggregateException && failure.Message.Contains(stage, StringComparison.Ordinal),
            "Prepare cleanup failure lost its actual staging path or original cause.");
        AssertOriginal(f);
        Assert(!Directory.Exists(f.Destination), "Failed prepare installed an unverified target.");
    }

    private static async Task ConflictAsync(string root, bool file, bool race)
    {
        var f = await FixtureAsync(root);
        void MakeConflict()
        {
            Directory.CreateDirectory(f.NewRoot);
            if (file) File.WriteAllText(f.Destination, "other owner's file");
            else { Directory.CreateDirectory(f.Destination); File.WriteAllText(Path.Combine(f.Destination, "sentinel"), "other owner's directory"); }
        }
        var manager = Manager((point, _) => { if (race && point == BackupProfileMoveFaultPoint.BeforeInstall) MakeConflict(); });
        if (!race) MakeConflict();
        await ThrowsAsync<IOException>(() => PrepareAsync(f, manager));
        AssertOriginal(f);
        Assert(File.ReadAllText(file ? f.Destination : Path.Combine(f.Destination, "sentinel")) ==
               (file ? "other owner's file" : "other owner's directory"), "A conflicting target was modified or removed.");
        AssertNoStage(f);
    }

    private static async Task LegacyWrapperAsync(string root, bool cleanupFailure)
    {
        var f = await FixtureAsync(root);
        var manager = Manager((point, _) =>
        {
            if (cleanupFailure && point == BackupProfileMoveFaultPoint.CleanupEntry) throw new IOException("legacy cleanup failure");
        });
        if (cleanupFailure)
        {
            var failure = await ThrowsAsync<BackupProfileMoveCleanupException>(() => manager.MoveProfileAsync(f.Id, f.OldRoot, f.NewRoot));
            Assert(failure.DestinationDirectory == f.Destination && failure.RetainedSourceDirectory == f.Source &&
                   failure.Message.Contains(f.Destination, StringComparison.Ordinal), "Legacy warning omitted the complete installed destination.");
            AssertOriginal(f);
        }
        else
        {
            Assert(await manager.MoveProfileAsync(f.Id, f.OldRoot, f.NewRoot), "Legacy wrapper did not move.");
            Assert(!Directory.Exists(f.Source), "Legacy success retained its source.");
        }
        await AssertUsableAsync(f.Destination);
    }

    private static async Task NoMoveAsync(string root)
    {
        var f = await FixtureAsync(root);
        var manager = new BackupDirectoryManager();
        Assert(await manager.PrepareMoveProfileAsync(f.Id, f.OldRoot, f.OldRoot) is null, "Same-root prepare created a lease.");
        Assert(await manager.PrepareMoveProfileAsync(Guid.NewGuid(), f.OldRoot, f.NewRoot) is null, "Missing source created a lease.");
        await ThrowsAsync<InvalidOperationException>(() => manager.PrepareMoveProfileAsync(f.Id, f.OldRoot, Path.Combine(f.OldRoot, "nested")));
        AssertOriginal(f);
        Assert(!Directory.Exists(f.NewRoot), "No-op prepare created the target.");
    }

    private sealed record Fixture(Guid Id, string OldRoot, string NewRoot, string Source, string Destination,
        IReadOnlyDictionary<string, string> Original);

    private static async Task<Fixture> FixtureAsync(string root, bool structured = false)
    {
        var id = Guid.NewGuid();
        var oldRoot = Path.Combine(root, "old");
        var newRoot = Path.Combine(root, "new");
        var source = Path.Combine(oldRoot, id.ToString("N"));
        for (var version = 1; version <= 2; version++)
        {
            var snapshot = Path.Combine(source, "snapshots", "version-" + version);
            var files = Path.Combine(snapshot, "files");
            Directory.CreateDirectory(structured ? snapshot : files);
            var entries = new List<SnapshotFileEntry>();
            foreach (var (name, content) in new[] { ("a.sav", "a-version-" + version), ("b.sav", "shared-b") })
            {
                var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToUpperInvariant();
                var path = structured ? SnapshotContent.GetObjectPath(source, hash) : Path.Combine(files, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (!File.Exists(path)) await File.WriteAllTextAsync(path, content);
                entries.Add(new SnapshotFileEntry
                {
                    RelativePath = name,
                    Sha256 = Hash(path),
                    Length = new FileInfo(path).Length,
                    LastWriteUtcTicks = File.GetLastWriteTimeUtc(path).Ticks
                });
            }
            var manifest = new SnapshotManifest
            {
                SchemaVersion = structured ? 2 : 1,
                ProfileId = id,
                CreatedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(version),
                Trigger = "migration-test",
                Files = entries
            };
            await File.WriteAllTextAsync(Path.Combine(snapshot, "manifest.json"), JsonSerializer.Serialize(manifest));
        }
        return new Fixture(id, oldRoot, newRoot, source, Path.Combine(newRoot, id.ToString("N")), Digest(source));
    }

    private static Dictionary<string, string> Digest(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root, path), path => Hash(path) + ":" + File.GetLastWriteTimeUtc(path).Ticks + ":" + File.GetAttributes(path), StringComparer.Ordinal);
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToUpperInvariant();
    private static void AssertOriginal(Fixture f)
    {
        var actual = Digest(f.Source);
        Assert(actual.Count == f.Original.Count && f.Original.All(pair => actual.TryGetValue(pair.Key, out var value) && value == pair.Value),
            "Original backup files, bytes, timestamps or attributes changed before commit/after rollback.");
    }
    private static async Task AssertUsableAsync(string profileRoot)
    {
        var snapshots = Directory.GetDirectories(Path.Combine(profileRoot, "snapshots"));
        Assert(snapshots.Length == 2, "Migrated snapshot versions are incomplete.");
        foreach (var snapshot in snapshots)
        {
            var manifest = await SnapshotContent.ReadManifestAsync(snapshot);
            Assert(manifest.Files.Count == 2, "Migrated manifest omitted files.");
            foreach (var entry in manifest.Files)
            {
                var path = await SnapshotContent.ResolveVerifiedFileAsync(snapshot, entry);
                var expected = entry.RelativePath == "b.sav" ? "shared-b" : "a-" + Path.GetFileName(snapshot);
                Assert(await File.ReadAllTextAsync(path) == expected && new FileInfo(path).Length == entry.Length,
                    "Migrated backup has wrong content or length.");
            }
        }
    }
    private static void AssertNoStage(Fixture f) => Assert(!Directory.Exists(f.NewRoot) || !Directory.GetDirectories(f.NewRoot, "*.moving").Any(), "Migration leaked its stage.");
    private static BackupDirectoryManager Manager(Action<BackupProfileMoveFaultPoint, string> hook) => new() { MoveFaultHook = hook };
    private static async Task<BackupProfileMove> PrepareAsync(Fixture f, BackupDirectoryManager manager) =>
        await manager.PrepareMoveProfileAsync(f.Id, f.OldRoot, f.NewRoot) ?? throw new InvalidOperationException("Expected move lease.");
    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T failure) { return failure; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
