using System.Text.Json;
using PathEcho.Core.Diagnostics;

internal static class DiagnosticJournalTests
{
    internal static Task RunAsync(string testRoot)
    {
        var directory = Path.Combine(testRoot, "diagnostic-journal");
        var journal = new LocalDiagnosticJournal(directory, maximumBytes: 1024, retainedFiles: 2, memoryCapacity: 10);
        DiagnosticEvent Item(string result = "Completed") => new()
        {
            OperationId = Guid.NewGuid(),
            ProfileId = Guid.NewGuid(),
            Stage = "history.scan.completed",
            Result = result,
            ReasonCode = "MANIFEST_MISSING",
            Counters = new Dictionary<string, long> { ["awaitingManifest"] = 1 },
        };
        journal.Append(Item(), periodic: true, stateKey: "profile");
        journal.Append(Item(), periodic: true, stateKey: "profile");
        Assert(journal.Snapshot().Count == 1, "Identical periodic state created duplicate logs.");
        journal.Append(Item("Canceled"), periodic: true, stateKey: "profile");
        Assert(journal.Snapshot().Count == 2, "Changed periodic state was hidden.");
        for (var index = 0; index < 30; index++) journal.Append(Item());
        Assert(journal.Snapshot().Count == 10, "Diagnostic memory buffer is not bounded.");
        var files = Directory.GetFiles(directory);
        Assert(files.Length <= 3 && files.All(path => new FileInfo(path).Length <= 1024), "Journal rotation or byte bound failed.");
        Assert(files.Any(path => path.EndsWith(".1", StringComparison.Ordinal)), "Journal did not rotate.");
        foreach (var path in files)
        {
            foreach (var line in File.ReadLines(path)) using (JsonDocument.Parse(line)) { }
        }
        Assert(new LocalDiagnosticJournal(directory, maximumBytes: 1024, memoryCapacity: 10).Snapshot().Count > 0,
            "Restarting diagnostics lost the bounded current-file evidence.");
        var blocked = Path.Combine(testRoot, "diagnostic-blocked");
        File.WriteAllText(blocked, "fixture");
        var failing = new LocalDiagnosticJournal(blocked);
        failing.Append(Item());
        Assert(failing.Snapshot().Count == 1, "Disk failure discarded memory evidence or escaped.");
        return Task.CompletedTask;
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
