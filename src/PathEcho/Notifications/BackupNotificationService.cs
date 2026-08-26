using PathEcho.Core.Models;

namespace PathEcho.Notifications;

internal sealed class BackupNotificationService : IDisposable
{
    private readonly Dictionary<WindowKey, BackupNotificationWindow> _windows = [];
    private BackupNotificationWindow? _previewWindow;

    internal void Show(BackupNotificationRequest request, BackupNotificationSettings settings)
    {
        var offset = settings.Offsets?.Get(settings.Position) ?? new BackupNotificationOffset();
        var key = new WindowKey(settings.Theme, settings.MonitorIndex, settings.Position, offset.X, offset.Y);
        if (!_windows.TryGetValue(key, out var window))
        {
            window = new BackupNotificationWindow(settings);
            _windows.Add(key, window);
        }

        window.Present(request);
    }

    internal void Preview(BackupNotificationSettings settings)
    {
        _previewWindow?.Close();
        _previewWindow = new BackupNotificationWindow(settings);
        _previewWindow.Present(new BackupNotificationRequest("通知位置预览", true, "自动备份完成 · 12 个文件"));
    }

    internal async Task CapturePreviewAsync(
        string path,
        BackupNotificationSettings settings,
        bool succeeded)
    {
        var offset = settings.Offsets?.Get(settings.Position) ?? new BackupNotificationOffset();
        var key = new WindowKey(settings.Theme, settings.MonitorIndex, settings.Position, offset.X, offset.Y);
        if (!_windows.TryGetValue(key, out var window))
        {
            window = new BackupNotificationWindow(settings);
            _windows.Add(key, window);
        }

        window.Present(new BackupNotificationRequest(
            "示例游戏",
            succeeded,
            succeeded ? "自动备份完成 · 128 个文件" : "存档目录暂时无法读取，PathEcho 将继续重试"));
        await window.CaptureAsync(path);
    }

    public void Dispose()
    {
        foreach (var window in _windows.Values)
        {
            window.Close();
        }

        _windows.Clear();
        _previewWindow?.Close();
        _previewWindow = null;
    }

    private sealed record WindowKey(
        BackupNotificationTheme Theme,
        int MonitorIndex,
        BackupNotificationPosition Position,
        int OffsetX,
        int OffsetY);
}
