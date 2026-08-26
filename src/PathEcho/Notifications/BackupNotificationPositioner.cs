using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using PathEcho.Core.Models;

namespace PathEcho.Notifications;

internal static class BackupNotificationPositioner
{
    private static readonly nint HwndTopmost = new(-1);

    internal static void Position(
        Window window,
        System.Drawing.Rectangle workingArea,
        BackupNotificationPosition position,
        int offsetX,
        int offsetY)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0)
        {
            return;
        }

        var center = new NativePoint
        {
            X = workingArea.Left + workingArea.Width / 2,
            Y = workingArea.Top + workingArea.Height / 2,
        };
        var monitor = MonitorFromPoint(center, 2);
        var scale = monitor != 0 && GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0
            ? dpiX / 96d
            : VisualTreeHelper.GetDpi(window).DpiScaleX;
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * scale));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * scale));
        var point = BackupNotificationPlacement.Resolve(
            workingArea.Left,
            workingArea.Top,
            workingArea.Right,
            workingArea.Bottom,
            width,
            height,
            18,
            position,
            offsetX,
            offsetY);
        _ = SetWindowPos(handle, HwndTopmost, point.X, point.Y, width, height, 0x0010 | 0x0040);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint handle, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
