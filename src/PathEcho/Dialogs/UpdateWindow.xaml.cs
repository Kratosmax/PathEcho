using System.Diagnostics;
using System.Windows;
using PathEcho.Core.Update;
using PathEcho.Services;

namespace PathEcho.Dialogs;

public partial class UpdateWindow : Window
{
    private readonly ApplicationUpdateService _updateService;
    private readonly bool _startImmediately;
    private UpdateCheckResult? _result;
    private CancellationTokenSource? _cancellation;

    public UpdateWindow(Window owner, UpdateNetworkOptions options, bool startImmediately = true)
    {
        Owner = owner;
        _updateService = new ApplicationUpdateService(options);
        _startImmediately = startImmediately;
        InitializeComponent();
        WindowBackdrop.Attach(this);
        Closed += (_, _) =>
        {
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _updateService.Dispose();
        };
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_startImmediately)
        {
            await CheckAsync();
        }
    }

    private async void OnPrimary(object sender, RoutedEventArgs e)
    {
        if (_result?.Availability == UpdateAvailability.Available && _result.Manifest is not null)
        {
            await DownloadAndInstallAsync(_result.Manifest);
            return;
        }

        if (_result?.Availability == UpdateAvailability.ManualOnly && _result.ManualDownloadUrl is not null)
        {
            Process.Start(new ProcessStartInfo(_result.ManualDownloadUrl) { UseShellExecute = true });
            return;
        }

        await CheckAsync();
    }

    private void OnSecondary(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel();
        Close();
    }

    private async Task CheckAsync()
    {
        SetBusy("正在获取并验证更新清单…");
        try
        {
            _cancellation = new CancellationTokenSource();
            _result = await _updateService.CheckAsync(_cancellation.Token);
            VersionText.Text = $"当前版本 {_result.CurrentVersion}";
            switch (_result.Availability)
            {
                case UpdateAvailability.Available:
                    VersionText.Text += $"  →  {_result.Manifest!.Version}";
                    NotesText.Text = _result.Manifest.ReleaseNotes;
                    StatusText.Text = $"已验证签名 · {_result.Manifest.PackageSize / 1024d / 1024d:F1} MB · {_result.Manifest.Channel}";
                    PrimaryButton.Content = "下载并安装";
                    break;
                case UpdateAvailability.Latest:
                    NotesText.Text = "当前已经是最新版本。";
                    StatusText.Text = "更新清单签名验证通过";
                    PrimaryButton.Content = "重新检查";
                    break;
                default:
                    NotesText.Text = "当前运行目录不具备就地更新条件，请从 GitHub Release 手动下载。";
                    StatusText.Text = "开发构建或安装标记缺失，未修改任何文件";
                    PrimaryButton.Content = "打开下载页";
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消，现有安装未修改";
        }
        catch (Exception exception)
        {
            NotesText.Text = exception.Message;
            StatusText.Text = "检查失败，现有安装未修改";
            PrimaryButton.Content = "重试";
        }
        finally
        {
            PrimaryButton.IsEnabled = true;
            SecondaryButton.IsEnabled = true;
        }
    }

    private async Task DownloadAndInstallAsync(UpdateManifest manifest)
    {
        SetBusy("正在连接更新线路…");
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.IsIndeterminate = false;
        var progress = new Progress<UpdateDownloadProgress>(value =>
        {
            var percent = Math.Min(99d, value.BytesReceived * 100d / value.MaximumBytes);
            DownloadProgress.Value = percent;
            StatusText.Text = $"正在下载 · {value.BytesReceived / 1024d / 1024d:F1} / {value.MaximumBytes / 1024d / 1024d:F1} MB";
        });
        try
        {
            _cancellation = new CancellationTokenSource();
            await _updateService.DownloadAndLaunchAsync(manifest, progress, _cancellation.Token);
            DownloadProgress.Value = 100;
            StatusText.Text = "验证完成，正在交给外部更新器…";
            PrimaryButton.Content = "正在退出";
            if (System.Windows.Application.Current is App app)
            {
                app.ExitApplication();
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "下载已取消，现有安装未修改";
            PrimaryButton.Content = "重试";
            PrimaryButton.IsEnabled = true;
            SecondaryButton.IsEnabled = true;
        }
        catch (Exception exception)
        {
            NotesText.Text = exception.Message;
            StatusText.Text = "下载或验证失败，现有安装未修改";
            PrimaryButton.Content = "重试";
            PrimaryButton.IsEnabled = true;
            SecondaryButton.IsEnabled = true;
        }
    }

    private void SetBusy(string status)
    {
        StatusText.Text = status;
        NotesText.Text = string.Empty;
        PrimaryButton.IsEnabled = false;
        SecondaryButton.IsEnabled = true;
    }
}
