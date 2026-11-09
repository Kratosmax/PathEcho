using PathEcho.Core.Backup;
using PathEcho.Core.GameCatalog;
using PathEcho.Core.Models;
using PathEcho.Core.Storage;

namespace PathEcho.Services;

public sealed class GameProfileRow : NotifyObject
{
    private string _status = "等待启动";

    public GameProfileRow(GameBackupProfile definition) => Definition = definition;

    public GameBackupProfile Definition { get; }
    public string Name => Definition.Name;
    public string SaveDirectory => Definition.SaveDirectory;
    public int RetainedVersions => Definition.RetainedVersions;
    public string RetentionSummary => $"{Definition.RetainedVersions} + {Definition.RetainedHourlyVersions}时 + {Definition.RetainedDailyVersions}日";
    public string Triggers => string.Join("、", new[]
    {
        Definition.Triggers.HasFlag(BackupTrigger.Scheduled) ? "定时" : null,
        Definition.Triggers.HasFlag(BackupTrigger.ImportantFileChanged) ? "重点文件" : null,
        Definition.Triggers.HasFlag(BackupTrigger.ChangedFiles) ? "文件变化" : null,
        Definition.Triggers.HasFlag(BackupTrigger.ProcessExited) ? "游戏退出" : null,
    }.Where(value => value is not null));

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }
}

public sealed class HistoryRow
{
    public HistoryRow(GameBackupProfile profile, SnapshotVersion version)
    {
        Profile = profile;
        Version = version;
    }

    public GameBackupProfile Profile { get; }
    public SnapshotVersion Version { get; }
    public string GameName => Profile.Name;
    public DateTimeOffset CreatedAtUtc => Version.Manifest.CreatedAtUtc;
    public string CreatedAt => CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string Trigger => Version.Manifest.Trigger;
    public bool IsContentAvailable => Version.IsContentAvailable;
    public string Availability => !IsContentAvailable ? "等待云同步" :
        Version.Manifest.ProfileId != Profile.Id ? "关联备份" : "当前档案";
    public int FileCount => Version.Manifest.Files.Count;
    public string SnapshotDirectory => Version.SnapshotDirectory;
}

public sealed class SyncRunRow
{
    public SyncRunRow(SyncRunRecord record) => Record = record;

    public SyncRunRecord Record { get; }
    public string TaskName => Record.TaskName;
    public string CompletedAt => Record.CompletedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string Trigger => Record.Trigger;
    public string Result => Record.Succeeded ? "成功" : "失败";
    public string Summary => Record.Succeeded
        ? $"复制 {Record.CopiedFiles} · 删除 {Record.DeletedFiles} · 冲突 {Record.Conflicts}"
        : Record.Error ?? "未知错误";
}

public sealed record GameDiscoveryOutcome(
    IReadOnlyList<DiscoveredGame> Matches,
    long CatalogRevision,
    bool UsedCachedCopy,
    IReadOnlyList<string> RouteFailures);

public sealed record SettingsSaveResult(
    bool BackupRootChanged,
    int MovedBackupProfiles,
    int ProfilesWithoutBackups,
    string? StartupWarning);

public sealed record AutomaticBackupNotification(
    GameBackupProfile Profile,
    bool Succeeded,
    string Detail);
