using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using PathEcho.Core.Backup;
using PathEcho.Core.Models;
using PathEcho.Core.Restore;
using PathEcho.Core.Storage;
using PathEcho.Core.Sync;
using PathEcho.Core.Update;
using PathEcho.Platform.Windows.Restore;
using PathEcho.Platform.Windows.Startup;

namespace PathEcho.Services;

public sealed class PathEchoRuntime : IAsyncDisposable
{
    private readonly bool _previewMode;
    private readonly bool _previewSeed;
    private readonly string _dataRoot;
    private readonly JsonConfigurationStore _configurationStore;
    private readonly SyncBaselineStore _baselineStore;
    private readonly Dictionary<Guid, SyncTaskMonitor> _syncMonitors = new();
    private readonly Dictionary<Guid, GameBackupMonitor> _gameMonitors = new();
    private readonly Dictionary<Guid, SyncEngine> _syncEngines = new();
    private readonly Dictionary<Guid, SyncTaskRunner> _syncRunners = new();
    private readonly Dictionary<Guid, GameBackupService> _gameServices = new();
    private readonly SnapshotStore _snapshotStore = new();
    private readonly StartupRegistrationService _startup = new();

    public PathEchoRuntime(bool previewMode, bool previewSeed = false)
    {
        _previewMode = previewMode;
        _previewSeed = previewSeed;
        _dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PathEcho");
        _configurationStore = new JsonConfigurationStore(Path.Combine(_dataRoot, "configuration.json"));
        _baselineStore = new SyncBaselineStore(Path.Combine(_dataRoot, "state", "baselines"));
    }

    public AppConfiguration Configuration { get; private set; } = new();

    public bool IsPreviewMode => _previewMode;

    public ObservableCollection<SyncTaskRow> SyncTasks { get; } = new();

    public ObservableCollection<GameProfileRow> GameProfiles { get; } = new();

    public ObservableCollection<HistoryRow> History { get; } = new();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Configuration = _previewMode
            ? new AppConfiguration()
            : await _configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        AppLogger.Configure(Configuration.EnableDebugLogging);
        AppLogger.Debug("Configuration loaded; runtime initialization started.");
        if (_previewSeed)
        {
            Configuration = Configuration with
            {
                SyncTasks = new[]
                {
                    new SyncTaskDefinition
                    {
                        Name = "截图与素材",
                        LeftPath = @"D:\Projects\PathEcho\Design References\Screenshots\Current Iteration",
                        RightPath = @"E:\Archive\Creative Projects\PathEcho\Screenshots\Current Iteration",
                        DeletionMode = DeletionMode.BackupThenPropagate,
                    },
                    new SyncTaskDefinition
                    {
                        Name = "模组配置",
                        LeftPath = @"D:\Games\Mods",
                        RightPath = @"E:\Games\Mods",
                        Mode = SyncMode.Bidirectional,
                    },
                },
                GameProfiles = new[]
                {
                    new GameBackupProfile
                    {
                        Name = "示例游戏",
                        SaveDirectory = @"D:\Games\Example\Saves",
                        Triggers = BackupTrigger.ChangedFiles | BackupTrigger.ProcessExited,
                        ProcessExecutablePath = @"D:\Games\Example\Game.exe",
                    },
                },
            };
        }
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            foreach (var task in Configuration.SyncTasks)
            {
                SyncTasks.Add(new SyncTaskRow(task));
            }

