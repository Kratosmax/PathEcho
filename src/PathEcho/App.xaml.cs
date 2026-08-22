using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PathEcho.Services;
using Forms = System.Windows.Forms;

namespace PathEcho;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private PathEchoRuntime? _runtime;
    private MainWindow? _mainWindow;
    private Forms.NotifyIcon? _trayIcon;
    private Icon? _trayIconImage;
    private bool _isExiting;

    public bool IsExiting => _isExiting;

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        var previewMode = e.Args.Contains("--preview", StringComparer.OrdinalIgnoreCase);
        var mutexName = previewMode
            ? $"Local\\PathEcho.Preview.{Environment.ProcessId}"
            : "Local\\PathEcho.SingleInstance";
        _singleInstance = new Mutex(true, mutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            System.Windows.MessageBox.Show("PathEcho 已经在运行。请从任务栏通知区域打开。", "PathEcho", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var previewSeed = e.Args.Contains("--preview-seed", StringComparer.OrdinalIgnoreCase);
        var background = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        _runtime = new PathEchoRuntime(previewMode, previewSeed);
        try
        {
            await _runtime.InitializeAsync();
        }
        catch (Exception exception)
        {
            AppLogger.Error("Application startup failed.", exception);
            System.Windows.MessageBox.Show($"PathEcho 启动失败，尚未开始后台监听。\n\n{exception.Message}", "PathEcho", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        _mainWindow = new MainWindow(_runtime);
        MainWindow = _mainWindow;
        CreateTrayIcon();
        if (previewMode || (!background && !_runtime.Configuration.StartMinimized))
        {
            _mainWindow.Show();
        }

        if (!previewMode && _runtime.Configuration.CheckForUpdates)
        {
            _ = _mainWindow.CheckForUpdatesInBackgroundAsync();
        }

        var capturePath = Environment.GetEnvironmentVariable("PATHECHO_CAPTURE_PATH");
        if (previewMode && !string.IsNullOrWhiteSpace(capturePath))
        {
            var captureView = Environment.GetEnvironmentVariable("PATHECHO_CAPTURE_VIEW");
            if (!string.IsNullOrWhiteSpace(captureView))
            {
                _mainWindow.SelectPreviewView(captureView);
            }

            if (string.Equals(Environment.GetEnvironmentVariable("PATHECHO_CAPTURE_SELECT_FIRST"), "1", StringComparison.Ordinal))
            {
                _mainWindow.SelectFirstSyncTaskForPreview();
            }

            await CapturePreviewAsync(capturePath);
        }
    }

    public void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
    }

    public void ExitApplication()
    {
        _isExiting = true;
        _mainWindow?.Close();
        Shutdown();
    }

    private void CreateTrayIcon()
    {
        var iconResource = GetResourceStream(new Uri("pack://application:,,,/Assets/PathEcho.ico"))
            ?? throw new InvalidOperationException("无法加载 PathEcho 图标资源。");
        _trayIconImage = new Icon(iconResource.Stream);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 PathEcho", null, (_, _) => Dispatcher.Invoke(ShowMainWindow));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "PathEcho",
            Icon = _trayIconImage,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMainWindow);
    }

    private async Task CapturePreviewAsync(string path)
    {
        if (_mainWindow is null)
        {
            return;
        }

        if (double.TryParse(Environment.GetEnvironmentVariable("PATHECHO_CAPTURE_WIDTH"), out var width))
        {
            _mainWindow.Width = Math.Max(_mainWindow.MinWidth, width);
        }

        if (double.TryParse(Environment.GetEnvironmentVariable("PATHECHO_CAPTURE_HEIGHT"), out var height))
        {
            _mainWindow.Height = Math.Max(_mainWindow.MinHeight, height);
        }

        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var dpi = VisualTreeHelper.GetDpi(_mainWindow);
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(_mainWindow.ActualWidth * dpi.DpiScaleX));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(_mainWindow.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(_mainWindow);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            encoder.Save(stream);
            await stream.FlushAsync();
        }

        ExitApplication();
    }

    private async void OnExit(object sender, ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _trayIconImage?.Dispose();
        if (_runtime is not null)
        {
            await _runtime.DisposeAsync();
        }

        if (_singleInstance is not null)
        {
            _singleInstance.ReleaseMutex();
            _singleInstance.Dispose();
        }
    }
}
