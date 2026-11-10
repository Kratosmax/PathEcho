using PathEcho.Core.Backup;

namespace PathEcho.Core.Diagnostics;

public sealed record DiagnosticEvent
{
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public Guid OperationId { get; init; }
    public Guid? ParentOperationId { get; init; }
    public Guid? ProfileId { get; init; }
    public long ConfigRevision { get; init; }
    public string AppVersion { get; init; } = "";
    public string Stage { get; init; } = "";
    public string Result { get; init; } = "";
    public string? ReasonCode { get; init; }
    public long? DurationMs { get; init; }
    public IReadOnlyDictionary<string, long> Counters { get; init; } = new Dictionary<string, long>();
    public string? Path { get; init; }
    public string? ErrorType { get; init; }
    public int? HResult { get; init; }
}

public sealed record DiagnosticProfileSnapshot
{
    public Guid ProfileId { get; init; }
    public string Name { get; init; } = "";
    public string SaveDirectory { get; init; } = "";
    public string BackupDirectory { get; init; } = "";
    public IReadOnlyList<Guid> SourceProfileIds { get; init; } = [];
    public bool IncludeOtherProfiles { get; init; }
}

public sealed record DiagnosticBundleSnapshot
{
    public string AppVersion { get; init; } = "";
    public string BuildCommit { get; init; } = "";
    public string OsVersion { get; init; } = "";
    public string RuntimeVersion { get; init; } = "";
    public long ConfigRevision { get; init; }
    public IReadOnlyList<DiagnosticProfileSnapshot> Profiles { get; init; } = [];
    public IReadOnlyList<HistoryScanReport> Reports { get; init; } = [];
    public IReadOnlyList<DiagnosticEvent> Events { get; init; } = [];
}
