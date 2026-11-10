using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using PathEcho.Core.Backup;
using PathEcho.Core.Models;
using PathEcho.Core.Storage;

internal static class ImportedBackupDiscoveryTests
{
    internal static async Task RunAsync(string testRoot)
    {
        var root = Path.Combine(testRoot, "imported-history");
        var oldCloud = Path.Combine(root, "old-cloud");
        var newCloud = Path.Combine(root, "new-cloud");
        var save = Path.Combine(root, "source-save");
        Directory.CreateDirectory(save);
        await File.WriteAllTextAsync(Path.Combine(save, "slot.sav"), "original progress");
        var source = new GameBackupProfile
        {
            Id = Guid.Parse("9b74b71f-244f-47da-84c6-7e0abff7f3e3"),
            Name = "switch2模拟器存档",
            SaveDirectory = save,
            BackupDirectory = oldCloud,
            IncludeOtherDeviceBackups = true,
            Triggers = BackupTrigger.ChangedFiles,
        };
        var store = new SnapshotStore();
        var snapshot = await store.CreateAsync(source.Id, save, oldCloud, "fixture");
        var export = Path.Combine(root, "export.pathecho-game.json");
        await GameProfileTransfer.ExportAsync(source, oldCloud, export);
        var imported = await GameProfileTransfer.ReadImportDraftAsync(export, []);
        Assert(imported.Id != source.Id && imported.LinkedBackupProfileIds.Contains(source.Id), "Imported write identity must remain separate from its historical source.");
        imported = imported with { SaveDirectory = Path.Combine(root, "different-local-save") };
        Assert((await ListAsync(imported)).Count == 1, "Changing only the local save path hid backup history.");
        Directory.Move(oldCloud, newCloud);
        imported = imported with { BackupDirectory = newCloud };
        Assert((await ListAsync(imported)).Count == 1, "Changing the backup root to the copied cloud directory hid source history.");
        var sourceRoot = Path.Combine(newCloud, source.Id.ToString("N"));
        var renamed = Path.Combine(newCloud, source.Id.ToString("D").ToUpperInvariant());
        Directory.Move(sourceRoot, renamed);
        Assert((await ListAsync(imported)).Count == 1, "Hyphenated profile directories were ignored.");
        var container = Path.Combine(newCloud, "PathEcho备份", "switch2");
        Directory.CreateDirectory(container);
        var nested = Path.Combine(container, source.Id.ToString("N"));
        Directory.Move(renamed, nested);
        Assert((await ListAsync(imported)).Count == 1, "Explicit imported source link was lost below an extra cloud folder.");
        Assert((await ListAsync(imported with { IncludeOtherDeviceBackups = false })).Count == 1, "Turning off broad discovery must not remove explicit source links.");
        Assert((await ListAsync(imported with { BackupDirectory = nested })).Count == 1, "Selecting the source profile directory itself failed.");
        var unrelated = Guid.NewGuid();
        await store.CreateAsync(unrelated, save, container, "unrelated nested game");
        Assert((await ListAsync(imported)).Count == 1, "Recursive lookup associated an unrelated nested game.");
        await store.CreateAsync(unrelated, save, newCloud, "explicit top-level discovery");
        Assert((await ListAsync(imported)).Count == 2, "Existing explicit broad top-level discovery changed.");
        Assert((await ListAsync(imported with { IncludeOtherDeviceBackups = false })).Count == 1, "A shared-root import included a different profile.");
        var versions = await ListAsync(imported with { IncludeOtherDeviceBackups = false });
        var manifest = versions.Single().Manifest;
        var blob = SnapshotContent.ResolveFile(versions.Single().SnapshotDirectory, manifest.Files.Single());
        var original = await File.ReadAllBytesAsync(blob);
        File.Delete(blob);
        Assert(!(await ListAsync(imported)).Single(v => v.Manifest.ProfileId == source.Id).IsContentAvailable, "Partial cloud content should appear as pending, not disappear.");
        await File.WriteAllBytesAsync(blob, original);
        Assert((await ListAsync(imported)).Single(v => v.Manifest.ProfileId == source.Id).IsContentAvailable, "Arriving cloud content did not restore availability.");
        var manifestPath = Path.Combine(versions.Single().SnapshotDirectory, "manifest.json");
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, "{");
        Assert((await ListAsync(imported)).Count == 1, "Malformed cloud manifest hid unrelated valid versions.");
        await File.WriteAllBytesAsync(manifestPath, manifestBytes);
        var before = SHA256.HashData(await File.ReadAllBytesAsync(manifestPath));
        var configPath = Path.Combine(root, "configuration.json");
        var configStore = new JsonConfigurationStore(configPath);
        await configStore.SaveAsync(new AppConfiguration { GameProfiles = [imported] });
        var reloaded = (await configStore.LoadAsync()).GameProfiles.Single();
        Assert((await ListAsync(reloaded)).Count == 2, "Configuration reload lost the changed root or source linkage.");
        var after = SHA256.HashData(await File.ReadAllBytesAsync(manifestPath));
        Assert(before.SequenceEqual(after), "Discovery changed a source manifest.");
        Assert(!Directory.Exists(Path.Combine(newCloud, imported.Id.ToString("N"))), "Discovery created a new write tree.");
        var canceled = false;
        try { await store.ListAsync(imported.Id, newCloud, new CancellationToken(true), true, imported.LinkedBackupProfileIds); }
        catch (OperationCanceledException) { canceled = true; }
        Assert(canceled, "Directory discovery ignored cancellation.");
        await TestRedirectingLinksAsync(Path.Combine(root, "links"));
        await TestDiagnosticEventsAsync(Path.Combine(root, "diagnostics"));

