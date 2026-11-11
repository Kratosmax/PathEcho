using System.Text.Json;
using PathEcho.Core.Backup;

internal static class ObjectIntegrityTests
{
    internal static async Task RunAsync(string testRoot)
    {
        var root = Path.Combine(testRoot, "object-integrity");
        var source = Path.Combine(root, "save"); var backup = Path.Combine(root, "backup");
        Directory.CreateDirectory(source); await File.WriteAllTextAsync(Path.Combine(source, "slot.sav"), "GOOD");
        var store = new SnapshotStore(); var id = Guid.NewGuid();
        var first = await store.CreateAsync(id, source, backup, "manual");
        var manifest = await SnapshotContent.ReadManifestAsync(first.SnapshotDirectory);
        var entry = manifest.Files.Single(); var blob = SnapshotContent.ResolveFile(first.SnapshotDirectory, entry);
        File.SetAttributes(blob, FileAttributes.Normal); await File.WriteAllTextAsync(blob, "EVIL");
        var second = await store.CreateAsync(id, source, backup, "manual");
        var path = await SnapshotContent.ResolveVerifiedFileAsync(second.SnapshotDirectory, entry);
        if (await File.ReadAllTextAsync(path) != "GOOD") throw new InvalidOperationException("Later full backup reused a corrupted object cached by a previous operation.");
        // A valid legacy copy must not be removed merely because its object path was trusted before.
        var legacyRoot = Path.Combine(first.SnapshotDirectory, "files"); Directory.CreateDirectory(legacyRoot);
        await File.WriteAllTextAsync(Path.Combine(legacyRoot, "slot.sav"), "GOOD");
        await File.WriteAllTextAsync(Path.Combine(first.SnapshotDirectory, "manifest.json"), JsonSerializer.Serialize(manifest with { SchemaVersion = 1 }));
        File.SetAttributes(blob, FileAttributes.Normal); await File.WriteAllTextAsync(blob, "EVIL");
        await store.PruneAsync(id, backup, new SnapshotRetentionPolicy(50, 0, 0));
        if (!File.Exists(Path.Combine(legacyRoot, "slot.sav"))) throw new InvalidOperationException("Pruning destroyed the valid legacy fallback while its content object was corrupted.");
        await SnapshotContent.ResolveVerifiedFileAsync(first.SnapshotDirectory, entry);
    }
}