            foreach (var profile in Configuration.GameProfiles)
            {
                GameProfiles.Add(new GameProfileRow(profile));
            }
        });

        if (!_previewMode)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法识别程序路径。");
            try
            {
                _startup.SetEnabled(executable, Configuration.StartWithWindows);
            }
            catch (Exception exception)
            {
                AppLogger.Error("Startup registration update failed during initialization.", exception);
            }

            foreach (var task in Configuration.SyncTasks.Where(task => task.IsEnabled && task.StartWithApplication))
            {
                await StartSyncMonitorSafelyAsync(task, cancellationToken).ConfigureAwait(false);
            }

            foreach (var profile in Configuration.GameProfiles.Where(profile => profile.IsEnabled))
            {
                await StartGameMonitorSafelyAsync(profile).ConfigureAwait(false);
            }
        }

        await RefreshHistoryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddSyncTaskAsync(SyncTaskDefinition task, CancellationToken cancellationToken = default)
    {
        task.Validate();
        var updatedConfiguration = Configuration with
        {
            SyncTasks = Configuration.SyncTasks.Append(task).ToArray(),
        };
        await PersistConfigurationAsync(updatedConfiguration, cancellationToken).ConfigureAwait(false);
        Configuration = updatedConfiguration;
        await InvokeOnUiAsync(() => SyncTasks.Add(new SyncTaskRow(task)));
        AppLogger.Debug($"Sync task created: {task.Id:N}.");
        if (!_previewMode && task.IsEnabled && task.StartWithApplication)
        {
            await StartSyncMonitorSafelyAsync(task, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task AddGameProfileAsync(GameBackupProfile profile, CancellationToken cancellationToken = default)
    {
        profile.Validate();
        var updatedConfiguration = Configuration with
        {
            GameProfiles = Configuration.GameProfiles.Append(profile).ToArray(),
        };
        await PersistConfigurationAsync(updatedConfiguration, cancellationToken).ConfigureAwait(false);
        Configuration = updatedConfiguration;
        await InvokeOnUiAsync(() => GameProfiles.Add(new GameProfileRow(profile)));
        AppLogger.Debug($"Game profile created: {profile.Id:N}.");
        if (!_previewMode && profile.IsEnabled)
        {
            await StartGameMonitorSafelyAsync(profile).ConfigureAwait(false);
        }
    }

    public async Task UpdateSyncTaskAsync(SyncTaskDefinition task, CancellationToken cancellationToken = default)
    {
        task.Validate();
        if (!Configuration.SyncTasks.Any(item => item.Id == task.Id))
        {
            throw new InvalidOperationException("要编辑的同步任务不存在。");
        }

        var updatedConfiguration = Configuration with
        {
            SyncTasks = Configuration.SyncTasks.Select(item => item.Id == task.Id ? task : item).ToArray(),
        };
        await PersistConfigurationAsync(updatedConfiguration, cancellationToken).ConfigureAwait(false);
        Configuration = updatedConfiguration;

        await StopSyncMonitorAsync(task.Id).ConfigureAwait(false);

        await InvokeOnUiAsync(() =>
        {
            var index = SyncTasks.ToList().FindIndex(item => item.Definition.Id == task.Id);
            if (index >= 0)
            {
                SyncTasks[index] = new SyncTaskRow(task);
            }
        });
        AppLogger.Debug($"Sync task updated: {task.Id:N}.");
        if (!_previewMode && task.IsEnabled && task.StartWithApplication)
        {
            await StartSyncMonitorSafelyAsync(task, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RemoveSyncTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var updatedConfiguration = Configuration with
        {
            SyncTasks = Configuration.SyncTasks.Where(task => task.Id != taskId).ToArray(),
        };
        await PersistConfigurationAsync(updatedConfiguration, cancellationToken).ConfigureAwait(false);
        Configuration = updatedConfiguration;
        await StopSyncMonitorAsync(taskId).ConfigureAwait(false);
        _syncRunners.Remove(taskId);
        _syncEngines.Remove(taskId);
        await InvokeOnUiAsync(() =>
        {
            var row = SyncTasks.FirstOrDefault(item => item.Definition.Id == taskId);
            if (row is not null)
            {
                SyncTasks.Remove(row);
            }
        });
    }

    public async Task RemoveGameProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var updatedConfiguration = Configuration with
        {
            GameProfiles = Configuration.GameProfiles.Where(profile => profile.Id != profileId).ToArray(),
        };
        await PersistConfigurationAsync(updatedConfiguration, cancellationToken).ConfigureAwait(false);
        Configuration = updatedConfiguration;
        await StopGameMonitorAsync(profileId).ConfigureAwait(false);
        _gameServices.Remove(profileId);
        await InvokeOnUiAsync(() =>
        {
            var row = GameProfiles.FirstOrDefault(item => item.Definition.Id == profileId);
            if (row is not null)
            {
                GameProfiles.Remove(row);
            }
        });
    }

    public async Task<SyncRunResult> RunSyncNowAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        EnsureNotPreview("预览模式不会执行真实目录同步。");
        var task = Configuration.SyncTasks.Single(item => item.Id == taskId);
        var row = SyncTasks.Single(item => item.Definition.Id == taskId);
        AppLogger.Debug($"Manual synchronization started for task {taskId:N}.");
        await InvokeOnUiAsync(() => row.Status = "正在同步");
        try
        {
            var result = await GetSyncRunner(task.Id).RunAsync(task, true, cancellationToken).ConfigureAwait(false);
            await InvokeOnUiAsync(() => row.Status = FormatSyncResult(result));
            AppLogger.Debug($"Manual synchronization completed for task {taskId:N}.");
            return result;
        }
        catch (Exception exception)
        {
            await InvokeOnUiAsync(() => row.Status = "同步失败");
            AppLogger.Error($"Manual synchronization failed for task {taskId:N}.", exception);
            throw;
        }
    }

    public async Task<SnapshotCreationResult> BackupNowAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        EnsureNotPreview("预览模式不会创建真实存档备份。");
        var profile = Configuration.GameProfiles.Single(item => item.Id == profileId);
        var row = GameProfiles.Single(item => item.Definition.Id == profileId);
        AppLogger.Debug($"Manual backup started for profile {profileId:N}.");
        await InvokeOnUiAsync(() => row.Status = "正在备份");
        try
        {
            var service = GetGameBackupService(profile);
            var result = await service.CreateAsync(BackupTrigger.None, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("手动备份被意外跳过。");
            await InvokeOnUiAsync(() => row.Status = $"已备份 {result.FileCount} 个文件");
            await RefreshHistoryAsync(cancellationToken).ConfigureAwait(false);
            AppLogger.Debug($"Manual backup completed for profile {profileId:N}.");
            return result;
        }
        catch (Exception exception)
        {
            await InvokeOnUiAsync(() => row.Status = "备份失败");
            AppLogger.Error($"Manual backup failed for profile {profileId:N}.", exception);
            throw;
        }
    }

    public async Task<RestoreResult> RestoreAsync(RestoreRequest request, CancellationToken cancellationToken = default)
    {
        EnsureNotPreview("预览模式不会执行真实存档回档。");
        var service = new SnapshotRestoreService(new RestartManagerOccupancyService());
        return await service.RestoreAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveSettingsAsync(
        bool startWithWindows,
        bool startMinimized,
        bool checkForUpdates,
        bool enableDebugLogging,
        string defaultBackupDirectory,
        UpdateNetworkOptions updateNetwork,
        CancellationToken cancellationToken = default)
    {
        var newBackupRoot = Path.GetFullPath(defaultBackupDirectory);
        var oldBackupRoot = Path.GetFullPath(Configuration.DefaultBackupDirectory);
        var backupRootChanged = !string.Equals(oldBackupRoot, newBackupRoot, StringComparison.OrdinalIgnoreCase);
        var previousConfiguration = Configuration;
        var updatedConfiguration = Configuration with
        {
            StartWithWindows = startWithWindows,
            StartMinimized = startMinimized,
            CheckForUpdates = checkForUpdates,
            EnableDebugLogging = enableDebugLogging,
            DefaultBackupDirectory = newBackupRoot,
            UpdateNetwork = UpdateRoutePlanner.Normalize(updateNetwork),
        };
        var movedProfiles = new List<Guid>();
        var manager = new BackupDirectoryManager();
        if (!_previewMode && backupRootChanged)
        {
            await StopAllGameMonitorsAsync().ConfigureAwait(false);
        }

        try
        {
            if (!_previewMode && backupRootChanged)
            {
                foreach (var profile in Configuration.GameProfiles.Where(profile => string.IsNullOrWhiteSpace(profile.BackupDirectory)))
                {
                    if (await manager.MoveProfileAsync(profile.Id, oldBackupRoot, newBackupRoot, cancellationToken).ConfigureAwait(false))
                    {
                        movedProfiles.Add(profile.Id);
                    }
                }
            }

            await PersistConfigurationAsync(updatedConfiguration, cancellationToken).ConfigureAwait(false);
            Configuration = updatedConfiguration;
        }
        catch (Exception exception)
        {
            Exception? rollbackException = null;
            try
            {
                foreach (var profileId in movedProfiles.AsEnumerable().Reverse())
                {
                    await manager.MoveProfileAsync(profileId, newBackupRoot, oldBackupRoot, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception rollbackFailure)
            {
                rollbackException = rollbackFailure;
            }

            Configuration = previousConfiguration;
            if (!_previewMode && backupRootChanged)
            {
                await StartAllGameMonitorsSafelyAsync().ConfigureAwait(false);
            }

            if (rollbackException is not null)
            {
                throw new AggregateException("保存设置失败，且备份目录回滚未完整完成。", exception, rollbackException);
            }

            throw;
        }

        if (!_previewMode)
        {
            AppLogger.Configure(enableDebugLogging);
            try
            {
                _startup.SetEnabled(Environment.ProcessPath!, startWithWindows);
            }
            catch (Exception exception)
            {
                AppLogger.Error("Startup registration update failed after saving settings.", exception);
            }

            if (backupRootChanged)
            {
                _gameServices.Clear();
                await StartAllGameMonitorsSafelyAsync().ConfigureAwait(false);
                await RefreshHistorySafelyAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task RefreshHistoryAsync(CancellationToken cancellationToken = default)
    {
        if (_previewMode)
        {
            await InvokeOnUiAsync(History.Clear);
            return;
        }

        var rows = new List<HistoryRow>();
        foreach (var profile in Configuration.GameProfiles)
        {
            var backupRoot = profile.ResolveBackupDirectory(Configuration.DefaultBackupDirectory);
            try
            {
                foreach (var version in await _snapshotStore.ListAsync(profile.Id, backupRoot, cancellationToken).ConfigureAwait(false))
                {
                    rows.Add(new HistoryRow(profile, version));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                AppLogger.Error($"Backup history load failed for profile {profile.Id:N}.", exception);
                SetGameStatus(profile.Id, "历史读取失败");
            }
        }

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            History.Clear();
            foreach (var row in rows.OrderByDescending(row => row.CreatedAtUtc))
            {
                History.Add(row);
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var taskId in _syncMonitors.Keys.ToArray())
        {
            await StopSyncMonitorAsync(taskId).ConfigureAwait(false);
        }

        foreach (var profileId in _gameMonitors.Keys.ToArray())
        {
            await StopGameMonitorAsync(profileId).ConfigureAwait(false);
        }

        _syncRunners.Clear();
        _syncEngines.Clear();
        _gameServices.Clear();
    }

    private async Task StartSyncMonitorAsync(SyncTaskDefinition task, CancellationToken cancellationToken)
    {
        if (_syncMonitors.ContainsKey(task.Id))
        {
            return;
        }

        var monitor = new SyncTaskMonitor(task, GetSyncRunner(task.Id));
        monitor.Synchronized += (_, result) => SetSyncStatus(task.Id, FormatSyncResult(result));
        monitor.SynchronizationFailed += (_, exception) =>
        {
            AppLogger.Error($"Automatic synchronization failed for task {task.Id:N}.", exception);
            SetSyncStatus(task.Id, $"失败：{exception.Message}");
        };
        _syncMonitors.Add(task.Id, monitor);
        try
        {
            await monitor.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _syncMonitors.Remove(task.Id);
            await monitor.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task StartSyncMonitorSafelyAsync(SyncTaskDefinition task, CancellationToken cancellationToken)
    {
        try
        {
            await StartSyncMonitorAsync(task, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLogger.Error($"Sync monitor startup failed for task {task.Id:N}.", exception);
            SetSyncStatus(task.Id, $"监听失败：{exception.Message}");
        }
    }

    private async Task StopSyncMonitorAsync(Guid taskId)
    {
        if (!_syncMonitors.Remove(taskId, out var monitor))
        {
            return;
        }

        try
        {
            await monitor.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AppLogger.Error($"Sync monitor shutdown failed for task {taskId:N}.", exception);
        }
    }

    private async Task StartGameMonitorAsync(GameBackupProfile profile)
    {
        if (_gameMonitors.ContainsKey(profile.Id))
        {
            return;
        }

        var service = GetGameBackupService(profile);
        var monitor = new GameBackupMonitor(profile, service);
        monitor.BackupCreated += (_, result) =>
        {
            SetGameStatus(profile.Id, $"已备份 {result.FileCount} 个文件");
            _ = RefreshHistorySafelyAsync();
        };
        monitor.BackupFailed += (_, exception) =>
        {
            AppLogger.Error($"Automatic backup failed for profile {profile.Id:N}.", exception);
            SetGameStatus(profile.Id, $"失败：{exception.Message}");
        };
        try
        {
            monitor.Start();
        }
        catch
        {
            await monitor.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _gameMonitors.Add(profile.Id, monitor);
        SetGameStatus(profile.Id, "监听中");
    }

    private async Task StartGameMonitorSafelyAsync(GameBackupProfile profile)
    {
        try
        {
            await StartGameMonitorAsync(profile).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AppLogger.Error($"Game backup monitor startup failed for profile {profile.Id:N}.", exception);
            SetGameStatus(profile.Id, $"监听失败：{exception.Message}");
        }
    }

    private async Task StartAllGameMonitorsSafelyAsync()
    {
        foreach (var profile in Configuration.GameProfiles.Where(profile => profile.IsEnabled))
        {
            await StartGameMonitorSafelyAsync(profile).ConfigureAwait(false);
        }
    }

    private async Task StopAllGameMonitorsAsync()
    {
        foreach (var profileId in _gameMonitors.Keys.ToArray())
        {
            await StopGameMonitorAsync(profileId).ConfigureAwait(false);
        }
    }

    private async Task StopGameMonitorAsync(Guid profileId)
    {
        if (!_gameMonitors.Remove(profileId, out var monitor))
        {
            return;
        }

        try
        {
            await monitor.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AppLogger.Error($"Game backup monitor shutdown failed for profile {profileId:N}.", exception);
        }
    }

    private SyncEngine GetSyncEngine(Guid taskId)
    {
        if (_syncEngines.TryGetValue(taskId, out var engine))
        {
            return engine;
        }

        engine = new SyncEngine(Path.Combine(_dataRoot, "deletion-vault"));
        _syncEngines.Add(taskId, engine);
        return engine;
    }

    private SyncTaskRunner GetSyncRunner(Guid taskId)
    {
        if (_syncRunners.TryGetValue(taskId, out var runner))
        {
            return runner;
        }

        runner = new SyncTaskRunner(GetSyncEngine(taskId), _baselineStore);
        _syncRunners.Add(taskId, runner);
        return runner;
    }

    private GameBackupService GetGameBackupService(GameBackupProfile profile)
    {
        if (_gameServices.TryGetValue(profile.Id, out var service))
        {
            return service;
        }

        service = new GameBackupService(profile, Configuration.DefaultBackupDirectory, new SnapshotStore());
        _gameServices.Add(profile.Id, service);
        return service;
    }

    private async Task RefreshHistorySafelyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await RefreshHistoryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLogger.Error("Background backup history refresh failed.", exception);
        }
    }

    private void SetSyncStatus(Guid taskId, string status) => System.Windows.Application.Current.Dispatcher.Invoke(() =>
    {
        var row = SyncTasks.FirstOrDefault(item => item.Definition.Id == taskId);
        if (row is not null)
        {
            row.Status = status;
        }
    });

    private void SetGameStatus(Guid profileId, string status) => System.Windows.Application.Current.Dispatcher.Invoke(() =>
    {
        var row = GameProfiles.FirstOrDefault(item => item.Definition.Id == profileId);
        if (row is not null)
        {
            row.Status = status;
        }
    });

    private static Task InvokeOnUiAsync(Action action) =>
        System.Windows.Application.Current.Dispatcher.InvokeAsync(action).Task;

    private Task PersistConfigurationAsync(CancellationToken cancellationToken) =>
        PersistConfigurationAsync(Configuration, cancellationToken);

    private Task PersistConfigurationAsync(AppConfiguration configuration, CancellationToken cancellationToken) =>
        _previewMode ? Task.CompletedTask : _configurationStore.SaveAsync(configuration, cancellationToken);

    private void EnsureNotPreview(string message)
    {
        if (_previewMode)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static string FormatSyncResult(SyncRunResult result) =>
        result.CopiedFiles == 0 && result.DeletedFiles == 0 && result.Conflicts == 0
            ? "已同步"
            : $"复制 {result.CopiedFiles} · 删除 {result.DeletedFiles} · 冲突 {result.Conflicts}";
}

public sealed class SyncTaskRow : NotifyObject
{
    private string _status = "等待启动";
    private readonly bool _leftDirectoryAvailable;
    private readonly bool _rightDirectoryAvailable;

    public SyncTaskRow(SyncTaskDefinition definition)
    {
        Definition = definition;
        _leftDirectoryAvailable = Directory.Exists(definition.LeftPath);
        _rightDirectoryAvailable = Directory.Exists(definition.RightPath);
    }

    public SyncTaskDefinition Definition { get; }
    public string Name => Definition.Name;
    public string LeftPath => Definition.LeftPath;
    public string RightPath => Definition.RightPath;
    public bool HasDirectoryIssue => !_leftDirectoryAvailable || !_rightDirectoryAvailable;
    public string DirectoryState => HasDirectoryIssue ? "需要检查" : "可用";
    public string DirectoryStateDetail => (!_leftDirectoryAvailable, !_rightDirectoryAvailable) switch
    {
        (true, true) => "源目录和目标目录均不可用",
        (true, false) => "源目录不可用",
        (false, true) => "目标目录不可用",
        _ => "源目录和目标目录均可用",
    };
    public string Mode => Definition.Mode switch
    {
        SyncMode.LeftToRight => "左 → 右",
        SyncMode.RightToLeft => "右 → 左",
        _ => "双向",
    };
    public string Deletion => Definition.DeletionMode switch
    {
        DeletionMode.Ignore => "不传播删除",
        DeletionMode.Propagate => "传播删除",
        _ => "删除前备份",
    };

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }
}

public sealed class GameProfileRow : NotifyObject
{
    private string _status = "等待启动";

    public GameProfileRow(GameBackupProfile definition) => Definition = definition;

    public GameBackupProfile Definition { get; }
    public string Name => Definition.Name;
    public string SaveDirectory => Definition.SaveDirectory;
    public int RetainedVersions => Definition.RetainedVersions;
    public string Triggers => string.Join("、", new[]
    {
        Definition.Triggers.HasFlag(BackupTrigger.Scheduled) ? "定时" : null,
        Definition.Triggers.HasFlag(BackupTrigger.ImportantFileChanged) ? "重点文件" : null,
        Definition.Triggers.HasFlag(BackupTrigger.ChangedFiles) ? "文件变化" : null,
        Definition.Triggers.HasFlag(BackupTrigger.ProcessExited) ? "游戏退出" : null,
    }.Where(value => value is not null));

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }
}

public sealed class HistoryRow
{
    public HistoryRow(GameBackupProfile profile, SnapshotVersion version)
    {
        Profile = profile;
        Version = version;
    }

    public GameBackupProfile Profile { get; }
    public SnapshotVersion Version { get; }
    public string GameName => Profile.Name;
    public DateTimeOffset CreatedAtUtc => Version.Manifest.CreatedAtUtc;
    public string CreatedAt => CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string Trigger => Version.Manifest.Trigger;
    public int FileCount => Version.Manifest.Files.Count;
    public string SnapshotDirectory => Version.SnapshotDirectory;
}

public abstract class NotifyObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
