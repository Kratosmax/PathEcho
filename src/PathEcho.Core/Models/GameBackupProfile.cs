namespace PathEcho.Core.Models;

[Flags]
public enum BackupTrigger
{
    None = 0,
    Scheduled = 1,
    ImportantFileChanged = 2,
    ChangedFiles = 4,
    ProcessExited = 8,
}

public sealed record GameBackupProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = "新游戏";

    public string SaveDirectory { get; init; } = string.Empty;

    public string? BackupDirectory { get; init; }

    public BackupTrigger Triggers { get; init; } = BackupTrigger.ProcessExited;

    public TimeSpan ScheduleInterval { get; init; } = TimeSpan.FromHours(1);

    public TimeSpan MinimumBackupInterval { get; init; } = TimeSpan.FromMinutes(5);

    public int RetainedVersions { get; init; } = 50;

    public int RetainedHourlyVersions { get; init; } = 24;

    public int RetainedDailyVersions { get; init; } = 30;

    public IReadOnlyList<string> ImportantFilePatterns { get; init; } = Array.Empty<string>();

    public string? ProcessExecutablePath { get; init; }

    public bool IsEnabled { get; init; } = true;

    public BackupNotificationMode BackupNotificationMode { get; init; } = BackupNotificationMode.Inherit;

    public BackupNotificationSettings? BackupNotificationSettings { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidOperationException("游戏名称不能为空。");
        }

        if (string.IsNullOrWhiteSpace(SaveDirectory))
        {
            throw new InvalidOperationException("游戏存档目录不能为空。");
        }

        if (RetainedVersions < 1)
        {
            throw new InvalidOperationException("至少保留一个备份版本。");
        }

        if (RetainedHourlyVersions < 0)
        {
            throw new InvalidOperationException("每小时锚点数量不能小于零。");
        }

        if (RetainedDailyVersions < 0)
        {
            throw new InvalidOperationException("每日锚点数量不能小于零。");
        }

        if (ScheduleInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("定时备份间隔必须大于零。");
        }

        if (MinimumBackupInterval < TimeSpan.Zero)
        {
            throw new InvalidOperationException("最低备份间隔不能小于零。");
        }

        if (Triggers.HasFlag(BackupTrigger.ImportantFileChanged) && ImportantFilePatterns.Count == 0)
        {
            throw new InvalidOperationException("重点文件变动备份至少需要一个正则表达式。");
        }

        foreach (var pattern in ImportantFilePatterns)
        {
            try
            {
                _ = new System.Text.RegularExpressions.Regex(pattern);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidOperationException($"重点文件正则表达式无效：{pattern}", exception);
            }
        }

        if (Triggers.HasFlag(BackupTrigger.ProcessExited) && string.IsNullOrWhiteSpace(ProcessExecutablePath))
        {
            throw new InvalidOperationException("进程退出备份需要设置游戏程序路径。");
        }
    }

    public string ResolveBackupDirectory(string defaultBackupDirectory) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(BackupDirectory) ? defaultBackupDirectory : BackupDirectory);
}
