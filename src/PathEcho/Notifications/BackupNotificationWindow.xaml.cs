using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PathEcho.Core.Models;
using PathEcho.Core.Notifications;
using Forms = System.Windows.Forms;
using MediaColor = System.Windows.Media.Color;

namespace PathEcho.Notifications;

internal sealed record BackupNotificationRequest(string GameName, bool Succeeded, string Detail);

public partial class BackupNotificationWindow : Window
{
    private readonly BackupNotificationSettings _settings;
    private readonly BackupNotificationQueue<BackupNotificationRequest> _queue = new();
    private readonly long _clockOrigin = Stopwatch.GetTimestamp();
    private readonly DispatcherTimer _timer = new();
    private bool _closed;

    internal BackupNotificationWindow(BackupNotificationSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        _timer.Tick += OnExpiration;
        SourceInitialized += (_, _) => MakeNonActivating();
        IsVisibleChanged += OnVisibilityChanged;
    }

    internal void Present(BackupNotificationRequest request)
    {
        if (_closed || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _queue.Enqueue(request, Elapsed);

        Render();
        if (!IsVisible)
        {
            Show();
        }

        UpdateLayout();
        PositionWindow();
        ScheduleExpiration();
    }

    internal async Task CaptureAsync(string path)
    {
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(this);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        encoder.Save(stream);
        await stream.FlushAsync();
    }

    private TimeSpan Elapsed => Stopwatch.GetElapsedTime(_clockOrigin);

    private void OnExpiration(object? sender, EventArgs e)
    {
        // DispatcherTimer repeats by default. Treat each scheduled deadline as one shot.
        _timer.Stop();
        if (_closed || !IsVisible)
        {
            return;
        }

        if (_queue.Expire(Elapsed))
        {
            Render();
            if (_queue.Items.Count == 0)
            {
                Hide();
                return;
            }

            UpdateLayout();
            PositionWindow();
        }

        ScheduleExpiration();
    }

    private void ScheduleExpiration()
    {
        _timer.Stop();
        if (_closed || !IsVisible || _queue.NextExpiration is not { } next)
        {
            return;
        }

        var delay = next - Elapsed;
        // A dispatcher wakeup can be slightly early; never spin on a zero interval.
        _timer.Interval = delay > TimeSpan.FromMilliseconds(1) ? delay : TimeSpan.FromMilliseconds(1);
        _timer.Start();
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            _timer.Stop();
        }
        else
        {
            ScheduleExpiration();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _timer.Stop();
        _timer.Tick -= OnExpiration;
        IsVisibleChanged -= OnVisibilityChanged;
        _queue.Clear();
        QueueRows.Children.Clear();
        base.OnClosed(e);
    }

    private void Render()
    {
        var dark = _settings.Theme == BackupNotificationTheme.Dark;
        var background = dark ? MediaColor.FromArgb(246, 24, 34, 31) : MediaColor.FromArgb(250, 255, 255, 255);
        var foreground = dark ? Colors.White : MediaColor.FromRgb(23, 32, 29);
        var muted = dark ? MediaColor.FromRgb(190, 205, 199) : MediaColor.FromRgb(91, 105, 100);
        QueueRows.Children.Clear();

        for (var index = 0; index < _queue.Items.Count; index++)
        {
            var request = _queue.Items[index].Request;
            var accent = request.Succeeded ? MediaColor.FromRgb(45, 184, 128) : MediaColor.FromRgb(222, 84, 75);
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            content.Children.Add(new Border
            {
                Background = new SolidColorBrush(accent),
                CornerRadius = new CornerRadius(2),
            });

            var icon = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(17),
                Background = new SolidColorBrush(MediaColor.FromArgb(35, accent.R, accent.G, accent.B)),
                Child = new TextBlock
                {
                    Text = request.Succeeded ? "✓" : "!",
                    FontSize = 19,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(accent),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                },
            };
            Grid.SetColumn(icon, 2);
            content.Children.Add(icon);

            var text = new StackPanel();
            Grid.SetColumn(text, 4);
            text.Children.Add(new TextBlock
            {
                Text = request.Succeeded ? "自动备份完成" : "自动备份失败",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(foreground),
            });
            text.Children.Add(new TextBlock
            {
                Text = request.GameName,
                FontSize = 13,
                Foreground = new SolidColorBrush(foreground),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 3, 0, 0),
            });
            text.Children.Add(new TextBlock
            {
                Text = _queue.Items[index].Count > 1 ? $"{request.Detail} · 重复 {_queue.Items[index].Count} 次" : request.Detail,
                FontSize = 12,
                Foreground = new SolidColorBrush(muted),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 38,
                Margin = new Thickness(0, 2, 0, 0),
            });
            content.Children.Add(text);

            QueueRows.Children.Add(new Border
            {
                Background = new SolidColorBrush(background),
                BorderBrush = new SolidColorBrush(dark ? MediaColor.FromRgb(66, 82, 76) : MediaColor.FromRgb(215, 224, 220)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(0, 13, 14, 13),
                Margin = new Thickness(0, index == 0 ? 0 : 8, 0, 0),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 16,
                    ShadowDepth = 3,
                    Opacity = dark ? 0.35 : 0.18,
                },
                Child = content,
            });
        }
    }

    private void PositionWindow()
    {
        var screens = Forms.Screen.AllScreens;
        if (screens.Length == 0 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var screen = screens[Math.Clamp(_settings.MonitorIndex, 0, screens.Length - 1)];
        var offset = _settings.Offsets?.Get(_settings.Position) ?? new BackupNotificationOffset();
        BackupNotificationPositioner.Position(this, screen.WorkingArea, _settings.Position, offset.X, offset.Y);
    }

    private void MakeNonActivating()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(handle, -20).ToInt64();
        _ = SetWindowLongPtr(handle, -20, new nint(style | 0x08000000L | 0x00000080L));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint handle, int index, nint newStyle);
}
