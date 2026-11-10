using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace PathEcho.Core.Diagnostics;

public static class DiagnosticBundleExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task ExportAsync(string destination, DiagnosticBundleSnapshot snapshot,
        bool includeOriginalPaths = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(destination);
        var parent = Path.GetDirectoryName(path) ?? throw new InvalidDataException("诊断包目标缺少目录。");
        Directory.CreateDirectory(parent);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var aliases = new Aliases(snapshot, includeOriginalPaths);
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                var reports = snapshot.Reports.Take(100).ToArray();
                await WriteAsync(archive, "summary.json", new
                {
                    schemaVersion = 1,
                    exportedAtUtc = DateTimeOffset.UtcNow,
                    includeOriginalPaths,
                    snapshot.ConfigRevision,
                    reportCount = reports.Length,
                    reports = reports.Select(report => new
                    {
                        operationId = aliases.Id(report.OperationId),
                        profileId = aliases.Id(report.ProfileId),
                        backupRoot = aliases.Path(report.BackupRoot),
                        report.Outcome,
                        report.IsComplete,
                        report.StartedAtUtc,
                        report.DurationMs,
                        staleConfiguration = report.ConfigRevision != snapshot.ConfigRevision,
                        summary = report.Summary,
                        issues = report.Issues.Take(50).Select(item => new { item.Stage, item.ReasonCode, path = aliases.Path(item.SnapshotDirectory), item.ErrorType, item.HResult }),
                    }),
                    limitations = "Metadata diagnostics; no save contents, content hashes or automatic uploads. Incomplete scans do not prove data absence.",
                }, cancellationToken);
                await WriteAsync(archive, "effective-settings.json", new
                {
                    schemaVersion = 1,
                    snapshot.ConfigRevision,
                    profiles = snapshot.Profiles.Take(100).Select(profile => new
                    {
                        profileId = aliases.Id(profile.ProfileId),
                        name = includeOriginalPaths ? profile.Name : aliases.Name(profile.ProfileId),
                        saveDirectory = aliases.Path(profile.SaveDirectory),
                        backupDirectory = aliases.Path(profile.BackupDirectory),
                        sourceProfileIds = profile.SourceProfileIds.Select(aliases.Id).ToArray(),
                        profile.IncludeOtherProfiles,
                    }),
                }, cancellationToken);
                await WriteAsync(archive, "backup-layout.json", new
                {
                    schemaVersion = 1,
                    reports = reports.Select(report => new
                    {
                        operationId = aliases.Id(report.OperationId),
                        report.Outcome,
                        candidateCount = report.Inspections.Count,
                        truncated = report.Inspections.Count > 1000,
                        inspections = report.Inspections.Take(1000).Select(item => new
                        {
                            path = aliases.Path(item.SnapshotDirectory),
                            sourceProfileId = item.SourceProfileId is { } id ? aliases.Id(id) : null,
                            item.Stage,
                            item.ReasonCode,
                            item.ErrorType,
                            item.HResult,
                        }),
                    }),
                }, cancellationToken);
                await WriteAsync(archive, "environment.json", new
                {
                    schemaVersion = 1,
                    snapshot.AppVersion,
                    snapshot.BuildCommit,
                    snapshot.OsVersion,
                    snapshot.RuntimeVersion,
                    pathMode = includeOriginalPaths ? "Original paths included by user choice" : "Paths and identifiers replaced with bundle-local aliases",
                }, cancellationToken);
                var entry = archive.CreateEntry("operation-events.jsonl", CompressionLevel.Optimal);
                await using var stream = entry.Open();
                foreach (var item in snapshot.Events.TakeLast(200))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var safe = new
                    {
                        item.SchemaVersion,
                        item.TimestampUtc,
                        operationId = aliases.Id(item.OperationId),
                        parentOperationId = item.ParentOperationId is { } parentId ? aliases.Id(parentId) : null,
                        profileId = item.ProfileId is { } profileId ? aliases.Id(profileId) : null,
                        item.ConfigRevision,
                        item.AppVersion,
                        item.Stage,
                        item.Result,
                        item.ReasonCode,
                        item.DurationMs,
                        counters = item.Counters.Take(32).ToDictionary(pair => pair.Key, pair => pair.Value),
                        path = item.Path is null ? null : aliases.Path(item.Path),
                        item.ErrorType,
                        item.HResult,
                    };
                    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(safe) + "\n");
                    await stream.WriteAsync(bytes, cancellationToken);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task WriteAsync(ZipArchive archive, string name, object value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, token);
    }

    private sealed class Aliases
    {
        private readonly bool _original;
        private readonly Dictionary<Guid, string> _ids = new();
        private readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _roots = new(StringComparer.OrdinalIgnoreCase);

        internal Aliases(DiagnosticBundleSnapshot snapshot, bool original)
        {
            _original = original;
            foreach (var profile in snapshot.Profiles.Take(100))
            {
                Root(profile.BackupDirectory, "backupRoot");
                Root(profile.SaveDirectory, "saveRoot");
            }
            foreach (var report in snapshot.Reports.Take(100)) Root(report.BackupRoot, "backupRoot");
        }

        internal string Id(Guid value)
        {
            if (_original) return value.ToString("N");
            if (!_ids.TryGetValue(value, out var alias)) _ids[value] = alias = "id-" + (_ids.Count + 1);
            return alias;
        }

        internal string Name(Guid value) => "游戏 " + Id(value);

        internal string Path(string value)
        {
            if (_original || string.IsNullOrWhiteSpace(value)) return value;
            if (_paths.TryGetValue(value, out var cached)) return cached;
            var root = _roots.Keys.OrderByDescending(item => item.Length).FirstOrDefault(item =>
                value.Equals(item, StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith(item + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith(item + "/", StringComparison.OrdinalIgnoreCase));
            var alias = root is null ? "[path-" + (_paths.Count + 1) + "]" :
                value.Equals(root, StringComparison.OrdinalIgnoreCase) ? _roots[root] : _roots[root] + "/[entry-" + (_paths.Count + 1) + "]";
            _paths[value] = alias;
            return alias;
        }

        private void Root(string value, string kind)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            value = value.TrimEnd('\\', '/');
            if (!_roots.ContainsKey(value)) _roots[value] = "[" + kind + "-" + (_roots.Count + 1) + "]";
        }
    }
}
