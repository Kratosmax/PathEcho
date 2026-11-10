namespace PathEcho.Core.Backup;

public static class HistoryReasonCodes
{
    public const string READY = "READY";
    public const string ROOT_NOT_FOUND = "ROOT_NOT_FOUND";
    public const string ROOT_ACCESS_DENIED = "ROOT_ACCESS_DENIED";
    public const string SOURCE_NOT_FOUND = "SOURCE_NOT_FOUND";
    public const string SOURCE_UNASSOCIATED = "SOURCE_UNASSOCIATED";
    public const string MANIFEST_MISSING = "MANIFEST_MISSING";
    public const string MANIFEST_ACCESS_DENIED = "MANIFEST_ACCESS_DENIED";
    public const string MANIFEST_CLOUD_UNAVAILABLE = "MANIFEST_CLOUD_UNAVAILABLE";
    public const string MANIFEST_INVALID_JSON = "MANIFEST_INVALID_JSON";
    public const string MANIFEST_INVALID_SCHEMA = "MANIFEST_INVALID_SCHEMA";
    public const string PROFILE_ID_MISMATCH = "PROFILE_ID_MISMATCH";
    public const string PROFILE_UNASSOCIATED = "PROFILE_UNASSOCIATED";
    public const string OBJECT_MISSING = "OBJECT_MISSING";
    public const string OBJECT_LENGTH_MISMATCH = "OBJECT_LENGTH_MISMATCH";
    public const string OBJECT_ACCESS_DENIED = "OBJECT_ACCESS_DENIED";
    public const string IO_ERROR = "IO_ERROR";
    public const string LINK_SKIPPED = "LINK_SKIPPED";
    public const string CANCELED = "CANCELED";
    public const string TIMEOUT = "TIMEOUT";
    public const string SCAN_INCOMPLETE = "SCAN_INCOMPLETE";
}

public sealed record HistoryScanRequest
{
    public required Guid ProfileId { get; init; }
    public required string BackupRoot { get; init; }
    public IReadOnlyList<Guid> SourceProfileIds { get; init; } = [];
    public bool IncludeOtherProfiles { get; init; }
    public Guid OperationId { get; init; } = Guid.NewGuid();
    public Guid? ParentOperationId { get; init; }
    public long ConfigRevision { get; init; }
    public int MaximumSnapshots { get; init; } = 5000;
    public int MaximumDirectories { get; init; } = 10000;
    public int MaximumManifestBytes { get; init; } = 32 * 1024 * 1024;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed record SnapshotInspection
{
    public string SnapshotDirectory { get; init; } = "";
    public Guid? SourceProfileId { get; init; }
    public string Stage { get; init; } = "";
    public string ReasonCode { get; init; } = "";
    public string? ErrorType { get; init; }
    public int? HResult { get; init; }
}

public sealed record HistoryScanSummary
{
    public int CandidateDirectories { get; init; }
    public int ReadyVersions { get; init; }
    public int AwaitingManifest { get; init; }
    public int ManifestInvalid { get; init; }
    public int IdentityMismatch { get; init; }
    public int AwaitingContent { get; init; }
    public int AccessDenied { get; init; }
    public int OtherFailures { get; init; }
    public int SkippedByPolicy { get; init; }
}

public sealed record HistoryScanReport
{
    public Guid OperationId { get; init; }
    public Guid? ParentOperationId { get; init; }
    public Guid ProfileId { get; init; }
    public long ConfigRevision { get; init; }
    public string BackupRoot { get; init; } = "";
    public DateTimeOffset StartedAtUtc { get; init; }
    public long DurationMs { get; init; }
    public string Outcome { get; init; } = "Incomplete";
    public bool IsComplete => Outcome == "Completed";
    public IReadOnlyList<SnapshotVersion> Versions { get; init; } = [];
    public IReadOnlyList<SnapshotInspection> Inspections { get; init; } = [];
    public IReadOnlyList<SnapshotInspection> Issues { get; init; } = [];
    public HistoryScanSummary Summary { get; init; } = new();
}
