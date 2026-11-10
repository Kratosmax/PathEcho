using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using PathEcho.Core.Backup;

internal static class HistoryScanTests
{
    internal static async Task RunAsync(string testRoot)
    {
        var root = Path.Combine(testRoot, "history-scan");
        Directory.CreateDirectory(root);
        await TestManifestArrivalAsync(Path.Combine(root, "arrival"));
        await TestFaultMatrixAsync(Path.Combine(root, "faults"));
        await TestIdentityAndFreshDiscoveryAsync(Path.Combine(root, "identity"));
        await TestBoundsAndCancellationAsync(Path.Combine(root, "bounds"));
        await TestSingleFlightAsync(Path.Combine(root, "single-flight"));
        await TestByteBudgetAsync(Path.Combine(root, "byte-budget"));
        await TestLinksAsync(Path.Combine(root, "links"));
    }

    private static async Task TestManifestArrivalAsync(string root)
    {
        var fixture = CreateFixture(root);
        var store = new SnapshotStore();
        var request = Request(fixture) with { OperationId = Guid.NewGuid(), ParentOperationId = Guid.NewGuid(), ConfigRevision = 37 };
        var missing = await store.ScanAsync(request);
        Assert(missing.IsComplete && missing.Versions.Count == 0, "Missing manifest created an executable version or an unfinished scan.");
        AssertReason(missing, HistoryReasonCodes.MANIFEST_MISSING);
        Assert(missing.Summary.AwaitingManifest == 1 && missing.Summary.CandidateDirectories == 1,
            "Existing snapshot without manifest must be counted as awaiting metadata.");
        Assert(missing.OperationId == request.OperationId && missing.ParentOperationId == request.ParentOperationId &&
            missing.ConfigRevision == 37 && missing.ProfileId == fixture.Id && missing.DurationMs >= 0 && missing.StartedAtUtc != default,
            "Report lost operation identity or timing.");
        await WriteManifestAsync(fixture);
        var manifestBytes = await File.ReadAllBytesAsync(ManifestPath(fixture));
        var pending = await store.ScanAsync(request);
        AssertReason(pending, HistoryReasonCodes.OBJECT_MISSING);
        Assert(pending.Versions.Count == 1 && !pending.Versions[0].IsContentAvailable && pending.Summary.AwaitingContent == 1,
            "Valid metadata with missing content should remain visible but unavailable.");
        await WriteObjectAsync(fixture);
        // Deliberately use a synthetic valid hash: this scan checks metadata, not object bytes.
        using (var locked = new FileStream(ObjectPath(fixture), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ready = await store.ScanAsync(request);
            AssertReason(ready, HistoryReasonCodes.READY);
            Assert(ready.Summary.ReadyVersions == 1 && ready.Versions.Single().IsContentAvailable,
                "Arriving content did not recover to metadata-ready, or scan opened object contents.");
        }
        var afterBytes = await File.ReadAllBytesAsync(ManifestPath(fixture));
        Assert(manifestBytes.SequenceEqual(afterBytes), "Read-only diagnosis modified the manifest.");
        Assert((await store.ListAsync(fixture.Id, root, diagnostic: _ => throw new IOException("broken sink"))).Count == 1,
            "Existing ListAsync consumers or diagnostic sink resilience regressed.");
    }

    private static async Task TestFaultMatrixAsync(string root)
    {
        var fixture = CreateFixture(root);
        var store = new SnapshotStore();
        await File.WriteAllTextAsync(ManifestPath(fixture), "{");
        AssertReason(await store.ScanAsync(Request(fixture)), HistoryReasonCodes.MANIFEST_INVALID_JSON);
        await WriteManifestAsync(fixture);
        var valid = await File.ReadAllTextAsync(ManifestPath(fixture));
        foreach (var mutate in new Action<JsonObject>[]
        {
            json => json["SchemaVersion"] = 3,
            json => json["Trigger"] = new string('x', 4097),
            json => json["Files"]![0]!["RelativePath"] = new string('x', 32768),
            json => json["SchemaVersion"] = "2",
            json => json.Remove("SchemaVersion"),
            json => json["Files"] = null,
            json => json["ProfileId"] = Guid.Empty.ToString(),
            json => json["Files"]![0]!["Sha256"] = "../../escape",
            json => json["Files"]![0]!["Length"] = -1,
            json => json["Files"]![0]!["RelativePath"] = "../outside.sav",
            json => json["Files"]![0]!["RelativePath"] = "C:\\outside.sav",
            json => json["Files"]![0]!["RelativePath"] = "slot.sav:stream",
            json => json["Files"]![0]!["RelativePath"] = "folder/.. /outside.sav",
            json => json["Files"]![0]!["RelativePath"] = "NUL.sav",
            json => json["Files"]![0]!["LastWriteUtcTicks"] = long.MaxValue,
        })
        {
            var json = JsonNode.Parse(valid)!.AsObject();
            mutate(json);
            await File.WriteAllTextAsync(ManifestPath(fixture), json.ToJsonString());
            var invalid = await store.ScanAsync(Request(fixture));
            AssertReason(invalid, HistoryReasonCodes.MANIFEST_INVALID_SCHEMA);
            Assert(invalid.Versions.Count == 0 && invalid.Summary.ManifestInvalid == 1, "Invalid schema produced a version.");
        }
        await File.WriteAllTextAsync(ManifestPath(fixture), valid);
        await WriteObjectAsync(fixture, [1]);
        AssertReason(await store.ScanAsync(Request(fixture)), HistoryReasonCodes.OBJECT_LENGTH_MISMATCH);
        File.Delete(ObjectPath(fixture));
        var legacy = Path.Combine(fixture.Snapshot, "files", "slot.sav");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        await File.WriteAllBytesAsync(legacy, [1, 2, 3]);
        AssertReason(await store.ScanAsync(Request(fixture)), HistoryReasonCodes.READY);

        await WriteObjectAsync(fixture);
        var sibling = Path.Combine(Path.GetDirectoryName(fixture.Snapshot)!, "broken-sibling");
        Directory.CreateDirectory(sibling);
        await File.WriteAllTextAsync(Path.Combine(sibling, "manifest.json"), "{");
        var partial = await store.ScanAsync(Request(fixture));
        Reconcile(partial);
        Assert(partial.IsComplete && partial.Versions.Count == 1 && partial.Summary.ReadyVersions == 1 && partial.Summary.ManifestInvalid == 1,
            "Malformed manifest hid an unrelated valid version.");
        Directory.Delete(sibling, true);
        var cloudStore = new SnapshotStore
        {
            HistoryScanProbe = (stage, _) =>
        { if (stage == "manifest.checked") throw new IOException("isolated cloud fault", unchecked((int)0x80070185)); }
        };
        AssertReason(await cloudStore.ScanAsync(Request(fixture)), HistoryReasonCodes.MANIFEST_CLOUD_UNAVAILABLE);
        var genericIoStore = new SnapshotStore
        {
            HistoryScanProbe = (stage, _) =>
        { if (stage == "manifest.checked") throw new IOException("isolated generic fault"); }
        };
        AssertReason(await genericIoStore.ScanAsync(Request(fixture)), HistoryReasonCodes.IO_ERROR);
        var deniedManifest = new SnapshotStore
        {
            HistoryScanProbe = (stage, _) =>
        { if (stage == "manifest.checked") throw new UnauthorizedAccessException(); }
        };
        var denied = await deniedManifest.ScanAsync(Request(fixture));
        AssertReason(denied, HistoryReasonCodes.MANIFEST_ACCESS_DENIED);
        Assert(denied.Inspections.Single().ErrorType == nameof(UnauthorizedAccessException) && denied.Summary.AccessDenied == 1,
            "Manifest access error lost its type or classification.");
        var deniedObject = new SnapshotStore
        {
            HistoryScanProbe = (stage, _) =>
        { if (stage == "content.checked") throw new UnauthorizedAccessException(); }
        };
        var objectDenied = await deniedObject.ScanAsync(Request(fixture));
        AssertReason(objectDenied, HistoryReasonCodes.OBJECT_ACCESS_DENIED);
        Assert(objectDenied.Versions.Count == 1 && !objectDenied.Versions[0].IsContentAvailable, "Object denial enabled or hid valid pending metadata.");
        var vanished = new SnapshotStore
        {
            HistoryScanProbe = (stage, path) =>
        { if (stage == "manifest.checked") Directory.Delete(path, true); }
        };
        AssertReason(await vanished.ScanAsync(Request(fixture)), HistoryReasonCodes.MANIFEST_MISSING);
        var missingRoot = await store.ScanAsync(Request(fixture) with { BackupRoot = Path.Combine(root, "never-existed") });
        Assert(!missingRoot.IsComplete && missingRoot.Issues.Any(item => item.ReasonCode == HistoryReasonCodes.ROOT_NOT_FOUND),
            "Missing root masqueraded as clean empty history.");
        var deniedRoot = new SnapshotStore
        {
            HistoryScanProbe = (stage, _) =>
        { if (stage == "root.checked") throw new UnauthorizedAccessException(); }
        };
        var rootDenied = await deniedRoot.ScanAsync(Request(fixture));
        Assert(!rootDenied.IsComplete && rootDenied.Issues.Any(item => item.ReasonCode == HistoryReasonCodes.ROOT_ACCESS_DENIED),
            "Root permission error was reported as missing data.");
    }

    private static async Task TestIdentityAndFreshDiscoveryAsync(string root)
    {
        var fixture = CreateFixture(root);
        var store = new SnapshotStore();
        await WriteManifestAsync(fixture, fixture.Manifest with { ProfileId = Guid.NewGuid() });
        AssertReason(await store.ScanAsync(Request(fixture)), HistoryReasonCodes.PROFILE_ID_MISMATCH);
        await WriteManifestAsync(fixture);
        await WriteObjectAsync(fixture);
        var sourceDeniedStore = new SnapshotStore
        {
            HistoryScanProbe = (stage, path) =>
        { if (stage == "source.located" && Path.GetFileName(path) == fixture.Id.ToString("N")) throw new UnauthorizedAccessException(); }
        };
        var sourceDenied = await sourceDeniedStore.ScanAsync(Request(fixture));
        Assert(!sourceDenied.IsComplete && sourceDenied.Issues.Any(item => item.ErrorType == nameof(UnauthorizedAccessException)) &&
            !sourceDenied.Issues.Any(item => item.ReasonCode == HistoryReasonCodes.SOURCE_NOT_FOUND),
            "Source access failure was incorrectly reported as nonexistent data.");
        var localId = Guid.NewGuid();
        var linked = Request(fixture) with { ProfileId = localId, SourceProfileIds = [fixture.Id] };
        AssertReason(await store.ScanAsync(linked), HistoryReasonCodes.READY);
        var unassociated = await store.ScanAsync(linked with { SourceProfileIds = [] });
        Assert(unassociated.Versions.Count == 0 && unassociated.Issues.Any(item => item.ReasonCode == HistoryReasonCodes.SOURCE_UNASSOCIATED),
            "Unlinked source was silently associated.");
        var container = Path.Combine(root, "fresh", "cloud-folder");
        Directory.CreateDirectory(container);
        var freshRoot = Path.Combine(root, "fresh");
        await store.ScanAsync(linked with { BackupRoot = freshRoot });
        Directory.Move(Path.Combine(root, fixture.Id.ToString("N")), Path.Combine(container, fixture.Id.ToString("D")));
        AssertReason(await store.ScanAsync(linked with { BackupRoot = freshRoot }), HistoryReasonCodes.READY);
        var nested = await store.ScanAsync(linked with { BackupRoot = freshRoot, SourceProfileIds = [], IncludeOtherProfiles = true });
        Assert(nested.Versions.Count == 0, "Broad top-level policy included an unlinked nested game.");
        AssertReason(await store.ScanAsync(linked with { BackupRoot = container, SourceProfileIds = [], IncludeOtherProfiles = true }), HistoryReasonCodes.READY);
        var renamedRoot = Path.Combine(root, "single-profile");
        Directory.Move(Path.Combine(container, fixture.Id.ToString("D")), renamedRoot);
        var wrongId = await store.ScanAsync(linked with { BackupRoot = renamedRoot, SourceProfileIds = [] });
        AssertReason(wrongId, HistoryReasonCodes.PROFILE_UNASSOCIATED);
    }

    private static async Task TestBoundsAndCancellationAsync(string root)
    {
        var fixture = CreateFixture(root);
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(fixture.Snapshot)!, "second"));
        var store = new SnapshotStore();
        var limited = await store.ScanAsync(Request(fixture) with { MaximumSnapshots = 1 });
        Assert(!limited.IsComplete && limited.Outcome == "Incomplete" && limited.Summary.CandidateDirectories == 1 &&
            limited.Issues.Any(item => item.ReasonCode == HistoryReasonCodes.SCAN_INCOMPLETE), "Snapshot bound silently looked complete.");
        Reconcile(limited);
        var directoryLimited = await store.ScanAsync(Request(fixture) with { MaximumDirectories = 1 });
        Assert(!directoryLimited.IsComplete && directoryLimited.Issues.Any(item => item.ReasonCode == HistoryReasonCodes.SCAN_INCOMPLETE),
            "Directory bound silently looked complete.");
        var canceled = await store.ScanAsync(Request(fixture), new CancellationToken(true));
        Assert(canceled.Outcome == "Canceled" && !canceled.IsComplete && canceled.Issues.Any(item => item.ReasonCode == HistoryReasonCodes.CANCELED),
            "Canceled scan masqueraded as empty history.");
        var timeout = await store.ScanAsync(Request(fixture) with { Timeout = TimeSpan.Zero });
        Assert(timeout.Outcome == "Timeout" && !timeout.IsComplete, "Expired scan masqueraded as empty history.");
        Reconcile(timeout);
    }

    private static async Task TestByteBudgetAsync(string root)
    {
        var fixture = CreateFixture(root);
        await WriteManifestAsync(fixture);
        await WriteObjectAsync(fixture);
        var bytes = await File.ReadAllBytesAsync(ManifestPath(fixture));
        var second = Path.Combine(Path.GetDirectoryName(fixture.Snapshot)!, "second-version");
        Directory.CreateDirectory(second);
        await File.WriteAllBytesAsync(Path.Combine(second, "manifest.json"), bytes);
        var report = await new SnapshotStore().ScanAsync(Request(fixture) with { MaximumManifestBytes = bytes.Length + 16 });
        Assert(!report.IsComplete && report.Outcome == "Incomplete" && report.Summary.ReadyVersions == 1 &&
            report.Issues.Any(item => item.ReasonCode == HistoryReasonCodes.SCAN_INCOMPLETE),
            "Aggregate manifest byte budget did not stop before retaining another decoded manifest.");
        Reconcile(report);
    }

    private static async Task TestSingleFlightAsync(string root)
    {
        var fixture = CreateFixture(root);
        await WriteManifestAsync(fixture);
        await WriteObjectAsync(fixture);
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var store = new SnapshotStore
        {
            HistoryScanProbe = (stage, _) =>
        {
            if (stage != "root.checked") return;
            Interlocked.Increment(ref attempts);
            entered.TrySetResult();
            release.Wait(); // Simulate an OS call that ignores cancellation.
        }
        };
        try
        {
            var first = store.ScanAsync(Request(fixture) with { Timeout = TimeSpan.FromMilliseconds(100) });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var timedOut = await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(timedOut.Outcome == "Timeout", "Stalled worker did not return a bounded timeout report.");
            var second = await store.ScanAsync(Request(fixture));
            Assert(second.Outcome == "Incomplete" && attempts == 1, "Timed-out I/O allowed another worker to stack.");
            Reconcile(second);
        }
        finally { release.Set(); }
        // The old worker releases its gate only after its actual return. Fresh calls may see
        // Incomplete briefly, but must eventually observe the arriving metadata.
        var recovered = false;
        for (var retry = 0; retry < 100; retry++)
        {
            var report = await store.ScanAsync(Request(fixture));
            if (report.IsComplete) { recovered = report.Summary.ReadyVersions == 1; break; }
            await Task.Delay(10);
        }
        Assert(recovered, "Single-flight gate did not recover after the blocked I/O finished.");

        using var cancel = new CancellationTokenSource();
        var canceledStore = new SnapshotStore { HistoryScanProbe = (stage, _) => { if (stage == "manifest.checked") cancel.Cancel(); } };
        var canceledReport = await canceledStore.ScanAsync(Request(fixture), cancel.Token);
        Assert(canceledReport.Outcome == "Canceled" && !canceledReport.IsComplete, "In-progress cancellation lost its explicit outcome.");
        Reconcile(canceledReport);
    }

    private static async Task TestLinksAsync(string root)
    {
        var fixture = CreateFixture(Path.Combine(root, "outside"));
        await WriteManifestAsync(fixture);
        await WriteObjectAsync(fixture);
        var cloud = Path.Combine(root, "cloud");
        Directory.CreateDirectory(cloud);
        var profileLink = Path.Combine(cloud, fixture.Id.ToString("N"));
        await CreateDirectoryLinkAsync(profileLink, Path.Combine(fixture.Root, fixture.Id.ToString("N")));
        try
        {
            var report = await new SnapshotStore().ScanAsync(Request(fixture) with { BackupRoot = cloud });
            Assert(report.Versions.Count == 0 && report.Issues.Any(item => item.ReasonCode == HistoryReasonCodes.LINK_SKIPPED),
                "Source discovery traversed a redirecting link.");
        }
        finally { Directory.Delete(profileLink); }
        var link = Path.Combine(Path.GetDirectoryName(fixture.Snapshot)!, "linked-version");
        await CreateDirectoryLinkAsync(link, fixture.Snapshot);
        try
        {
            var report = await new SnapshotStore().ScanAsync(Request(fixture));
            Assert(report.Summary.ReadyVersions == 1 && report.Summary.SkippedByPolicy == 1,
                "Snapshot junction was traversed or omitted from candidate accounting.");
            Reconcile(report);
        }
        finally { Directory.Delete(link); }
        var objectDirectory = Path.GetDirectoryName(ObjectPath(fixture))!;
        var movedObjects = Path.Combine(root, "redirected-objects");
        Directory.Move(objectDirectory, movedObjects);
        await CreateDirectoryLinkAsync(objectDirectory, movedObjects);
        try
        {
            var report = await new SnapshotStore().ScanAsync(Request(fixture));
            AssertReason(report, HistoryReasonCodes.LINK_SKIPPED);
            Assert(report.Versions.All(version => !version.IsContentAvailable), "Object link enabled a restoreable version.");
        }
        finally { Directory.Delete(objectDirectory); }
    }

    private static async Task CreateDirectoryLinkAsync(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        { UseShellExecute = false, CreateNoWindow = true, Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"" })
            ?? throw new InvalidOperationException("Could not create isolated junction fixture.");
        await process.WaitForExitAsync();
        Assert(process.ExitCode == 0, "Could not create isolated junction fixture.");
    }

    private sealed record Fixture(string Root, Guid Id, string Snapshot, SnapshotManifest Manifest);
    private static Fixture CreateFixture(string root)
    {
        var id = Guid.NewGuid();
        var snapshot = Path.Combine(root, id.ToString("N"), "snapshots", "existing-version");
        Directory.CreateDirectory(snapshot);
        return new Fixture(root, id, snapshot, new SnapshotManifest
        {
            SchemaVersion = 2,
            ProfileId = id,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Trigger = "isolated fixture",
            Files = [new SnapshotFileEntry { RelativePath = "slot.sav", Sha256 = new string('A', 64), Length = 3,
                LastWriteUtcTicks = DateTime.UtcNow.Ticks }],
        });
    }
    private static HistoryScanRequest Request(Fixture fixture) => new() { ProfileId = fixture.Id, BackupRoot = fixture.Root };
    private static string ManifestPath(Fixture fixture) => Path.Combine(fixture.Snapshot, "manifest.json");
    private static string ObjectPath(Fixture fixture) => SnapshotContent.GetObjectPath(Path.Combine(fixture.Root, fixture.Id.ToString("N")), fixture.Manifest.Files[0].Sha256);
    private static Task WriteManifestAsync(Fixture fixture, SnapshotManifest? manifest = null) =>
        File.WriteAllTextAsync(ManifestPath(fixture), JsonSerializer.Serialize(manifest ?? fixture.Manifest));
    private static async Task WriteObjectAsync(Fixture fixture, byte[]? bytes = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ObjectPath(fixture))!);
        await File.WriteAllBytesAsync(ObjectPath(fixture), bytes ?? [1, 2, 3]);
    }
    private static void AssertReason(HistoryScanReport report, string reason)
    {
        Reconcile(report);
        Assert(report.Inspections.Count == 1 && report.Inspections[0].ReasonCode == reason,
            $"Expected candidate reason {reason}; got {string.Join(',', report.Inspections.Select(item => item.ReasonCode))} ({report.Outcome}).");
    }
    private static void Reconcile(HistoryScanReport report)
    {
        var summary = report.Summary;
        Assert(summary.CandidateDirectories == report.Inspections.Count && summary.CandidateDirectories ==
            summary.ReadyVersions + summary.AwaitingManifest + summary.ManifestInvalid + summary.IdentityMismatch +
            summary.AwaitingContent + summary.AccessDenied + summary.OtherFailures + summary.SkippedByPolicy,
            "Candidate categories do not reconcile; root/source issues must remain separate.");
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