        Task<IReadOnlyList<SnapshotVersion>> ListAsync(GameBackupProfile profile) =>
            store.ListAsync(profile.Id, profile.BackupDirectory!, includeOtherProfiles: profile.IncludeOtherDeviceBackups,
                linkedProfileIds: profile.LinkedBackupProfileIds);
    }

    private static async Task TestDiagnosticEventsAsync(string root)
    {
        var diagnostics = new List<BackupHistoryDiagnostic>();
        var store = new SnapshotStore();
        var id = Guid.NewGuid();
        await store.ListAsync(id, Path.Combine(root, "missing"), diagnostic: diagnostics.Add);
        Assert(diagnostics.Any(item => item.Code == "RootUnavailable"), "Missing root has no diagnostic.");
        var save = Path.Combine(root, "save");
        Directory.CreateDirectory(save);
        await File.WriteAllTextAsync(Path.Combine(save, "slot.sav"), "progress");
        var backup = Path.Combine(root, "backup");
        var snapshot = await store.CreateAsync(id, save, backup, "diagnostic fixture");
        diagnostics.Clear();
        Assert((await store.ListAsync(id, backup, diagnostic: diagnostics.Add)).Count == 1, "Valid diagnostic fixture is missing.");
        Assert(diagnostics.Any(item => item.Code == "ProfileDirectoriesLocated") && diagnostics.Any(item => item.Code == "Completed"), "Discovery counts are not reported.");
        var manifestPath = Path.Combine(snapshot.SnapshotDirectory, "manifest.json");
        var bytes = await File.ReadAllBytesAsync(manifestPath);
        var manifest = await SnapshotContent.ReadManifestAsync(snapshot.SnapshotDirectory);
        File.Delete(SnapshotContent.ResolveFile(snapshot.SnapshotDirectory, manifest.Files.Single()));
        diagnostics.Clear();
        await store.ListAsync(id, backup, diagnostic: diagnostics.Add);
        Assert(diagnostics.Any(item => item.Code == "ContentPending"), "Missing object has no diagnostic.");
        await File.WriteAllTextAsync(manifestPath, "{");
        diagnostics.Clear();
        await store.ListAsync(id, backup, diagnostic: diagnostics.Add);
        Assert(diagnostics.Any(item => item.Code == "ManifestReadFailed"), "Malformed manifest is silently ignored.");
        await File.WriteAllBytesAsync(manifestPath, bytes);
        Assert((await store.ListAsync(id, backup, diagnostic: _ => throw new IOException("broken log sink"))).Count == 1,
            "A diagnostic sink failure changed discovery results.");
    }

    private static async Task TestRedirectingLinksAsync(string root)
    {
        var outside = Path.Combine(root, "outside");
        var save = Path.Combine(root, "save");
        var cloud = Path.Combine(root, "cloud");
        Directory.CreateDirectory(save);
        Directory.CreateDirectory(cloud);
        await File.WriteAllTextAsync(Path.Combine(save, "slot.sav"), "outside content");
        var id = Guid.NewGuid();
        var snapshot = await new SnapshotStore().CreateAsync(id, save, outside, "outside");
        var outsideProfile = Path.Combine(outside, id.ToString("N"));
        var linkedProfile = Path.Combine(cloud, id.ToString("N"));
        await CreateLinkAsync(linkedProfile, outsideProfile);
        try
        {
            Assert((await new SnapshotStore().ListAsync(id, cloud)).Count == 0, "Discovery followed a profile directory junction.");
        }
        finally { Directory.Delete(linkedProfile); }
        Directory.CreateDirectory(linkedProfile);
        var linkedSnapshots = Path.Combine(linkedProfile, "snapshots");
        await CreateLinkAsync(linkedSnapshots, Path.Combine(outsideProfile, "snapshots"));
        try
        {
            Assert((await new SnapshotStore().ListAsync(id, cloud)).Count == 0, "Discovery followed a snapshots junction.");
        }
        finally { Directory.Delete(linkedSnapshots); }
        Directory.CreateDirectory(linkedSnapshots);
        var linkedVersion = Path.Combine(linkedSnapshots, "version");
        await CreateLinkAsync(linkedVersion, snapshot.SnapshotDirectory);
        try
        {
            Assert((await new SnapshotStore().ListAsync(id, cloud)).Count == 0, "Discovery followed a snapshot version junction.");
        }
        finally { Directory.Delete(linkedVersion); }

        var rootLink = Path.Combine(root, "linked-root");
        await CreateLinkAsync(rootLink, outside);
        try
        {
            Assert((await new SnapshotStore().ListAsync(id, rootLink)).Count == 0, "Known-ID fast path followed a selected root junction.");
        }
        finally { Directory.Delete(rootLink); }
        var cachedCloud = Path.Combine(root, "cached-cloud");
        var cachedParent = Path.Combine(cachedCloud, "container");
        Directory.CreateDirectory(cachedParent);
        var profileCopy = Path.Combine(cachedParent, id.ToString("N"));
        await new BackupDirectoryManager().ImportAsync(new DiscoveredBackup(id, outsideProfile, 1, DateTimeOffset.UtcNow), cachedParent);
        var cachedStore = new SnapshotStore();
        Assert((await cachedStore.ListAsync(id, cachedCloud)).Count == 1, "Nested cache fixture was not located.");
        var movedParent = Path.Combine(root, "outside-container");
        Directory.Move(cachedParent, movedParent);
        await CreateLinkAsync(cachedParent, movedParent);
        try
        {
            Assert((await cachedStore.ListAsync(id, cachedCloud)).Count == 0, "Cached history followed a redirected ancestor.");
        }
        finally { Directory.Delete(cachedParent); }

        static async Task CreateLinkAsync(string link, string target)
        {
            if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
            var start = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                Arguments = $"/d /c mklink /J \"{link}\" \"{target}\""
            };
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not create junction fixture.");
            await process.WaitForExitAsync();
            Assert(process.ExitCode == 0, "Could not create junction fixture.");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
