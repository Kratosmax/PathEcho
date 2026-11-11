using System.Reflection;
using System.Threading.Channels;
using PathEcho.Core.Backup;
using PathEcho.Core.Models;

internal static class BackupSafetyTests
{
    public static async Task RunAsync(string testRoot)
    {
        var root = Path.Combine(testRoot, "backup-safety");
        Directory.CreateDirectory(root);
        await MissingSourcePreservesHistoryAsync(Path.Combine(root, "missing"));
        await MissingSourceRecoversWithinRetryAsync(Path.Combine(root, "recovery"));
        await StagingEmptyAndFreshContentsAsync(Path.Combine(root, "staging"));
        await RestartReconcilesRestoredContentsAsync(Path.Combine(root, "restart"));
        await OverflowSurvivesThrottleAsync(Path.Combine(root, "overflow"));
        await OverflowDuringBackupSurvivesAsync(Path.Combine(root, "inflight"));
        await FailedBatchStopsAsync(Path.Combine(root, "stopped"), true);
        await FailedBatchStopsAsync(Path.Combine(root, "failed"), false);
        await CancelTrailingAsync(Path.Combine(root, "cancel"));
        await StartDoesNotCreateMissingSourceAsync(Path.Combine(root, "start"));
        await ImportantRegexTimeoutAsync(Path.Combine(root, "regex-timeout"));
    }

