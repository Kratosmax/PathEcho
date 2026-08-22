using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using PathEcho.Platform.Windows.Windows;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfControl = System.Windows.Controls.Control;
using WpfPanel = System.Windows.Controls.Panel;

namespace PathEcho;

internal static class WindowBackdrop
{
    private static readonly SolidColorBrush FallbackBrush = CreateBrush(255);
    private static readonly SolidColorBrush AcrylicSurfaceBrush = CreateBrush(184);

    public static void Attach(Window window)
    {
        ApplySurface(window, FallbackBrush);
        window.SourceInitialized += (_, _) => Apply(window);
    }

    private static void Apply(Window window)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("PATHECHO_DISABLE_BACKDROP"), "1", StringComparison.Ordinal))
        {
            ApplySurface(window, FallbackBrush);
            return;
        }

        var source = (HwndSource?)PresentationSource.FromVisual(window);
        if (source?.CompositionTarget is null)
        {
            ApplySurface(window, FallbackBrush);
            return;
        }

        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        var result = WindowBackdropService.TryApplyAcrylic(source.Handle);
        if (result.Status == WindowBackdropStatus.Applied)
        {
            window.Background = WpfBrushes.Transparent;
            ApplyContentSurface(window, AcrylicSurfaceBrush);
            return;
        }

        source.CompositionTarget.BackgroundColor = WpfColor.FromRgb(244, 247, 248);
        ApplySurface(window, FallbackBrush);
    }

    private static void ApplySurface(Window window, WpfBrush brush)
    {
        window.Background = brush;
        ApplyContentSurface(window, brush);
    }

    private static void ApplyContentSurface(Window window, WpfBrush brush)
    {
        switch (window.Content)
        {
            case WpfPanel panel:
                panel.Background = brush;
                break;
            case WpfControl control:
                control.Background = brush;
                break;
            case Border border:
                border.Background = brush;
                break;
        }
    }

    private static SolidColorBrush CreateBrush(byte alpha)
    {
        var brush = new SolidColorBrush(WpfColor.FromArgb(alpha, 244, 247, 248));
        brush.Freeze();
        return brush;
    }
}
