using System.IO.Compression;
using System.Text;
using System.Text.Json;
using PathEcho.Core.Backup;
using PathEcho.Core.Diagnostics;

internal static class DiagnosticExportTests
{
    internal static async Task RunAsync(string testRoot)
    {
        var root = Path.Combine(testRoot, "diagnostic-export");
        Directory.CreateDirectory(root);
        var id = Guid.NewGuid(); var op = Guid.NewGuid();
        var backup = @"C:\Users\private-user\SecretGame\Backups";
        var path = backup + @"\private-version\manifest.json";
        var snapshot = new DiagnosticBundleSnapshot
        {
            AppVersion = "0.8.0",
            BuildCommit = "test",
            OsVersion = "Windows",
            RuntimeVersion = "8",
            ConfigRevision = 17,
            Profiles = [new() { ProfileId = id, Name = "SecretGame", BackupDirectory = backup,
                SaveDirectory = @"C:\Users\private-user\private.sav", SourceProfileIds = [id] }],
            Reports = [new() { OperationId = op, ProfileId = id, BackupRoot = backup, ConfigRevision = 17, Outcome = "Completed",
                Summary = new() { CandidateDirectories = 1, AwaitingManifest = 1 },
                Inspections = [new() { SnapshotDirectory = path, SourceProfileId = id, Stage = "manifest.checked", ReasonCode = "MANIFEST_MISSING" }] }],
            Events = [new() { OperationId = op, ProfileId = id, Path = path, Stage = "manifest.checked", Result = "Pending", ReasonCode = "MANIFEST_MISSING" }],
        };
        var destination = Path.Combine(root, "evidence.zip");
        await DiagnosticBundleExporter.ExportAsync(destination, snapshot);
        string ReadAll(string zip)
        {
            using var archive = ZipFile.OpenRead(zip);
            var expected = new[] { "summary.json", "operation-events.jsonl", "effective-settings.json", "backup-layout.json", "environment.json" };
            Assert(archive.Entries.Select(entry => entry.FullName).Order().SequenceEqual(expected.Order()), "Diagnostic ZIP violated its exact allowlist.");
            var text = new StringBuilder();
            foreach (var entry in archive.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                var part = reader.ReadToEnd();
                foreach (var line in entry.FullName.EndsWith("jsonl", StringComparison.Ordinal) ? part.Split('\n', StringSplitOptions.RemoveEmptyEntries) : new[] { part })
                    using (JsonDocument.Parse(line)) { }
                text.Append(part);
            }
            return text.ToString();
        }
        var text = ReadAll(destination);
        foreach (var secret in new[] { "private-user", "SecretGame", "private.sav", "private-version", id.ToString("N"), id.ToString("D"), "C:" })
            Assert(!text.Contains(secret, StringComparison.OrdinalIgnoreCase), "Default export leaked " + secret);
        Assert(text.Contains("MANIFEST_MISSING", StringComparison.Ordinal) && text.Contains("backupRoot-", StringComparison.Ordinal), "Redaction removed diagnostic meaning.");
        var existing = await File.ReadAllBytesAsync(destination);
        var canceled = false;
        try { await DiagnosticBundleExporter.ExportAsync(destination, snapshot, cancellationToken: new CancellationToken(true)); }
        catch (OperationCanceledException) { canceled = true; }
        var after = await File.ReadAllBytesAsync(destination);
        Assert(canceled && existing.SequenceEqual(after), "Canceled export replaced the existing file.");
        Assert(Directory.GetFiles(root, "*.tmp").Length == 0, "Canceled export left temporary files.");
        using var during = new CancellationTokenSource();
        var midCanceled = false;
        try
        {
            await DiagnosticBundleExporter.ExportAsync(destination, snapshot with { Events = new CancelingEvents(during) }, cancellationToken: during.Token);
        }
        catch (OperationCanceledException) { midCanceled = true; }
        var afterMidCancel = await File.ReadAllBytesAsync(destination);
        Assert(midCanceled && existing.SequenceEqual(afterMidCancel) && Directory.GetFiles(root, "*.tmp").Length == 0,
            "Cancellation during ZIP construction damaged the original or left partial evidence.");
        var raw = Path.Combine(root, "explicit-original.zip");
        await DiagnosticBundleExporter.ExportAsync(raw, snapshot, includeOriginalPaths: true);
        Assert(ReadAll(raw).Contains("private-user", StringComparison.Ordinal), "Explicit original-path choice was ignored.");
    }
    private sealed class CancelingEvents(CancellationTokenSource source) : IReadOnlyList<DiagnosticEvent>
    {
        public int Count => 1;
        public DiagnosticEvent this[int index] { get { source.Cancel(); return new DiagnosticEvent(); } }
        public IEnumerator<DiagnosticEvent> GetEnumerator() { source.Cancel(); yield return new DiagnosticEvent(); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
