using System.Diagnostics;
using System.Text.Json;
using PathEcho.Core.IO;
using PathEcho.Core.Sync;

namespace PathEcho.Core.Backup;

public sealed record BackupHistoryDiagnostic(string Code, string Path, string Detail);

public sealed class SnapshotStore : IBackupSnapshotStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private int _historyScanRunning;
    // Deterministic fault injection for the independent smoke suite; never set by production.
    internal Action<string, string>? HistoryScanProbe { get; init; }
    private readonly DirectoryScanner _scanner = new();
    private readonly BackupProfileLocator _profileLocator = new();
    private readonly HashSet<string> _verifiedObjects = new(StringComparer.OrdinalIgnoreCase);

    public async Task<SnapshotCreationResult> CreateAsync(
        Guid profileId,
        string sourceDirectory,
        string backupDirectory,
        string trigger,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? changedPaths = null)
    {
        var sourceRoot = SafePath.NormalizeDirectory(sourceDirectory, "存档目录不能为空。");
        var backupRoot = SafePath.NormalizeDirectory(backupDirectory, "备份目录不能为空。");
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException($"存档目录不存在：{sourceRoot}");
        }

        var profileRoot = Path.Combine(backupRoot, profileId.ToString("N"));
        ValidateSeparateRoots(sourceRoot, profileRoot);
        var objectsRoot = Path.Combine(profileRoot, "objects");
        var snapshotsRoot = Path.Combine(profileRoot, "snapshots");
        Directory.CreateDirectory(objectsRoot);
        Directory.CreateDirectory(snapshotsRoot);

        var entries = await BuildCurrentEntriesAsync(
            sourceRoot,
            snapshotsRoot,
            changedPaths,
            cancellationToken).ConfigureAwait(false);
        var createdAt = DateTimeOffset.UtcNow;
        var snapshotName = $"{createdAt:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}";
        var pendingRoot = Path.Combine(snapshotsRoot, $".{snapshotName}.pending");
        var finalRoot = Path.Combine(snapshotsRoot, snapshotName);
        Directory.CreateDirectory(pendingRoot);
        var newObjects = 0;
        var reusedObjects = 0;

        try
        {
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = SafePath.CombineUnderRoot(
                    sourceRoot,
                    entry.RelativePath,
                    "备份文件路径超出存档目录。");
                var objectPath = SnapshotContent.GetObjectPath(profileRoot, entry.Sha256);
                if (await EnsureObjectAsync(sourcePath, objectPath, entry.Sha256, cancellationToken).ConfigureAwait(false))
                {
                    newObjects++;
                }
                else
                {
                    reusedObjects++;
                }
            }

            var manifest = new SnapshotManifest
            {
                SchemaVersion = 2,
                ProfileId = profileId,
                CreatedAtUtc = createdAt,
                Trigger = trigger,
                Files = entries,
            };
            await WriteManifestAsync(Path.Combine(pendingRoot, "manifest.json"), manifest, cancellationToken)
                .ConfigureAwait(false);
            Directory.Move(pendingRoot, finalRoot);
        }
        catch
        {
            DirectoryTree.DeleteIfPresent(pendingRoot);
            throw;
        }

        await MigrateLegacyViewsAsync(profileRoot, cancellationToken).ConfigureAwait(false);
        return new SnapshotCreationResult(finalRoot, entries.Count, newObjects, reusedObjects, 0, 0);
    }

    public async Task<int> PruneAsync(
        Guid profileId,
        string backupDirectory,
        SnapshotRetentionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        policy.Validate();

        var profileRoot = Path.Combine(
            SafePath.NormalizeDirectory(backupDirectory, "备份目录不能为空。"),
            profileId.ToString("N"));
        var snapshotsRoot = Path.Combine(profileRoot, "snapshots");
        if (!Directory.Exists(snapshotsRoot))
        {
            return 0;
        }

        var snapshots = new List<SnapshotVersion>();
        foreach (var snapshotDirectory in EnumerateSnapshotDirectories(snapshotsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshots.Add(new SnapshotVersion(
                snapshotDirectory,
                await SnapshotContent.ReadManifestAsync(snapshotDirectory, cancellationToken).ConfigureAwait(false)));
        }

        var retained = SnapshotRetentionPlanner.Select(snapshots, policy);
        var removed = 0;
        foreach (var snapshot in snapshots.Where(snapshot => !retained.Contains(snapshot.SnapshotDirectory)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectoryTree.DeleteIfPresent(snapshot.SnapshotDirectory);
            removed++;
        }

        await GarbageCollectObjectsAsync(profileRoot, cancellationToken).ConfigureAwait(false);
        return removed;
    }

    public void InvalidateHistoryLocations() => _profileLocator.Invalidate();

    public async Task<IReadOnlyList<SnapshotVersion>> ListAsync(
        Guid profileId,
        string backupDirectory,
        CancellationToken cancellationToken = default,
        bool includeOtherProfiles = false,
        IReadOnlyCollection<Guid>? linkedProfileIds = null,
        Action<BackupHistoryDiagnostic>? diagnostic = null)
    {
        var backupRoot = SafePath.NormalizeDirectory(backupDirectory, "备份目录不能为空。");
        if (!Directory.Exists(backupRoot))
        {
            Report("RootUnavailable", backupRoot, "Directory is missing or cannot be accessed.");
            return Array.Empty<SnapshotVersion>();
        }

        var versions = new List<SnapshotVersion>();
        var requestedIds = (linkedProfileIds ?? Array.Empty<Guid>()).Append(profileId).ToHashSet();
        var profileRoots = await Task.Run(
            () => _profileLocator.Find(backupRoot, requestedIds, includeOtherProfiles, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        Report("ProfileDirectoriesLocated", backupRoot, $"count={profileRoots.Length}; requested={string.Join(",", requestedIds.Order().Select(id => id.ToString("N")))}; includeOthers={includeOtherProfiles}");
        foreach (var profileRoot in profileRoots)
        {
            var snapshotsRoot = Path.Combine(profileRoot, "snapshots");
            if (!BackupProfileLocator.IsRegularDirectory(snapshotsRoot))
            {
                Report("SnapshotRootUnavailable", snapshotsRoot, "Directory is unavailable or redirects through a link.");
                continue;
            }

            string[] snapshotDirectories;
            try
            {
                snapshotDirectories = EnumerateSnapshotDirectories(snapshotsRoot).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Report("SnapshotEnumerationFailed", snapshotsRoot, exception.GetType().Name + ": " + exception.Message);
                // Preserve already read profiles if a different cloud source disappears mid-refresh.
                continue;
            }
            Report("SnapshotDirectoriesLocated", snapshotsRoot, $"count={snapshotDirectories.Length}");
            foreach (var directory in snapshotDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!BackupProfileLocator.IsRegularDirectory(directory))
                {
                    Report("SnapshotDirectorySkipped", directory, "Directory is unavailable or redirects through a link.");
                    continue;
                }
                try
                {
                    var manifest = await SnapshotContent.ReadManifestAsync(directory, cancellationToken).ConfigureAwait(false);
                    if (manifest.Files is null || manifest.Files.Any(entry => entry is null || string.IsNullOrEmpty(entry.Sha256)) ||
                        (Guid.TryParse(Path.GetFileName(profileRoot), out var directoryId) && manifest.ProfileId != directoryId) ||
                        (!includeOtherProfiles && !requestedIds.Contains(manifest.ProfileId)) ||
                        (!requestedIds.Contains(manifest.ProfileId) &&
                            !string.Equals(Path.GetDirectoryName(profileRoot), backupRoot, StringComparison.OrdinalIgnoreCase)))
                    {
                        Report("ManifestRejected", directory, $"Invalid entries or mismatched profile identity; manifestProfile={manifest.ProfileId:N}");
                        continue;
                    }

                    var available = manifest.Files.All(entry => HasContent(directory, entry));
                    if (!available) Report("ContentPending", directory, "Referenced content is missing, inaccessible or differs in length; waiting for cloud synchronization.");
                    versions.Add(new SnapshotVersion(directory, manifest) { IsContentAvailable = available });
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
                {
                    Report("ManifestReadFailed", directory, exception.GetType().Name + ": " + exception.Message);
                    // Cloud sync can deliver the manifest last, partially, or before its objects.
                    // Retry this version on the next refresh without hiding unrelated versions.
                }
            }
        }

        Report("Completed", backupRoot, $"versions={versions.Count}; pendingContent={versions.Count(version => !version.IsContentAvailable)}");
        return versions.OrderByDescending(version => version.Manifest.CreatedAtUtc).ToArray();

        void Report(string code, string path, string detail)
        {
            try { diagnostic?.Invoke(new BackupHistoryDiagnostic(code, path, detail)); }
            catch { /* Diagnostic sinks must not change read results. */ }
        }
    }

    /// <summary>Read-only metadata diagnosis. Availability is not a hash verification.</summary>
    public async Task<HistoryScanReport> ScanAsync(
        HistoryScanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var state = new HistoryScanState(request);
        if (cancellationToken.IsCancellationRequested) return state.Report("Canceled", HistoryReasonCodes.CANCELED);
        if (request.Timeout <= TimeSpan.Zero) return state.Report("Timeout", HistoryReasonCodes.TIMEOUT);
        if (request.MaximumSnapshots < 1 || request.MaximumDirectories < 1 || request.MaximumManifestBytes < 1 || request.ProfileId == Guid.Empty)
            return state.Report("Failed", HistoryReasonCodes.SCAN_INCOMPLETE);
        if (Interlocked.CompareExchange(ref _historyScanRunning, 1, 0) != 0)
            return state.Report("Incomplete", HistoryReasonCodes.SCAN_INCOMPLETE);

        // The gate belongs to the actual I/O worker, not the waiting caller. A stuck OS call
        // retains the gate even after timeout, so repeated refreshes cannot stack workers.
        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeout = TimeSpan.FromMilliseconds(Math.Min(request.Timeout.TotalMilliseconds, 120000));
        var worker = Task.Run(async () =>
        {
            try { return await ScanHistoryWorkerAsync(request, state, stop.Token).ConfigureAwait(false); }
            finally { Interlocked.Exchange(ref _historyScanRunning, 0); stop.Dispose(); }
        });
        try { return await worker.WaitAsync(timeout, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            TryStop();
            return state.Report("Timeout", HistoryReasonCodes.TIMEOUT);
        }
        catch (OperationCanceledException)
        {
            TryStop();
            return state.Report("Canceled", HistoryReasonCodes.CANCELED);
        }
        void TryStop()
        {
            try { stop.Cancel(); }
            catch (ObjectDisposedException) { /* Worker completed concurrently. */ }
        }
    }

    private async Task<HistoryScanReport> ScanHistoryWorkerAsync(
        HistoryScanRequest request, HistoryScanState state, CancellationToken cancellationToken)
    {
        try
        {
            var root = SafePath.NormalizeDirectory(request.BackupRoot, "备份目录不能为空。");
            state.SetRoot(root);
            var ids = (request.SourceProfileIds ?? []).Append(request.ProfileId).Where(id => id != Guid.Empty).ToHashSet();
            var budget = new HistoryTraversalBudget(Math.Min(request.MaximumDirectories, 10000), cancellationToken);
            var profiles = BackupProfileLocator.FindForScan(root, ids, request.IncludeOtherProfiles, budget, state.AddIssue, HistoryScanProbe);
            foreach (var profile in profiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceId = Guid.TryParse(Path.GetFileName(profile), out var directoryId) ? directoryId : (Guid?)null;
                var snapshots = Path.Combine(profile, "snapshots");
                foreach (var directory in BackupProfileLocator.EnumerateForScan(snapshots, budget, state.AddIssue))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Path.GetFileName(directory).StartsWith(".", StringComparison.Ordinal)) continue;
                    if (state.CandidateCount >= Math.Min(request.MaximumSnapshots, 5000))
                        return state.Report("Incomplete", HistoryReasonCodes.SCAN_INCOMPLETE);
                    var index = state.BeginCandidate(directory, sourceId);
                    var inspection = new SnapshotInspection
                    {
                        SnapshotDirectory = directory,
                        SourceProfileId = sourceId,
                        Stage = "snapshot.enumerated",
                        ReasonCode = HistoryReasonCodes.SCAN_INCOMPLETE
                    };
                    SnapshotVersion? version = null;
                    try
                    {
                        var checks = new List<SnapshotInspection>();
                        if (!BackupProfileLocator.CheckScanDirectory(directory, "snapshot.enumerated", checks.Add, false))
                        {
                            inspection = inspection with
                            {
                                ReasonCode = checks.FirstOrDefault()?.ReasonCode ?? HistoryReasonCodes.IO_ERROR,
                                ErrorType = checks.FirstOrDefault()?.ErrorType,
                                HResult = checks.FirstOrDefault()?.HResult
                            };
                        }
                        else
                        {
                            inspection = inspection with { Stage = "manifest.checked" };
                            HistoryScanProbe?.Invoke("manifest.checked", directory);
                            var manifest = await ReadScanManifestAsync(directory, state, cancellationToken).ConfigureAwait(false);
                            if (!state.ReserveFileEntries(manifest.Files.Count))
                            {
                                state.FinishCandidate(index, inspection with { ReasonCode = HistoryReasonCodes.SCAN_INCOMPLETE }, null);
                                return state.Report("Incomplete", HistoryReasonCodes.SCAN_INCOMPLETE);
                            }
                            inspection = inspection with { SourceProfileId = manifest.ProfileId, Stage = "identity.checked" };
                            if (sourceId.HasValue && manifest.ProfileId != sourceId.Value)
                                inspection = inspection with { ReasonCode = HistoryReasonCodes.PROFILE_ID_MISMATCH };
                            else if ((!request.IncludeOtherProfiles && !ids.Contains(manifest.ProfileId)) ||
                                (!ids.Contains(manifest.ProfileId) && !string.Equals(profile, root, StringComparison.OrdinalIgnoreCase) &&
                                    !string.Equals(Path.GetDirectoryName(profile), root, StringComparison.OrdinalIgnoreCase)))
                                inspection = inspection with { ReasonCode = HistoryReasonCodes.PROFILE_UNASSOCIATED };
                            else
                            {
                                inspection = inspection with { Stage = "content.checked", ReasonCode = HistoryReasonCodes.READY };
                                version = new SnapshotVersion(directory, manifest) { IsContentAvailable = false };
                                foreach (var entry in manifest.Files)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    HistoryScanProbe?.Invoke("content.checked", directory);
                                    var reason = InspectScanContent(profile, directory, entry);
                                    if (reason is not null) { inspection = inspection with { ReasonCode = reason }; break; }
                                }
                                version = new SnapshotVersion(directory, manifest)
                                { IsContentAvailable = inspection.ReasonCode == HistoryReasonCodes.READY };
                            }
                        }
                    }
                    catch (HistoryScanLimitException)
                    {
                        state.FinishCandidate(index, inspection with { ReasonCode = HistoryReasonCodes.SCAN_INCOMPLETE }, null);
                        return state.Report("Incomplete", HistoryReasonCodes.SCAN_INCOMPLETE);
                    }
                    catch (HistoryLinkException)
                    {
                        inspection = inspection with { ReasonCode = HistoryReasonCodes.LINK_SKIPPED };
                    }
                    catch (JsonException exception)
                    {
                        inspection = WithError(inspection, HistoryReasonCodes.MANIFEST_INVALID_JSON, exception);
                    }
                    catch (InvalidDataException exception)
                    {
                        inspection = WithError(inspection, HistoryReasonCodes.MANIFEST_INVALID_SCHEMA, exception);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        var manifestStage = inspection.Stage == "manifest.checked";
                        var code = exception is UnauthorizedAccessException
                            ? (manifestStage ? HistoryReasonCodes.MANIFEST_ACCESS_DENIED : HistoryReasonCodes.OBJECT_ACCESS_DENIED)
                            : manifestStage && (exception is FileNotFoundException or DirectoryNotFoundException)
                                ? HistoryReasonCodes.MANIFEST_MISSING
                                : manifestStage && IsCloudUnavailable(exception.HResult)
                                    ? HistoryReasonCodes.MANIFEST_CLOUD_UNAVAILABLE : HistoryReasonCodes.IO_ERROR;
                        inspection = WithError(inspection, code, exception);
                        // Valid manifest metadata remains visible when object metadata is inaccessible.
                        if (version is not null) version = version with { IsContentAvailable = false };
                    }
                    state.FinishCandidate(index, inspection, version);
                }
                if (budget.LimitReached) break;
            }
            return state.Report(budget.LimitReached || state.HasTraversalFailure ? "Incomplete" : "Completed",
                budget.LimitReached ? HistoryReasonCodes.SCAN_INCOMPLETE : null);
        }
        catch (OperationCanceledException) { return state.Report("Canceled", HistoryReasonCodes.CANCELED); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            state.AddIssue(new SnapshotInspection
            {
                Stage = "root.checked",
                ReasonCode = exception is UnauthorizedAccessException
                ? HistoryReasonCodes.ROOT_ACCESS_DENIED : HistoryReasonCodes.IO_ERROR,
                ErrorType = exception.GetType().Name,
                HResult = exception.HResult
            });
            return state.Report("Failed");
        }
    }

    private static SnapshotInspection WithError(SnapshotInspection inspection, string reason, Exception exception) =>
        inspection with { ReasonCode = reason, ErrorType = exception.GetType().Name, HResult = exception.HResult };

    // Only documented Cloud Files errors are classified as cloud unavailability. Generic
    // sharing violations, missing paths and I/O errors retain their observed classifications.
    private static bool IsCloudUnavailable(int hresult) =>
        (unchecked((uint)hresult) & 0xffff0000u) == 0x80070000u && (hresult & 0xffff) is 362 or 388 or 389 or 426 or 4350;

    private static async Task<SnapshotManifest> ReadScanManifestAsync(string directory, HistoryScanState state, CancellationToken token)
    {
        const int maximumManifestBytes = 8 * 1024 * 1024;
        var path = Path.Combine(directory, "manifest.json");
        EnsureScanPathDoesNotRedirect(directory, path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
        if (stream.Length > maximumManifestBytes) throw new InvalidDataException("Manifest exceeds metadata limit.");
        // Read a bounded buffer even if a producer grows the manifest after the length check.
        using var buffer = new MemoryStream();
        var bytes = new byte[16384];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var count = await stream.ReadAsync(bytes, token).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > maximumManifestBytes) throw new InvalidDataException("Manifest exceeds metadata limit.");
            if (!state.ReserveManifestBytes(count)) throw new HistoryScanLimitException();
            buffer.Write(bytes, 0, count);
        }
        using var json = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
        var value = json.RootElement;
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("SchemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var schemaNumber) || schemaNumber is < 1 or > 2)
            throw new InvalidDataException("Unsupported manifest schema.");
        SnapshotManifest manifest;
        try { manifest = value.Deserialize<SnapshotManifest>() ?? throw new InvalidDataException("Empty manifest."); }
        catch (JsonException exception) { throw new InvalidDataException("Invalid manifest fields.", exception); }
        if (manifest.ProfileId == Guid.Empty || manifest.CreatedAtUtc == default || manifest.Trigger is null || manifest.Trigger.Length > 4096 ||
            manifest.Files is null || manifest.Files.Count > 100000)
            throw new InvalidDataException("Invalid manifest metadata.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            if (entry is null || string.IsNullOrWhiteSpace(entry.RelativePath) || entry.RelativePath.Length > 32767 ||
                string.IsNullOrEmpty(entry.Sha256) || entry.Sha256.Length != 64 || entry.Sha256.Any(c => !Uri.IsHexDigit(c)) ||
                entry.Length < 0 || entry.LastWriteUtcTicks < DateTime.MinValue.Ticks || entry.LastWriteUtcTicks > DateTime.MaxValue.Ticks)
                throw new InvalidDataException("Invalid manifest file metadata.");
            var normalized = entry.RelativePath.Replace('\\', '/');
            var segments = normalized.Split('/');
            if (Path.IsPathRooted(entry.RelativePath) || normalized.Contains(':') ||
                segments.Any(segment => segment.Length > 255 || segment is "" or "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.') ||
                    segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsReservedScanFileName(segment)) ||
                !paths.Add(normalized)) throw new InvalidDataException("Invalid manifest relative path.");
            _ = SafePath.CombineUnderRoot(Path.Combine(directory, "files"), entry.RelativePath, "Invalid manifest path.");
        }
        return manifest;
    }

    private static bool IsReservedScanFileName(string segment)
    {
        var stem = segment.Split('.')[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && stem[3] is >= '1' and <= '9' &&
                (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)));
    }

    private static string? InspectScanContent(string profile, string directory, SnapshotFileEntry entry)
    {
        try
        {
            var objectPath = SnapshotContent.GetObjectPath(profile, entry.Sha256);
            var path = objectPath;
            try
            {
                EnsureScanPathDoesNotRedirect(profile, objectPath);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                path = SafePath.CombineUnderRoot(Path.Combine(directory, "files"), entry.RelativePath, "Invalid legacy path.");
                EnsureScanPathDoesNotRedirect(directory, path);
            }
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0) return HistoryReasonCodes.OBJECT_MISSING;
            return new FileInfo(path).Length == entry.Length ? null : HistoryReasonCodes.OBJECT_LENGTH_MISMATCH;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return HistoryReasonCodes.OBJECT_MISSING;
        }
    }

    private static void EnsureScanPathDoesNotRedirect(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Metadata path escapes its root.");
        var current = root;
        var segments = relative.Split(Path.DirectorySeparatorChar);
        for (var index = 0; index <= segments.Length; index++)
        {
            var attributes = File.GetAttributes(current);
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null) throw new HistoryLinkException();
            if (index < segments.Length) current = Path.Combine(current, segments[index]);
        }
    }

    private sealed class HistoryLinkException : IOException { }
    private sealed class HistoryScanLimitException : IOException { }

    private sealed class HistoryScanState(HistoryScanRequest request)
    {
        private readonly object _gate = new();
        private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<SnapshotInspection> _inspections = [];
        private readonly List<SnapshotInspection> _issues = [];
        private readonly List<SnapshotVersion> _versions = [];
        private string _root = request.BackupRoot;
        private int _fileEntries;
        private int _manifestBytes;
        internal bool ReserveManifestBytes(int count)
        {
            lock (_gate)
            {
                var limit = Math.Min(request.MaximumManifestBytes, 32 * 1024 * 1024);
                if (count > limit - _manifestBytes) return false;
                _manifestBytes += count;
                return true;
            }
        }
        internal bool ReserveFileEntries(int count)
        {
            lock (_gate)
            {
                if (count > 100000 - _fileEntries) return false;
                _fileEntries += count;
                return true;
            }
        }
        internal int CandidateCount { get { lock (_gate) return _inspections.Count; } }
        internal bool HasTraversalFailure
        {
            get
            {
                lock (_gate) return _issues.Any(issue =>
            issue.ReasonCode is HistoryReasonCodes.ROOT_NOT_FOUND or HistoryReasonCodes.ROOT_ACCESS_DENIED or
                HistoryReasonCodes.IO_ERROR or HistoryReasonCodes.OBJECT_ACCESS_DENIED or HistoryReasonCodes.LINK_SKIPPED);
            }
        }
        internal void SetRoot(string root) { lock (_gate) _root = root; }
        internal void AddIssue(SnapshotInspection issue) { lock (_gate) { if (_issues.Count < 10000) _issues.Add(issue); } }
        internal int BeginCandidate(string directory, Guid? id)
        {
            lock (_gate)
            {
                _inspections.Add(new SnapshotInspection
                {
                    SnapshotDirectory = directory,
                    SourceProfileId = id,
                    Stage = "snapshot.enumerated",
                    ReasonCode = HistoryReasonCodes.SCAN_INCOMPLETE
                });
                return _inspections.Count - 1;
            }
        }
        internal void FinishCandidate(int index, SnapshotInspection inspection, SnapshotVersion? version)
        {
            lock (_gate) { _inspections[index] = inspection; if (version is not null) _versions.Add(version); }
        }
        internal HistoryScanReport Report(string outcome, string? reason = null)
        {
            lock (_gate)
            {
                var inspections = _inspections.ToArray();
                var issues = _issues.ToList();
                if (reason is not null) issues.Add(new SnapshotInspection
                {
                    SnapshotDirectory = _root,
                    Stage = "history.scan.completed",
                    ReasonCode = reason
                });
                var groups = inspections.GroupBy(item => item.ReasonCode).ToDictionary(group => group.Key, group => group.Count());
                int Count(params string[] codes) => codes.Sum(code => groups.GetValueOrDefault(code));
                var ready = Count(HistoryReasonCodes.READY);
                var awaitingManifest = Count(HistoryReasonCodes.MANIFEST_MISSING, HistoryReasonCodes.MANIFEST_CLOUD_UNAVAILABLE);
                var invalid = Count(HistoryReasonCodes.MANIFEST_INVALID_JSON, HistoryReasonCodes.MANIFEST_INVALID_SCHEMA);
                var identity = Count(HistoryReasonCodes.PROFILE_ID_MISMATCH, HistoryReasonCodes.PROFILE_UNASSOCIATED);
                var content = Count(HistoryReasonCodes.OBJECT_MISSING, HistoryReasonCodes.OBJECT_LENGTH_MISMATCH);
                var access = Count(HistoryReasonCodes.MANIFEST_ACCESS_DENIED, HistoryReasonCodes.OBJECT_ACCESS_DENIED);
                var skipped = Count(HistoryReasonCodes.LINK_SKIPPED);
                return new HistoryScanReport
                {
                    OperationId = request.OperationId,
                    ParentOperationId = request.ParentOperationId,
                    ProfileId = request.ProfileId,
                    ConfigRevision = request.ConfigRevision,
                    BackupRoot = _root,
                    StartedAtUtc = _startedAt,
                    DurationMs = _clock.ElapsedMilliseconds,
                    Outcome = outcome,
                    Versions = _versions.OrderByDescending(version => version.Manifest.CreatedAtUtc).ToArray(),
                    Inspections = inspections,
                    Issues = issues.ToArray(),
                    Summary = new HistoryScanSummary
                    {
                        CandidateDirectories = inspections.Length,
                        ReadyVersions = ready,
                        AwaitingManifest = awaitingManifest,
                        ManifestInvalid = invalid,
                        IdentityMismatch = identity,
                        AwaitingContent = content,
                        AccessDenied = access,
                        SkippedByPolicy = skipped,
                        OtherFailures = inspections.Length - ready - awaitingManifest - invalid - identity - content - access - skipped
                    },
                };
            }
        }
    }

    private static bool HasContent(string directory, SnapshotFileEntry entry)
    {
        try
        {
            return new FileInfo(SnapshotContent.ResolveFile(directory, entry)).Length == entry.Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<SnapshotFileEntry>> BuildCurrentEntriesAsync(
        string sourceRoot,
        string snapshotsRoot,
        IReadOnlyCollection<string>? changedPaths,
        CancellationToken cancellationToken)
    {
        if (changedPaths is null || changedPaths.Count == 0)
        {
            return await ScanAllEntriesAsync(sourceRoot, cancellationToken).ConfigureAwait(false);
        }

        var latestDirectory = EnumerateSnapshotDirectories(snapshotsRoot).FirstOrDefault();
        if (latestDirectory is null)
        {
            return await ScanAllEntriesAsync(sourceRoot, cancellationToken).ConfigureAwait(false);
        }

        var latest = await SnapshotContent.ReadManifestAsync(latestDirectory, cancellationToken).ConfigureAwait(false);
        var entries = latest.Files.ToDictionary(
            entry => entry.RelativePath,
            StringComparer.OrdinalIgnoreCase);
        foreach (var rawPath in changedPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = rawPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            if (relativePath == "." || Path.IsPathRooted(relativePath))
            {
                return await ScanAllEntriesAsync(sourceRoot, cancellationToken).ConfigureAwait(false);
            }

            var fullPath = SafePath.CombineUnderRoot(sourceRoot, relativePath, "变化文件路径超出存档目录。");
            RemovePathAndChildren(entries, relativePath);
            if (File.Exists(fullPath))
            {
                entries[relativePath] = await CreateEntryAsync(fullPath, relativePath, cancellationToken).ConfigureAwait(false);
            }
            else if (Directory.Exists(fullPath))
            {
                var subtree = await _scanner.ScanAsync(
                    fullPath,
                    cancellationToken,
                    forceHash: true).ConfigureAwait(false);
                foreach (var pair in subtree)
                {
                    var childRelative = Path.Combine(relativePath, pair.Key);
                    entries[childRelative] = ToEntry(childRelative, pair.Value);
                }
            }
        }

        return entries.Values.OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<IReadOnlyList<SnapshotFileEntry>> ScanAllEntriesAsync(
        string sourceRoot,
        CancellationToken cancellationToken)
    {
        var files = await _scanner.ScanAsync(
            sourceRoot,
            cancellationToken,
            forceHash: true).ConfigureAwait(false);
        return files
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => ToEntry(pair.Key, pair.Value))
            .ToArray();
    }

    private static async Task<SnapshotFileEntry> CreateEntryAsync(
        string path,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var stamp = await FileStamp.CreateAsync(path, cancellationToken).ConfigureAwait(false);
        return ToEntry(relativePath, stamp);
    }

    private static SnapshotFileEntry ToEntry(string relativePath, FileStamp stamp) => new()
    {
        RelativePath = relativePath,
        Sha256 = stamp.Sha256,
        Length = stamp.Length,
        LastWriteUtcTicks = stamp.LastWriteUtcTicks,
    };

    private static void RemovePathAndChildren(
        IDictionary<string, SnapshotFileEntry> entries,
        string relativePath)
    {
        var prefix = Path.TrimEndingDirectorySeparator(relativePath) + Path.DirectorySeparatorChar;
        foreach (var key in entries.Keys
                     .Where(key => string.Equals(key, relativePath, StringComparison.OrdinalIgnoreCase) ||
                         key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            entries.Remove(key);
        }
    }

    private async Task<bool> EnsureObjectAsync(
        string sourcePath,
        string objectPath,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        if (File.Exists(objectPath))
        {
            if (_verifiedObjects.Contains(objectPath) ||
                string.Equals(
                    await ContentHash.ComputeAsync(objectPath, cancellationToken).ConfigureAwait(false),
                    expectedHash,
                    StringComparison.Ordinal))
            {
                _verifiedObjects.Add(objectPath);
                return false;
            }

            File.SetAttributes(objectPath, FileAttributes.Normal);
            File.Delete(objectPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);
        await AtomicFileOperations.CopyAsync(sourcePath, objectPath, cancellationToken).ConfigureAwait(false);
        var storedHash = await ContentHash.ComputeAsync(objectPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(storedHash, expectedHash, StringComparison.Ordinal))
        {
            File.Delete(objectPath);
            throw new IOException($"备份对象校验失败：{Path.GetFileName(sourcePath)}");
        }

        _verifiedObjects.Add(objectPath);
        return true;
    }

    private async Task MigrateLegacyViewsAsync(string profileRoot, CancellationToken cancellationToken)
    {
        var snapshotsRoot = Path.Combine(profileRoot, "snapshots");
        foreach (var snapshotDirectory in EnumerateSnapshotDirectories(snapshotsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filesRoot = Path.Combine(snapshotDirectory, "files");
            if (!Directory.Exists(filesRoot))
            {
                continue;
            }

            try
            {
                var manifest = await SnapshotContent.ReadManifestAsync(snapshotDirectory, cancellationToken)
                    .ConfigureAwait(false);
                var valid = true;
                foreach (var entry in manifest.Files)
                {
                    var objectPath = SnapshotContent.GetObjectPath(profileRoot, entry.Sha256);
                    if (!File.Exists(objectPath) ||
                        (!_verifiedObjects.Contains(objectPath) &&
                         !string.Equals(
                             await ContentHash.ComputeAsync(objectPath, cancellationToken).ConfigureAwait(false),
                             entry.Sha256,
                             StringComparison.Ordinal)))
                    {
                        valid = false;
                        break;
                    }

                    _verifiedObjects.Add(objectPath);
                }

                if (!valid)
                {
                    continue;
                }

                DirectoryTree.DeleteIfPresent(filesRoot);
                if (manifest.SchemaVersion < 2)
                {
                    await ReplaceManifestAsync(
                        Path.Combine(snapshotDirectory, "manifest.json"),
                        manifest with { SchemaVersion = 2 },
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                // Leave a legacy view intact unless every referenced object was verified.
            }
        }
    }

    private static IEnumerable<string> EnumerateSnapshotDirectories(string snapshotsRoot) =>
        Directory.EnumerateDirectories(snapshotsRoot)
            .Where(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal))
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal);

    private static async Task GarbageCollectObjectsAsync(string profileRoot, CancellationToken cancellationToken)
    {
        var snapshotsRoot = Path.Combine(profileRoot, "snapshots");
        var usedHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var snapshotDirectory in EnumerateSnapshotDirectories(snapshotsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = await SnapshotContent.ReadManifestAsync(snapshotDirectory, cancellationToken).ConfigureAwait(false);
            usedHashes.UnionWith(manifest.Files.Select(file => file.Sha256));
        }

        var objectsRoot = Path.Combine(profileRoot, "objects");
        if (!Directory.Exists(objectsRoot))
        {
            return;
        }

        foreach (var objectPath in Directory.EnumerateFiles(objectsRoot, "*.blob", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!usedHashes.Contains(Path.GetFileNameWithoutExtension(objectPath)))
            {
                File.SetAttributes(objectPath, FileAttributes.Normal);
                File.Delete(objectPath);
            }
        }
    }

    private static async Task WriteManifestAsync(
        string path,
        SnapshotManifest manifest,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
        await JsonSerializer.SerializeAsync(stream, manifest, SerializerOptions, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReplaceManifestAsync(
        string path,
        SnapshotManifest manifest,
        CancellationToken cancellationToken)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await WriteManifestAsync(temporary, manifest, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void ValidateSeparateRoots(string sourceRoot, string backupRoot)
    {
        if (SafePath.IsSameOrNested(sourceRoot, backupRoot) ||
            SafePath.IsSameOrNested(backupRoot, sourceRoot))
        {
            throw new InvalidOperationException("存档目录和备份目录不能相同或互相包含。");
        }
    }
}
