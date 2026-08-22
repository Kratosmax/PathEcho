using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using PathEcho.Core.Backup;
using PathEcho.Core.Restore;
using PathEcho.Core.Update;
using PathEcho.Dialogs;
using PathEcho.Services;
using Button = System.Windows.Controls.Button;
using OpenFolderDialog = Microsoft.Win32.OpenFolderDialog;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;

namespace PathEcho;

public partial class MainWindow : Window
{
    private readonly PathEchoRuntime _runtime;
    private readonly ICollectionView _syncTaskView;

    public ObservableCollection<UpdateRouteRow> UpdateRoutes { get; } = new();

    public IReadOnlyList<int> UpdateRoutePriorities { get; } = Enumerable.Range(0, 11).Reverse().ToArray();

    public MainWindow(PathEchoRuntime runtime)
    {
        _runtime = runtime;
        InitializeComponent();
        WindowBackdrop.Attach(this);
        var currentVersion = ApplicationUpdateService.CurrentVersion;
        SidebarVersionText.Text = $"v{currentVersion}";
        SettingsVersionText.Text = $"当前版本 {currentVersion}";
        DataContext = runtime;
        _syncTaskView = CollectionViewSource.GetDefaultView(runtime.SyncTasks);
        _syncTaskView.Filter = MatchesSyncFilter;
        SyncGrid.ItemsSource = _syncTaskView;
        SyncStatusFilter.ItemsSource = new[] { "全部状态", "可用", "目录异常", "同步中", "失败" };
        SyncStatusFilter.SelectedIndex = 0;
        StartupCheck.IsChecked = runtime.Configuration.StartWithWindows;
        MinimizedCheck.IsChecked = runtime.Configuration.StartMinimized;
        UpdateCheck.IsChecked = runtime.Configuration.CheckForUpdates;
        DebugLogCheck.IsChecked = runtime.Configuration.EnableDebugLogging;
        foreach (var route in runtime.Configuration.UpdateNetwork.UrlRoutes)
        {
            UpdateRoutes.Add(new UpdateRouteRow(route));
        }

        if (UpdateRoutes.All(route => !route.IsDirect))
        {
            UpdateRoutes.Add(new UpdateRouteRow(UpdateUrlRoute.Direct));
        }

        HttpProxyBox.Text = runtime.Configuration.UpdateNetwork.HttpProxy ?? string.Empty;
        BackupDirectoryBox.Text = runtime.Configuration.DefaultBackupDirectory;
        BackgroundStatusText.Text = runtime.IsPreviewMode ? "预览模式 · 未启动监听" : "后台监听已启用";
        BackgroundStatusText.Foreground = runtime.IsPreviewMode
            ? new SolidColorBrush(MediaColor.FromRgb(102, 115, 110))
            : new SolidColorBrush(MediaColor.FromRgb(22, 122, 91));
        runtime.SyncTasks.CollectionChanged += OnCollectionChanged;
        runtime.GameProfiles.CollectionChanged += OnCollectionChanged;
        runtime.History.CollectionChanged += OnCollectionChanged;
        UpdateEmptyStates();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _syncTaskView.Refresh();
        UpdateEmptyStates();
    }

