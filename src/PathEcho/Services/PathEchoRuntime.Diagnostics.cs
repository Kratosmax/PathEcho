using System.Reflection;
using PathEcho.Core.Backup;
using PathEcho.Core.Diagnostics;
using PathEcho.Core.Models;

namespace PathEcho.Services;

public sealed partial class PathEchoRuntime
{
    private async Task LoadHistoryAsync(CancellationToken cancellationToken, Guid? parentOperationId = null, bool periodic = false)
    {
        if (_previewMode)
        {
            await InvokeOnUiAsync(() =>
            {
                History.Clear();
                if (_previewSeed) foreach (var row in CreatePreviewHistory()) History.Add(row);
            });
            return;
        }
        var configuration = Configuration;
        var revision = Interlocked.Read(ref _configRevision);
        var operationId = Guid.NewGuid();
        var reports = new List<HistoryScanReport>();
        var rows = new List<HistoryRow>();
        if (!periodic) RecordDiagnostic(operationId, parentOperationId, null, "history.scan.start", "Started");
        foreach (var profile in configuration.GameProfiles)
        {
            var request = new HistoryScanRequest
            {
                ProfileId = profile.Id,
                BackupRoot = profile.ResolveBackupDirectory(configuration.DefaultBackupDirectory),
                SourceProfileIds = profile.LinkedBackupProfileIds.ToArray(),
                IncludeOtherProfiles = profile.IncludeOtherDeviceBackups && profile.HasDedicatedBackupDirectory(configuration),
                OperationId = Guid.NewGuid(),
                ParentOperationId = parentOperationId ??
                    (_profileOperations.TryGetValue(profile.Id, out var previousOperation) ? previousOperation : operationId),
                ConfigRevision = revision,
            };
            var report = await _snapshotStore.ScanAsync(request, cancellationToken).ConfigureAwait(false);
            reports.Add(report);
            rows.AddRange(report.Versions.Select(version => new HistoryRow(profile, version)));
            var summary = report.Summary;
            var counters = new Dictionary<string, long>
            {
                ["candidateDirectories"] = summary.CandidateDirectories,
                ["readyVersions"] = summary.ReadyVersions,
                ["awaitingManifest"] = summary.AwaitingManifest,
                ["manifestInvalid"] = summary.ManifestInvalid,
                ["identityMismatch"] = summary.IdentityMismatch,
                ["awaitingContent"] = summary.AwaitingContent,
                ["accessDenied"] = summary.AccessDenied,
                ["otherFailures"] = summary.OtherFailures,
                ["skippedByPolicy"] = summary.SkippedByPolicy,
            };
            var reason = report.Issues.FirstOrDefault()?.ReasonCode ?? report.Inspections
                .FirstOrDefault(item => item.ReasonCode != "READY")?.ReasonCode ?? "READY";
            _diagnosticJournal.Append(new DiagnosticEvent
            {
                OperationId = report.OperationId,
                ParentOperationId = report.ParentOperationId,
                ProfileId = profile.Id,
                ConfigRevision = report.ConfigRevision,
                AppVersion = ApplicationUpdateService.CurrentVersion,
                Stage = "history.scan.completed",
                Result = report.Outcome,
                ReasonCode = reason,
                DurationMs = report.DurationMs,
                Path = report.BackupRoot,
                Counters = counters,
            }, periodic, profile.Id.ToString("N"));
            if (!periodic)
            {
                foreach (var inspection in report.Inspections.Concat(report.Issues).Where(item => item.ReasonCode != "READY").Take(30))
                {
                    _diagnosticJournal.Append(new DiagnosticEvent
                    {
                        OperationId = report.OperationId,
                        ParentOperationId = report.ParentOperationId,
                        ProfileId = profile.Id,
                        ConfigRevision = report.ConfigRevision,
                        AppVersion = ApplicationUpdateService.CurrentVersion,
                        Stage = inspection.Stage,
                        Result = "Observed",
                        ReasonCode = inspection.ReasonCode,
                        Path = inspection.SnapshotDirectory,
                        ErrorType = inspection.ErrorType,
                        HResult = inspection.HResult,
                    });
                }
            }
        }
        await InvokeOnUiAsync(() =>
        {
            if (!ReferenceEquals(configuration, Configuration))
            {
                RecordDiagnostic(operationId, parentOperationId, null, "history.scan.discarded", "ConfigurationChanged", "CONFIG_CHANGED");
                return;
            }
            _lastHistoryOperationId = operationId;
            _historyReports = reports.ToArray();
            var ordered = rows.OrderByDescending(row => row.CreatedAtUtc).ToArray();
            if (!History.Select(row => (row.Profile, row.SnapshotDirectory, row.Version.IsContentAvailable))
                .SequenceEqual(ordered.Select(row => (row.Profile, row.SnapshotDirectory, row.Version.IsContentAvailable))))
            {
                History.Clear();
                foreach (var row in ordered) History.Add(row);
            }
            HistoryDiagnosticsChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    public async Task<IReadOnlyList<HistoryScanReport>> CheckHistoryAsync(CancellationToken cancellationToken = default)
    {
        var operation = BeginDiagnosticOperation("history.manual-check");
        try
        {
            await RefreshHistoryAsync(cancellationToken, forceDiscovery: true, parentOperationId: operation).ConfigureAwait(false);
            return HistoryReports;
        }
        catch (OperationCanceledException)
        {
            RecordDiagnostic(operation, null, null, "history.scan.completed", "Canceled", "CANCELED");
            throw;
        }
    }

    public async Task ExportHistoryDiagnosticsAsync(string destination, bool includeOriginalPaths = false, CancellationToken cancellationToken = default)
    {
        var snapshot = await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var configuration = Configuration;
            return new DiagnosticBundleSnapshot
            {
                AppVersion = ApplicationUpdateService.CurrentVersion,
                BuildCommit = typeof(PathEchoRuntime).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+').LastOrDefault() ?? "unknown",
                OsVersion = Environment.OSVersion.VersionString,
                RuntimeVersion = Environment.Version.ToString(),
                ConfigRevision = Interlocked.Read(ref _configRevision),
                Reports = HistoryReports.ToArray(),
                Events = _diagnosticJournal.Snapshot(),
                Profiles = configuration.GameProfiles.Select(profile => new DiagnosticProfileSnapshot
                {
                    ProfileId = profile.Id,
                    Name = profile.Name,
                    SaveDirectory = profile.SaveDirectory,
                    BackupDirectory = profile.ResolveBackupDirectory(configuration.DefaultBackupDirectory),
                    SourceProfileIds = profile.LinkedBackupProfileIds.ToArray(),
                    IncludeOtherProfiles = profile.IncludeOtherDeviceBackups && profile.HasDedicatedBackupDirectory(configuration),
                }).ToArray(),
            };
        });
        await DiagnosticBundleExporter.ExportAsync(destination, snapshot, includeOriginalPaths, cancellationToken).ConfigureAwait(false);
    }

    public void RecordHistoryPresentation(int visibleCount, bool hasFilters)
    {
        var state = $"{_lastHistoryOperationId:N}|{visibleCount}|{History.Count}|{hasFilters}";
        if (state == _lastPresentation) return;
        _lastPresentation = state;
        RecordDiagnostic(_lastHistoryOperationId, null, null, "ui.presented", "Completed",
            visibleCount < History.Count && hasFilters ? "RESULT_FILTERED_OUT" : null,
            counters: new Dictionary<string, long> { ["rawVersions"] = History.Count, ["visibleVersions"] = visibleCount });
    }

    public Guid BeginDiagnosticOperation(string stage, Guid? profileId = null)
    {
        var id = Guid.NewGuid();
        RecordDiagnostic(id, null, profileId, stage, "Started");
        return id;
    }

    public void RecordDiagnostic(Guid operationId, Guid? parentOperationId, Guid? profileId, string stage, string result,
        string? reason = null, string? path = null, IReadOnlyDictionary<string, long>? counters = null)
    {
        _diagnosticJournal.Append(new DiagnosticEvent
        {
            OperationId = operationId,
            ParentOperationId = parentOperationId,
            ProfileId = profileId,
            ConfigRevision = Interlocked.Read(ref _configRevision),
            AppVersion = ApplicationUpdateService.CurrentVersion,
            Stage = stage,
            Result = result,
            ReasonCode = reason,
            Path = path,
            Counters = counters ?? new Dictionary<string, long>(),
        });
    }
}
