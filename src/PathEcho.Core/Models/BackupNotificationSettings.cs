namespace PathEcho.Core.Models;

public enum BackupNotificationTheme
{
    Dark,
    Light,
}

public enum BackupNotificationPosition
{
    BottomRight,
    TopRight,
    BottomLeft,
    TopLeft,
}

public enum BackupNotificationMode
{
    Inherit,
    Disabled,
    Custom,
}

public sealed record BackupNotificationOffset
{
    public int X { get; init; }

    public int Y { get; init; }
}

public sealed record BackupNotificationOffsets
{
    public BackupNotificationOffset BottomRight { get; init; } = new();

    public BackupNotificationOffset TopRight { get; init; } = new();

    public BackupNotificationOffset BottomLeft { get; init; } = new();

    public BackupNotificationOffset TopLeft { get; init; } = new();

    public BackupNotificationOffset Get(BackupNotificationPosition position) => position switch
    {
        BackupNotificationPosition.TopRight => TopRight ?? new(),
        BackupNotificationPosition.BottomLeft => BottomLeft ?? new(),
        BackupNotificationPosition.TopLeft => TopLeft ?? new(),
        _ => BottomRight ?? new(),
    };

    public BackupNotificationOffsets With(BackupNotificationPosition position, BackupNotificationOffset offset) => position switch
    {
        BackupNotificationPosition.TopRight => this with { TopRight = offset },
        BackupNotificationPosition.BottomLeft => this with { BottomLeft = offset },
        BackupNotificationPosition.TopLeft => this with { TopLeft = offset },
        _ => this with { BottomRight = offset },
    };
}

public sealed record BackupNotificationSettings
{
    public BackupNotificationTheme Theme { get; init; } = BackupNotificationTheme.Dark;

    public int MonitorIndex { get; init; }

    public BackupNotificationPosition Position { get; init; } = BackupNotificationPosition.BottomRight;

    public BackupNotificationOffsets Offsets { get; init; } = new();
}

public static class BackupNotificationResolver
{
    public static BackupNotificationSettings? Resolve(AppConfiguration configuration, GameBackupProfile profile) =>
        profile.BackupNotificationMode switch
        {
            BackupNotificationMode.Disabled => null,
            BackupNotificationMode.Custom => profile.BackupNotificationSettings ?? new BackupNotificationSettings(),
            _ => configuration.AutomaticBackupNotificationsEnabled
                ? configuration.DefaultBackupNotification ?? new BackupNotificationSettings()
                : null,
        };
}

public readonly record struct BackupNotificationCoordinates(int X, int Y);

public static class BackupNotificationPlacement
{
    public static BackupNotificationCoordinates Resolve(
        int left,
        int top,
        int right,
        int bottom,
        int width,
        int height,
        int gap,
        BackupNotificationPosition position,
        int offsetX,
        int offsetY)
    {
        var leftAnchored = position is BackupNotificationPosition.TopLeft or BackupNotificationPosition.BottomLeft;
        var topAnchored = position is BackupNotificationPosition.TopLeft or BackupNotificationPosition.TopRight;
        var anchoredX = leftAnchored ? left + gap : right - width - gap;
        var anchoredY = topAnchored ? top + gap : bottom - height - gap;
        return new BackupNotificationCoordinates(
            Math.Clamp(anchoredX + offsetX, left, Math.Max(left, right - width)),
            Math.Clamp(anchoredY + offsetY, top, Math.Max(top, bottom - height)));
    }
}