    private void UpdateEmptyStates()
    {
        var syncCount = _runtime.SyncTasks.Count;
        var visibleSyncCount = _syncTaskView.Cast<object>().Count();
        var selectedCount = SyncGrid.SelectedItem is SyncTaskRow ? 1 : 0;
        SyncSummaryText.Text = selectedCount > 0
            ? $"{syncCount} 个任务 · 已选中 {selectedCount} 个"
            : visibleSyncCount == syncCount
                ? $"{syncCount} 个任务"
                : $"显示 {visibleSyncCount} / {syncCount} 个任务";
        SyncEmpty.Visibility = syncCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        SyncNoResults.Visibility = syncCount > 0 && visibleSyncCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        SyncGrid.Visibility = visibleSyncCount == 0 ? Visibility.Collapsed : Visibility.Visible;
        RunAllSyncButton.IsEnabled = syncCount > 0;
        GameEmpty.Visibility = _runtime.GameProfiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        GameGrid.Visibility = _runtime.GameProfiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        HistoryEmpty.Visibility = _runtime.History.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryGrid.Visibility = _runtime.History.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnNavigate(object sender, RoutedEventArgs e)
    {
        var selected = (sender as Button)?.Tag as string ?? "Sync";
        SelectView(selected);
    }

    public void SelectPreviewView(string selected) => SelectView(selected);

    public void SelectFirstSyncTaskForPreview()
    {
        SyncGrid.SelectedIndex = SyncGrid.Items.Count > 0 ? 0 : -1;
    }

    private void SelectView(string selected)
    {
        SyncView.Visibility = selected == "Sync" ? Visibility.Visible : Visibility.Collapsed;
        GameView.Visibility = selected == "Game" ? Visibility.Visible : Visibility.Collapsed;
        HistoryView.Visibility = selected == "History" ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = selected == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { SyncNavButton, GameNavButton, HistoryNavButton, SettingsNavButton })
        {
            var active = string.Equals(button.Tag as string, selected, StringComparison.Ordinal);
            button.Background = active ? new SolidColorBrush(MediaColor.FromRgb(43, 55, 51)) : MediaBrushes.Transparent;
            button.BorderBrush = active ? button.Background : MediaBrushes.Transparent;
            button.Foreground = active ? MediaBrushes.White : new SolidColorBrush(MediaColor.FromRgb(214, 223, 219));
        }
    }

    private async void OnAddSync(object sender, RoutedEventArgs e)
    {
        var dialog = new SyncTaskEditorWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null)
        {
            return;
        }

