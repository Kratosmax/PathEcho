using System.Runtime.InteropServices;

namespace PathEcho.Platform.Windows.Windows;

public enum WindowBackdropStatus
{
    Applied,
    Unsupported,
    Failed,
}

public readonly record struct WindowBackdropResult(WindowBackdropStatus Status, int HResult = 0);

public static class WindowBackdropService
{
    // DWM_WINDOW_CORNER_PREFERENCE and DWM_SYSTEMBACKDROP_TYPE from dwmapi.h.
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmsbtNone = 1;
    private const int DwmsbtTransientWindow = 3;
    private const int DwmwcpRound = 2;

    public static WindowBackdropResult TryApplyAcrylic(nint windowHandle)
    {
        if (windowHandle == 0 || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            return new WindowBackdropResult(WindowBackdropStatus.Unsupported);
        }

        var lightMode = 0;
        _ = DwmSetWindowAttribute(windowHandle, DwmwaUseImmersiveDarkMode, ref lightMode, sizeof(int));

        var cornerPreference = DwmwcpRound;
        _ = DwmSetWindowAttribute(windowHandle, DwmwaWindowCornerPreference, ref cornerPreference, sizeof(int));

        var backdropType = DwmsbtTransientWindow;
        var backdropResult = DwmSetWindowAttribute(windowHandle, DwmwaSystemBackdropType, ref backdropType, sizeof(int));
        if (backdropResult < 0)
        {
            Reset(windowHandle);
            return new WindowBackdropResult(WindowBackdropStatus.Failed, backdropResult);
        }

        var margins = new Margins(-1, -1, -1, -1);
        var frameResult = DwmExtendFrameIntoClientArea(windowHandle, ref margins);
        if (frameResult < 0)
        {
            Reset(windowHandle);
            return new WindowBackdropResult(WindowBackdropStatus.Failed, frameResult);
        }

        return new WindowBackdropResult(WindowBackdropStatus.Applied);
    }

    public static void Reset(nint windowHandle)
    {
        if (windowHandle == 0)
        {
            return;
        }

        var backdropType = DwmsbtNone;
        _ = DwmSetWindowAttribute(windowHandle, DwmwaSystemBackdropType, ref backdropType, sizeof(int));
        var margins = new Margins(0, 0, 0, 0);
        _ = DwmExtendFrameIntoClientArea(windowHandle, ref margins);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Margins(int left, int right, int top, int bottom)
    {
        public readonly int Left = left;
        public readonly int Right = right;
        public readonly int Top = top;
        public readonly int Bottom = bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int valueSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);
}
