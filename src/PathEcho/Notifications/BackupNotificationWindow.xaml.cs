using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PathEcho.Core.Models;
using Forms = System.Windows.Forms;
using MediaColor = System.Windows.Media.Color;

namespace PathEcho.Notifications;

internal sealed record BackupNotificationRequest(string GameName, bool Succeeded, string Detail);

public partial class BackupNotificationWindow : Window
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);
    private readonly BackupNotificationSettings _settings;
    private readonly List<QueueItem> _items = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    internal BackupNotificationWindow(BackupNotificationSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        _timer.Tick += (_, _) => Refresh();
        SourceInitialized += (_, _) => MakeNonActivating();
        Loaded += (_, _) => PositionWindow();
    }

    internal void Present(BackupNotificationRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        Prune(now);
        var duplicate = _items.FirstOrDefault(item => item.Request == request);
        if (duplicate is not null)
        {
            duplicate.Count++;
            duplicate.ExpiresAt = now + Lifetime;
        }
        else
        {
            _items.Add(new QueueItem(request, now + Lifetime));
        }
        while (_items.Count > 4)
        {
            _items.RemoveAt(0);
        }

        Render();
        if (!IsVisible)
        {
            Show();
        }

        UpdateLayout();
        PositionWindow();
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
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

    private void Refresh()
    {
        Prune(DateTimeOffset.UtcNow);
        if (_items.Count == 0)
        {
            Hide();
            _timer.Stop();
            return;
        }

        Render();
        UpdateLayout();
        PositionWindow();
    }

    private void Render()
    {
        var dark = _settings.Theme == BackupNotificationTheme.Dark;
        var background = dark ? MediaColor.FromArgb(246, 24, 34, 31) : MediaColor.FromArgb(250, 255, 255, 255);
        var foreground = dark ? Colors.White : MediaColor.FromRgb(23, 32, 29);
        var muted = dark ? MediaColor.FromRgb(190, 205, 199) : MediaColor.FromRgb(91, 105, 100);
        QueueRows.Children.Clear();

        for (var index = 0; index < _items.Count; index++)
        {
            var request = _items[index].Request;
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
                Text = _items[index].Count > 1 ? $"{request.Detail} · 重复 {_items[index].Count} 次" : request.Detail,
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

    private void Prune(DateTimeOffset now) => _items.RemoveAll(item => item.ExpiresAt <= now);

    private sealed class QueueItem(BackupNotificationRequest request, DateTimeOffset expiresAt)
    {
        internal BackupNotificationRequest Request { get; } = request;
        internal DateTimeOffset ExpiresAt { get; set; } = expiresAt;
        internal int Count { get; set; } = 1;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint handle, int index, nint newStyle);
}