        await RunUiActionAsync("正在创建同步任务", async () =>
        {
            await _runtime.AddSyncTaskAsync(dialog.Result);
            StatusText.Text = "同步任务已创建并开始监听";
        });
    }

    private async void OnAddGame(object sender, RoutedEventArgs e)
    {
        var dialog = new GameProfileEditorWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null)
        {
            return;
        }

        await RunUiActionAsync("正在添加游戏存档", async () =>
        {
            await _runtime.AddGameProfileAsync(dialog.Result);
            StatusText.Text = "游戏存档已添加";
        });
    }

    private async void OnRunSyncRow(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SyncTaskRow row)
        {
            return;
        }

        await RunSyncAsync(row);
    }

    private async Task RunSyncAsync(SyncTaskRow row)
    {
        await RunUiActionAsync($"正在同步 {row.Name}", async () =>
        {
            await _runtime.RunSyncNowAsync(row.Definition.Id);
            StatusText.Text = $"{row.Name} 同步完成";
        });
        RefreshSyncFilter();
    }

    private async void OnRunAllSync(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync("正在同步全部任务", async () =>
        {
            foreach (var row in _runtime.SyncTasks.ToArray())
            {
                await _runtime.RunSyncNowAsync(row.Definition.Id);
            }

            StatusText.Text = $"全部同步完成，共 {_runtime.SyncTasks.Count} 个任务";
        });
    }

    private async void OnBackupRow(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GameProfileRow row)
        {
            return;
        }

        await RunUiActionAsync($"正在备份 {row.Name}", async () =>
        {
            await _runtime.BackupNowAsync(row.Definition.Id);
            StatusText.Text = $"{row.Name} 已创建新备份版本";
        });
    }

    private async void OnRefreshHistory(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync("正在刷新版本历史", async () =>
        {
            await _runtime.RefreshHistoryAsync();
            StatusText.Text = $"已找到 {_runtime.History.Count} 个备份版本";
        });
    }

    private void OnOpenSnapshot(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HistoryRow row)
        {
            return;
        }

        var files = System.IO.Path.Combine(row.SnapshotDirectory, "files");
        OpenDirectory(files);
    }

    private async void OnRestoreRow(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HistoryRow row)
        {
            return;
        }

        var dialog = new RestoreWindow(row) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null)
        {
            return;
        }

        var request = dialog.Result;
        await RunUiActionAsync($"正在检查 {row.GameName} 的存档", async () =>
        {
            try
            {
                var result = await _runtime.RestoreAsync(request);
                StatusText.Text = $"回档完成，恢复 {result.RestoredFiles} 个文件";
            }
            catch (FilesOccupiedException exception)
            {
                var processes = string.Join("、", exception.OccupiedFiles.SelectMany(file => file.Processes)
                    .Select(process => $"{process.Name} ({process.ProcessId})")
                    .Distinct());
                var prompt = new PromptWindow(
                    this,
                    "存档正在使用",
                    $"以下程序正在占用存档：\n\n{processes}",
                    "结束进程后恢复",
                    "强行尝试",
                    "取消");
                prompt.ShowDialog();
                if (prompt.Choice is PromptChoice.None or PromptChoice.Tertiary)
                {
                    StatusText.Text = "已取消回档，存档未修改";
                    return;
                }

                var action = prompt.Choice == PromptChoice.Primary
                    ? OccupiedFileAction.EndProcesses
                    : OccupiedFileAction.ForceAttempt;
                var result = await _runtime.RestoreAsync(request with { OccupiedFileAction = action });
                StatusText.Text = $"回档完成，恢复 {result.RestoredFiles} 个文件";
            }
        });
    }

    private void OnBrowseDefaultBackup(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog();
        if (dialog.ShowDialog() == true)
        {
            BackupDirectoryBox.Text = dialog.FolderName;
        }
    }

    private async void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(BackupDirectoryBox.Text))
        {
            ShowError("默认备份目录不能为空。");
            return;
        }

        await RunUiActionAsync("正在保存设置并校验备份目录", async () =>
        {
            await _runtime.SaveSettingsAsync(
                StartupCheck.IsChecked == true,
                MinimizedCheck.IsChecked == true,
                UpdateCheck.IsChecked == true,
                DebugLogCheck.IsChecked == true,
                BackupDirectoryBox.Text.Trim(),
                BuildUpdateNetworkOptions());
            BackupDirectoryBox.Text = _runtime.Configuration.DefaultBackupDirectory;
            StatusText.Text = "设置已保存";
        });
    }

    private void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        if (_runtime.IsPreviewMode)
        {
            ShowError("预览模式不会访问更新网络。");
            return;
        }

        try
        {
            new UpdateWindow(this, BuildUpdateNetworkOptions()).ShowDialog();
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    public async Task CheckForUpdatesInBackgroundAsync()
    {
        if (_runtime.IsPreviewMode || !_runtime.Configuration.CheckForUpdates)
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            using var service = new ApplicationUpdateService(_runtime.Configuration.UpdateNetwork);
            var result = await service.CheckAsync();
            if (result.Availability == UpdateAvailability.Available)
            {
                new UpdateWindow(this, _runtime.Configuration.UpdateNetwork).ShowDialog();
            }
        }
        catch (Exception exception)
        {
            AppLogger.Error("Background update check failed.", exception);
            StatusText.Text = "后台更新检查失败，可在设置中重试";
        }
    }

    private UpdateNetworkOptions BuildUpdateNetworkOptions()
    {
        var routes = UpdateRoutes.Select(route => new UpdateUrlRoute
        {
            BaseUrl = route.IsDirect ? null : route.BaseUrl.Trim(),
            Priority = route.Priority,
            IsDirect = route.IsDirect,
        }).ToArray();
        return UpdateRoutePlanner.Normalize(new UpdateNetworkOptions
        {
            UrlRoutes = routes,
            HttpProxy = string.IsNullOrWhiteSpace(HttpProxyBox.Text) ? null : HttpProxyBox.Text.Trim(),
        });
    }

    private void OnAddUpdateRoute(object sender, RoutedEventArgs e)
    {
        var route = new UpdateRouteRow(new UpdateUrlRoute { Priority = 8 });
        UpdateRoutes.Add(route);
        UpdateRoutesGrid.SelectedItem = route;
        UpdateRoutesGrid.ScrollIntoView(route);
    }

    private void OnDeleteUpdateRoute(object sender, RoutedEventArgs e)
    {
        if (UpdateRoutesGrid.SelectedItem is UpdateRouteRow { IsDirect: false } route)
        {
            UpdateRoutes.Remove(route);
        }
    }

    private void OnUpdateRouteSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        DeleteUpdateRouteButton.IsEnabled = UpdateRoutesGrid.SelectedItem is UpdateRouteRow { IsDirect: false };

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            AppLogger.OpenLogDirectory();
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private async void OnDiscoverBackups(object sender, RoutedEventArgs e)
    {
        if (_runtime.IsPreviewMode)
        {
            ShowError("预览模式不会导入真实备份。");
            return;
        }

        var dialog = new OpenFolderDialog { Title = "选择需要扫描的备份目录" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await RunUiActionAsync("正在发现已有备份", async () =>
        {
            var manager = new BackupDirectoryManager();
            var discovered = await manager.DiscoverAsync(dialog.FolderName);
            var imported = 0;
            foreach (var candidate in discovered)
            {
                var profile = _runtime.Configuration.GameProfiles.FirstOrDefault(item => item.Id == candidate.ProfileId);
                if (profile is null)
                {
                    continue;
                }

                var targetRoot = profile.ResolveBackupDirectory(_runtime.Configuration.DefaultBackupDirectory);
                var target = System.IO.Path.Combine(targetRoot, profile.Id.ToString("N"));
                if (Directory.Exists(target))
                {
                    continue;
                }

                await manager.ImportAsync(candidate, targetRoot);
                imported++;
            }

            await _runtime.RefreshHistoryAsync();
            StatusText.Text = $"发现 {discovered.Count} 组备份，已导入 {imported} 组";
        });
    }

    private void OnSyncSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var visibility = SyncGrid.SelectedItem is SyncTaskRow ? Visibility.Visible : Visibility.Collapsed;
        EditSyncButton.Visibility = visibility;
        DeleteSyncButton.Visibility = visibility;
        UpdateEmptyStates();
    }

    private void OnSyncFilterChanged(object sender, EventArgs e)
    {
        if (!IsInitialized || _syncTaskView is null)
        {
            return;
        }

        RefreshSyncFilter();
    }

    private bool MatchesSyncFilter(object item)
    {
        if (item is not SyncTaskRow row)
        {
            return false;
        }

        var query = SyncSearchBox?.Text.Trim() ?? string.Empty;
        var matchesQuery = query.Length == 0 || new[]
        {
            row.Name, row.LeftPath, row.RightPath, row.Mode, row.Deletion, row.Status,
        }.Any(value => value.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        if (!matchesQuery)
        {
            return false;
        }

        return (SyncStatusFilter?.SelectedItem as string) switch
        {
            "可用" => !row.HasDirectoryIssue,
            "目录异常" => row.HasDirectoryIssue,
            "同步中" => row.Status.Contains("正在", StringComparison.Ordinal),
            "失败" => row.Status.Contains("失败", StringComparison.Ordinal),
            _ => true,
        };
    }

    private void RefreshSyncFilter()
    {
        SyncSearchHint.Visibility = string.IsNullOrEmpty(SyncSearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        _syncTaskView.Refresh();
        UpdateEmptyStates();
    }

    private void OnClearSyncFilter(object sender, RoutedEventArgs e)
    {
        SyncSearchBox.Clear();
        SyncStatusFilter.SelectedIndex = 0;
        SyncSearchBox.Focus();
    }

    private void OnSyncRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source ||
            FindAncestor<Button>(source) is not null ||
            ItemsControl.ContainerFromElement(SyncGrid, source) is not DataGridRow { Item: SyncTaskRow row })
        {
            return;
        }

        e.Handled = true;
        EditSync(row);
    }

    private void OnSyncRowRightClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is { } row)
        {
            row.IsSelected = true;
            row.Focus();
        }
    }

    private void OnEditSync(object sender, RoutedEventArgs e)
    {
        if (SyncGrid.SelectedItem is SyncTaskRow row)
        {
            EditSync(row);
        }
    }

    private void OnEditSyncMenu(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SyncTaskRow row)
        {
            EditSync(row);
        }
    }

    private async void EditSync(SyncTaskRow row)
    {
        var dialog = new SyncTaskEditorWindow(row.Definition) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null)
        {
            return;
        }

        await RunUiActionAsync("正在保存同步任务", () => _runtime.UpdateSyncTaskAsync(dialog.Result));
        StatusText.Text = $"{dialog.Result.Name} 已更新";
        RefreshSyncFilter();
    }

    private async void OnDuplicateSyncMenu(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SyncTaskRow row)
        {
            return;
        }

        var dialog = new SyncTaskEditorWindow(row.Definition, duplicate: true) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null)
        {
            return;
        }

        await RunUiActionAsync("正在创建任务副本", () => _runtime.AddSyncTaskAsync(dialog.Result));
        StatusText.Text = $"{dialog.Result.Name} 已创建";
    }

    private async void OnRunSyncMenu(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SyncTaskRow row)
        {
            await RunSyncAsync(row);
        }
    }

    private void OnOpenLeftDirectory(object sender, RoutedEventArgs e) =>
        OpenTaskDirectory((sender as FrameworkElement)?.DataContext as SyncTaskRow, useLeftPath: true);

    private void OnOpenRightDirectory(object sender, RoutedEventArgs e) =>
        OpenTaskDirectory((sender as FrameworkElement)?.DataContext as SyncTaskRow, useLeftPath: false);

    private void OpenTaskDirectory(SyncTaskRow? row, bool useLeftPath)
    {
        if (row is null)
        {
            return;
        }

        var path = useLeftPath ? row.LeftPath : row.RightPath;
        if (!OpenDirectory(path))
        {
            RefreshSyncFilter();
        }
    }

    private bool OpenDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            ShowError($"目录不存在或当前不可访问：\n\n{path}");
            return false;
        }

        var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        startInfo.ArgumentList.Add(path);
        Process.Start(startInfo);
        return true;
    }

    private async void OnDeleteSyncMenu(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SyncTaskRow row)
        {
            await DeleteSyncAsync(row);
        }
    }

    private void OnGameSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DeleteGameButton.IsEnabled = GameGrid.SelectedItem is GameProfileRow;
    }

    private async void OnDeleteSync(object sender, RoutedEventArgs e)
    {
        if (SyncGrid.SelectedItem is not SyncTaskRow row)
        {
            return;
        }

        await DeleteSyncAsync(row);
    }

    private async Task DeleteSyncAsync(SyncTaskRow row)
    {
        var prompt = new PromptWindow(this, "删除同步任务", $"删除同步任务“{row.Name}”？\n\n已同步的文件不会被删除。", "删除", "取消", primaryIsDanger: true);
        prompt.ShowDialog();
        if (prompt.Choice != PromptChoice.Primary)
        {
            return;
        }

        await RunUiActionAsync("正在删除同步任务", () => _runtime.RemoveSyncTaskAsync(row.Definition.Id));
        StatusText.Text = "同步任务已删除";
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = current is FrameworkContentElement content
                ? content.Parent
                : VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private async void OnDeleteGame(object sender, RoutedEventArgs e)
    {
        if (GameGrid.SelectedItem is not GameProfileRow row)
        {
            return;
        }

        var prompt = new PromptWindow(this, "删除游戏", $"删除游戏“{row.Name}”？\n\n已有备份会保留，可通过“发现已有备份”重新导入。", "删除", "取消", primaryIsDanger: true);
        prompt.ShowDialog();
        if (prompt.Choice != PromptChoice.Primary)
        {
            return;
        }

        await RunUiActionAsync("正在删除游戏", () => _runtime.RemoveGameProfileAsync(row.Definition.Id));
        StatusText.Text = "游戏已删除，备份仍保留";
    }

    private async Task RunUiActionAsync(string status, Func<Task> action)
    {
        StatusText.Text = status;
        IsEnabled = false;
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            StatusText.Text = "操作失败";
            AppLogger.Error(status, exception);
            ShowError(exception.Message);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void ShowError(string message) =>
        new PromptWindow(this, "操作失败", message, "关闭").ShowDialog();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (System.Windows.Application.Current is App { IsExiting: false })
        {
            e.Cancel = true;
            Hide();
            StatusText.Text = "PathEcho 仍在后台监听";
        }
    }
}

public sealed class UpdateRouteRow
{
    public UpdateRouteRow(UpdateUrlRoute route)
    {
        IsDirect = route.IsDirect;
        BaseUrl = route.IsDirect ? "GitHub 官方地址" : route.BaseUrl ?? string.Empty;
        Priority = route.Priority;
    }

    public bool IsDirect { get; }
    public string Type => IsDirect ? "直连" : "URL 前缀";
    public string BaseUrl { get; set; }
    public int Priority { get; set; }
}
