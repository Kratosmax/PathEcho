using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PathEcho.Platform.Windows.Instance;
using PathEcho.Services;
using Forms = System.Windows.Forms;

namespace PathEcho;

public partial class App : System.Windows.Application
{
    private SingleInstanceCoordinator? _singleInstance;
    private PathEchoRuntime? _runtime;
    private MainWindow? _mainWindow;
    private Forms.NotifyIcon? _trayIcon;
    private Icon? _trayIconImage;
    private bool _isExiting;
    private bool _activationRequested;

    public bool IsExiting => _isExiting;

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        var previewMode = e.Args.Contains("--preview", StringComparer.OrdinalIgnoreCase);
        var instanceName = previewMode
            ? $"Local\\PathEcho.Preview.{Environment.ProcessId}"
            : "Local\\PathEcho.SingleInstance";
        _singleInstance = SingleInstanceCoordinator.Create(
            instanceName,
            () =>
            {
                if (!Dispatcher.HasShutdownStarted)
                {
                    Dispatcher.BeginInvoke(RequestMainWindowActivation);
                }
            });
        if (!_singleInstance.IsPrimary)
        {
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
            AppLogger.Critical("Application startup failed.", exception);
            System.Windows.MessageBox.Show($"PathEcho 启动失败，尚未开始后台监听。\n\n{exception.Message}", "PathEcho", MessageBoxButton.OK, MessageBoxImage.Error);
            ExitApplication();
            return;
        }

        _mainWindow = new MainWindow(_runtime);
        MainWindow = _mainWindow;
        CreateTrayIcon();
        if (previewMode || _activationRequested || (!background && !_runtime.Configuration.StartMinimized))
        {
            ShowMainWindow();
        }

        SignalUpdateReady(e.Args);
        ShowUpdateResult(e.Args);

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
        _activationRequested = false;
    }

    private void ShowUpdateResult(string[] arguments)
    {
        string? path = null;
        bool succeeded;
        string message;
        try
        {
            path = GetUpdateStatePath(arguments, "--update-result");
            if (path is null)
            {
                return;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            succeeded = string.Equals(root.GetProperty("Status").GetString(), "succeeded", StringComparison.Ordinal);
            message = root.GetProperty("Message").GetString() ?? "更新流程已结束。";
        }
        catch (Exception exception)
        {
            AppLogger.Critical("Unable to read update result.", exception);
            ShowMainWindow();
            System.Windows.MessageBox.Show(
                "PathEcho 已重新启动，但无法读取更新结果详情。现有配置和备份未被删除，请查看日志或从 Release 页面手动覆盖安装。",
                "PathEcho 更新结果不可用",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        ShowMainWindow();
        System.Windows.MessageBox.Show(
            message,
            succeeded ? "PathEcho 更新完成" : "PathEcho 更新失败",
            MessageBoxButton.OK,
            succeeded ? MessageBoxImage.Information : MessageBoxImage.Error);
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLogger.Critical("Unable to delete consumed update result.", exception);
        }
    }

    private static void SignalUpdateReady(string[] arguments)
    {
        var path = GetUpdateStatePath(arguments, "--update-ready");
        if (path is null)
        {
            return;
        }

        var temporary = path + $".{Environment.ProcessId}.tmp";
        File.WriteAllText(temporary, "ready");
        File.Move(temporary, path, true);
    }

    private static string? GetUpdateStatePath(string[] arguments, string name)
    {
        var index = Array.FindIndex(arguments, argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= arguments.Length)
        {
            return null;
        }

        var path = Path.GetFullPath(arguments[index + 1]);
        var updateRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PathEcho",
            "Update"));
        var relative = Path.GetRelativePath(updateRoot, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException("更新状态文件不在允许的目录中。");
        }

        return path;
    }

    private void RequestMainWindowActivation()
    {
        if (_isExiting)
        {
            return;
        }

        if (_mainWindow is null)
        {
            _activationRequested = true;
            return;
        }

        ShowMainWindow();
    }

    public async void ExitApplication(bool discardUnsavedSettings = false)
    {
        if (_isExiting)
        {
            return;
        }

        if (!discardUnsavedSettings &&
            _mainWindow is not null &&
            !_mainWindow.TryDiscardUnsavedSettings(_mainWindow))
        {
            return;
        }

        _isExiting = true;
        _mainWindow?.Close();
        if (_runtime is not null)
        {
            try
            {
                await _runtime.DisposeAsync();
            }
            catch (Exception exception)
            {
                AppLogger.Critical("Application shutdown cleanup failed.", exception);
            }

            _runtime = null;
        }

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
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(() => ExitApplication()));
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

        ExitApplication(discardUnsavedSettings: true);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.Critical("Unhandled UI exception.", e.Exception);
        e.Handled = true;
        System.Windows.MessageBox.Show(
            $"PathEcho 遇到无法恢复的异常，需要关闭。\n\n{e.Exception.Message}\n\n错误详情已写入日志。",
            "PathEcho",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        ExitApplication(discardUnsavedSettings: true);
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _trayIconImage?.Dispose();
        _singleInstance?.Dispose();
    }
}