    private static async Task ImportantRegexTimeoutAsync(string root)
    {
        var profile = Profile(root) with
        {
            Triggers = BackupTrigger.ImportantFileChanged,
            ImportantFilePatterns = ["^(a+)+$"]
        };
        var service = new GameBackupService(profile, Path.Combine(root, "backup"));
        await using var monitor = new GameBackupMonitor(profile, service);
        var failures = 0;
        monitor.BackupFailed += (_, exception) => { if (exception is System.Text.RegularExpressions.RegexMatchTimeoutException) failures++; };
        var important = (bool)typeof(GameBackupMonitor).GetMethod("IsImportantFile", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(monitor, [new string('a', 10000) + "!"])!;
        if (!important || failures != 1) throw new InvalidOperationException("Regex timeout was silent or escaped instead of preserving conservative backup coverage.");
    }

    private static GameBackupProfile Profile(string root, TimeSpan? interval = null) => new()
    {
        Name = "Backup safety fixture",
        SaveDirectory = Path.Combine(root, "save"),
        Triggers = BackupTrigger.ChangedFiles,
        MinimumBackupInterval = interval ?? TimeSpan.Zero,
        RetainedVersions = 1,
        RetainedHourlyVersions = 0,
        RetainedDailyVersions = 0,
    };

    private static BackupRetryOptions StopImmediately(Func<BackupRetryPrompt, CancellationToken, Task<bool>>? confirm = null) => new()
    {
        AttemptsPerPrompt = 1,
        Delay = TimeSpan.FromMilliseconds(10),
        ConfirmContinueAsync = confirm,
    };

    private static async Task MissingSourcePreservesHistoryAsync(string root)
    {
        var profile = Profile(root);
        await WriteAsync(profile.SaveDirectory, "slot.sav", "valuable save");
        var backup = Path.Combine(root, "backup");
        var store = new ObservedStore();
        var service = new GameBackupService(profile, backup, store, retryOptions: StopImmediately());
        var original = await service.CreateAsync(BackupTrigger.None) ?? throw new Exception("Initial backup missing.");
        Directory.Move(profile.SaveDirectory, Path.Combine(root, "offline"));
        await ExpectAsync<BackupRetryStoppedException>(() => service.CreateAsync(BackupTrigger.None));
        Check(store.PruneCalls == 1, "Missing source reached retention pruning.");
        Check(Directory.Exists(original.SnapshotDirectory), "Missing source pruned valid history.");
        await AssertContentsAsync(original, ("slot.sav", "valuable save"));
        var snapshots = Path.Combine(backup, profile.Id.ToString("N"), "snapshots");
        Check(Directory.GetDirectories(snapshots).Length == 1, "Missing source published an empty version.");
        var transaction = Path.Combine(root, "direct-staging");
        await ExpectAsync<IOException>(() => new BackupStagingStore().CreateAsync(profile.SaveDirectory, transaction, CancellationToken.None));
        Check(!Directory.Exists(transaction), "Failed staging left an incomplete transaction.");
    }

    private static async Task MissingSourceRecoversWithinRetryAsync(string root)
    {
        var profile = Profile(root);
        await WriteAsync(profile.SaveDirectory, "slot.sav", "returned source");
        var offline = Path.Combine(root, "offline");
        Directory.Move(profile.SaveDirectory, offline);
        var prompts = 0;
        var options = StopImmediately((prompt, _) =>
        {
            Check(prompt.Stage == BackupRetryStage.ReadingSource, "Missing source reported the wrong retry stage.");
            prompts++;
            Directory.Move(offline, profile.SaveDirectory);
            return Task.FromResult(true);
        });
        // The same operation must recover; there is no monitor or extra filesystem event to wake it.
        var service = new GameBackupService(profile, Path.Combine(root, "backup"), retryOptions: options);
        var recovered = await service.CreateAsync(BackupTrigger.None).WaitAsync(TimeSpan.FromSeconds(5));
        Check(prompts == 1 && recovered is not null, "Source recovery did not finish the original operation.");
        await AssertContentsAsync(recovered!, ("slot.sav", "returned source"));
    }

    private static async Task StagingEmptyAndFreshContentsAsync(string root)
    {
        var save = Path.Combine(root, "save");
        Directory.CreateDirectory(save);
        var staging = new BackupStagingStore();
        var transaction = Path.Combine(root, "transaction");
        var empty = await staging.CreateAsync(save, transaction, CancellationToken.None);
        Check(Directory.Exists(empty) && !Directory.EnumerateFileSystemEntries(empty).Any(), "Legitimate empty source was rejected.");
        staging.DeleteIfPresent(transaction);
        var path = Path.Combine(save, "slot.sav");
        await File.WriteAllTextAsync(path, "AAAA");
        var timestamp = File.GetLastWriteTimeUtc(path);
        await staging.CreateAsync(save, transaction, CancellationToken.None);
        staging.DeleteIfPresent(transaction);
        await File.WriteAllTextAsync(path, "BBBB");
        File.SetLastWriteTimeUtc(path, timestamp);
        var fresh = await staging.CreateAsync(save, transaction, CancellationToken.None);
        Check(await File.ReadAllTextAsync(Path.Combine(fresh, "slot.sav")) == "BBBB", "Staging trusted an old same-size/timestamp hash.");
        staging.DeleteIfPresent(transaction);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ExpectAsync<OperationCanceledException>(() => staging.CreateAsync(save, transaction, cancellation.Token));
        Check(!Directory.Exists(transaction), "Cancelled staging published a transaction.");
    }

    private static async Task RestartReconcilesRestoredContentsAsync(string root)
    {
        var profile = Profile(root) with { RetainedVersions = 10 };
        var backup = Path.Combine(root, "backup");
        await WriteAsync(profile.SaveDirectory, "a.sav", "old-a");
        await WriteAsync(profile.SaveDirectory, "b.sav", "old-b");
        await WriteAsync(profile.SaveDirectory, "restored.sav", "restored-only");
        var store = new ObservedStore();
        var service = new GameBackupService(profile, backup, store);
        var old = await service.CreateAsync(BackupTrigger.None) ?? throw new Exception("Old version missing.");
        await WriteAsync(profile.SaveDirectory, "a.sav", "new-a");
        await WriteAsync(profile.SaveDirectory, "b.sav", "new-b");
        File.Delete(Path.Combine(profile.SaveDirectory, "restored.sav"));
        await WriteAsync(profile.SaveDirectory, "extra.sav", "new-only");
        await using (var previous = new Worker(profile, service))
        {
            previous.Change("a.sav");
            await previous.CreatedAsync();
        }

        // A restore happens while the old watcher is stopped; only a.sav changes afterwards.
        await SnapshotContent.MaterializeBrowseCopyAsync(old.SnapshotDirectory, profile.SaveDirectory);
        await WriteAsync(profile.SaveDirectory, "a.sav", "after-restore");
        await using var restarted = new Worker(profile, service);
        restarted.Change("a.sav");
        var reconciled = await restarted.CreatedAsync();
        Check(store.LastWasFull, "Restart reused the latest pre-restore incremental manifest.");
        await AssertContentsAsync(reconciled, ("a.sav", "after-restore"), ("b.sav", "old-b"), ("restored.sav", "restored-only"));
    }

    private static async Task OverflowSurvivesThrottleAsync(string root)
    {
        var profile = Profile(root, TimeSpan.FromSeconds(3));
        await WriteAsync(profile.SaveDirectory, "known.sav", "first");
        await WriteAsync(profile.SaveDirectory, "lost.sav", "before-overflow");
        await WriteAsync(profile.SaveDirectory, "deleted.sav", "delete-me");
        var store = new ObservedStore();
        var service = new GameBackupService(profile, Path.Combine(root, "backup"), store);
        await using var worker = new Worker(profile, service);
        worker.Change("known.sav");
        await worker.CreatedAsync();
        await WriteAsync(profile.SaveDirectory, "known.sav", "second");
        await WriteAsync(profile.SaveDirectory, "lost.sav", "after-overflow");
        await WriteAsync(profile.SaveDirectory, "added.sav", "unreported-addition");
        File.Delete(Path.Combine(profile.SaveDirectory, "deleted.sav"));
        worker.Change("known.sav");
        worker.Overflow();
        // No more events: the throttled batch itself must complete the full reconciliation.
        var trailing = await worker.CreatedAsync();
        Check(store.LastWasFull && store.CreateCalls == 2, "Overflow became an incremental or duplicate trailing backup.");
        await AssertContentsAsync(trailing, ("known.sav", "second"), ("lost.sav", "after-overflow"), ("added.sav", "unreported-addition"));
    }

    private static async Task OverflowDuringBackupSurvivesAsync(string root)
    {
        var profile = Profile(root);
        await WriteAsync(profile.SaveDirectory, "slot.sav", "before");
        var store = new ObservedStore();
        var service = new GameBackupService(profile, Path.Combine(root, "backup"), store);
        await using var worker = new Worker(profile, service);
        store.AfterCreateAsync = async () =>
        {
            store.AfterCreateAsync = null;
            await WriteAsync(profile.SaveDirectory, "slot.sav", "after");
            worker.Overflow();
        };
        worker.Change("slot.sav");
        await worker.CreatedAsync();
        var reconciled = await worker.CreatedAsync();
        Check(store.LastWasFull, "Successful in-flight backup erased a newer overflow request.");
        await AssertContentsAsync(reconciled, ("slot.sav", "after"));
    }

    private static async Task FailedBatchStopsAsync(string root, bool retryStopped)
    {
        var profile = Profile(root);
        await WriteAsync(profile.SaveDirectory, "slot.sav", "first");
        var store = new ObservedStore { FailPrune = retryStopped, FailCreate = !retryStopped };
        var prompts = 0;
        var service = new GameBackupService(profile, Path.Combine(root, "backup"), store,
            retryOptions: StopImmediately((_, _) => { prompts++; return Task.FromResult(false); }));
        await using var worker = new Worker(profile, service);
        worker.Change("slot.sav");
        var failure = await worker.FailedAsync();
        Check(retryStopped ? failure is BackupRetryStoppedException : failure is InvalidOperationException,
            "Failed batch reported an unexpected exception.");
        var calls = store.CreateCalls;
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        Check(store.CreateCalls == calls && prompts == (retryStopped ? 1 : 0), "Stopped/failed batch restarted without an event.");
        Check(!worker.HasCreated && !worker.HasFailed, "Stopped/failed batch emitted repeated notifications.");
        store.FailPrune = false;
        store.FailCreate = false;
        await WriteAsync(profile.SaveDirectory, "unreported.sav", "recover-all");
        await WriteAsync(profile.SaveDirectory, "slot.sav", "recovered");
        worker.Change("slot.sav");
        var recovered = await worker.CreatedAsync();
        Check(store.LastWasFull, "A failed batch lost pending changes on the next event.");
        await AssertContentsAsync(recovered, ("slot.sav", "recovered"), ("unreported.sav", "recover-all"));
    }

    private static async Task CancelTrailingAsync(string root)
    {
        var profile = Profile(root, TimeSpan.FromSeconds(30));
        await WriteAsync(profile.SaveDirectory, "slot.sav", "first");
        var store = new ObservedStore();
        var service = new GameBackupService(profile, Path.Combine(root, "backup"), store);
        var worker = new Worker(profile, service);
        try
        {
            worker.Change("slot.sav");
            await worker.CreatedAsync();
            await WriteAsync(profile.SaveDirectory, "slot.sav", "pending");
            worker.Change("slot.sav");
            worker.Overflow();
            Check(await worker.FailedAsync() is InternalBufferOverflowException, "Overflow diagnostic missing.");
            await Task.Delay(TimeSpan.FromMilliseconds(1000));
        }
        finally
        {
            await worker.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }

        Check(store.CreateCalls == 1 && !worker.HasFailed, "Cancellation created or reported a failed trailing backup.");
        // A replacement monitor must carry out its own initial calibration after cancelled work.
        var replacementProfile = profile with { MinimumBackupInterval = TimeSpan.Zero };
        await using var replacement = new Worker(replacementProfile,
            new GameBackupService(replacementProfile, Path.Combine(root, "backup"), store));
        replacement.Change("slot.sav");
        await AssertContentsAsync(await replacement.CreatedAsync(), ("slot.sav", "pending"));
        Check(store.LastWasFull, "Replacement monitor inherited cancelled incremental state.");
    }

    private static async Task StartDoesNotCreateMissingSourceAsync(string root)
    {
        var profile = Profile(root);
        await using var monitor = new GameBackupMonitor(profile, new GameBackupService(profile, Path.Combine(root, "backup")));
        try
        {
            monitor.Start();
            throw new Exception("Missing source unexpectedly accepted a file watcher.");
        }
        catch (ArgumentException)
        {
        }
        Check(!Directory.Exists(profile.SaveDirectory), "Starting monitoring manufactured an empty source directory.");
    }

    private static async Task AssertContentsAsync(SnapshotCreationResult result, params (string Path, string Content)[] expected)
    {
        var manifest = await SnapshotContent.ReadManifestAsync(result.SnapshotDirectory);
        Check(manifest.Files.Count == expected.Length, "Snapshot includes stale or missing paths.");
        foreach (var (path, content) in expected)
        {
            var entry = manifest.Files.SingleOrDefault(file => file.RelativePath == path)
                ?? throw new Exception($"Snapshot lacks {path}.");
            var verified = await SnapshotContent.ResolveVerifiedFileAsync(result.SnapshotDirectory, entry);
            Check(await File.ReadAllTextAsync(verified) == content, $"Snapshot content differs for {path}.");
        }
    }

    private static async Task WriteAsync(string root, string path, string content)
    {
        var full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content);
    }

    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new Exception(message); }
    }

    // Exercise the real channel/debounce/trailing worker with deterministic watcher events.
    // OS watcher integration is already covered in Program; no reflection changes product state.
    private sealed class Worker : IAsyncDisposable
    {
        private readonly GameBackupProfile _profile;
        private readonly GameBackupMonitor _monitor;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _task;
        private readonly Channel<SnapshotCreationResult> _created = Channel.CreateUnbounded<SnapshotCreationResult>();
        private readonly Channel<Exception> _failed = Channel.CreateUnbounded<Exception>();

        public Worker(GameBackupProfile profile, GameBackupService service)
        {
            _profile = profile;
            _monitor = new GameBackupMonitor(profile, service);
            _monitor.BackupCreated += (_, result) => _created.Writer.TryWrite(result);
            _monitor.BackupFailed += (_, exception) => _failed.Writer.TryWrite(exception);
            _task = (Task)Method("RunFileChangesAsync").Invoke(_monitor, [_stop.Token])!;
        }

        public bool HasCreated => _created.Reader.TryPeek(out _);
        public bool HasFailed => _failed.Reader.TryPeek(out _);
        public Task<SnapshotCreationResult> CreatedAsync() => _created.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        public Task<Exception> FailedAsync() => _failed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        public void Change(string path) => Method("TrackChange").Invoke(_monitor, [Path.Combine(_profile.SaveDirectory, path)]);
        public void Overflow() => Method("OnWatcherError").Invoke(_monitor, [_monitor, new ErrorEventArgs(new InternalBufferOverflowException())]);
        private static MethodInfo Method(string name) => typeof(GameBackupMonitor).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new Exception($"Monitor method missing: {name}.");

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try { await _task; }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            await _monitor.DisposeAsync();
            _stop.Dispose();
        }
    }

    private sealed class ObservedStore : IBackupSnapshotStore
    {
        private readonly SnapshotStore _inner = new();
        public int CreateCalls;
        public int PruneCalls;
        public bool LastWasFull;
        public bool FailPrune;
        public bool FailCreate;
        public Func<Task>? AfterCreateAsync;

        public async Task<SnapshotCreationResult> CreateAsync(Guid profileId, string sourceDirectory, string backupDirectory,
            string trigger, CancellationToken cancellationToken = default, IReadOnlyCollection<string>? changedPaths = null)
        {
            Interlocked.Increment(ref CreateCalls);
            LastWasFull = changedPaths is null;
            if (FailCreate) { throw new InvalidOperationException("Injected non-retryable create failure."); }
            var result = await _inner.CreateAsync(profileId, sourceDirectory, backupDirectory, trigger, cancellationToken, changedPaths);
            if (AfterCreateAsync is { } afterCreate) { await afterCreate(); }
            return result;
        }

        public Task<int> PruneAsync(Guid profileId, string backupDirectory, SnapshotRetentionPolicy policy, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref PruneCalls);
            if (FailPrune) { throw new IOException("Injected prune failure."); }
            return _inner.PruneAsync(profileId, backupDirectory, policy, cancellationToken);
        }
    }
}
