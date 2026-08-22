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
        Configuration = await _configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (_previewSeed)
        {
            Configuration = Configuration with
            {
                SyncTasks = new[]
                {
                    new SyncTaskDefinition
                    {
                        Name = "截图与素材",
                        LeftPath = @"D:\Projects\Screenshots",
                        RightPath = @"E:\Mirror\Screenshots",
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
            _startup.SetEnabled(executable, Configuration.StartWithWindows);
            foreach (var task in Configuration.SyncTasks.Where(task => task.IsEnabled && task.StartWithApplication))
            {
                await StartSyncMonitorAsync(task, cancellationToken).ConfigureAwait(false);
            }

            foreach (var profile in Configuration.GameProfiles.Where(profile => profile.IsEnabled))
            {
                StartGameMonitor(profile);
            }
        }

        await RefreshHistoryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddSyncTaskAsync(SyncTaskDefinition task, CancellationToken cancellationToken = default)
    {
        task.Validate();
        Configuration = Configuration with
        {
            SyncTasks = Configuration.SyncTasks.Append(task).ToArray(),
        };
        await _configurationStore.SaveAsync(Configuration, cancellationToken).ConfigureAwait(false);
        SyncTasks.Add(new SyncTaskRow(task));
        if (!_previewMode && task.IsEnabled && task.StartWithApplication)
        {
            await StartSyncMonitorAsync(task, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task AddGameProfileAsync(GameBackupProfile profile, CancellationToken cancellationToken = default)
    {
        profile.Validate();
        Configuration = Configuration with
        {
            GameProfiles = Configuration.GameProfiles.Append(profile).ToArray(),
        };
        await _configurationStore.SaveAsync(Configuration, cancellationToken).ConfigureAwait(false);
        GameProfiles.Add(new GameProfileRow(profile));
        if (!_previewMode && profile.IsEnabled)
        {
            StartGameMonitor(profile);
        }
    }

    public async Task RemoveSyncTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        if (_syncMonitors.Remove(taskId, out var monitor))
        {
            await monitor.DisposeAsync().ConfigureAwait(false);
        }

        Configuration = Configuration with
        {
            SyncTasks = Configuration.SyncTasks.Where(task => task.Id != taskId).ToArray(),
        };
        await _configurationStore.SaveAsync(Configuration, cancellationToken).ConfigureAwait(false);
        var row = SyncTasks.FirstOrDefault(item => item.Definition.Id == taskId);
        if (row is not null)
        {
            SyncTasks.Remove(row);
        }
    }

    public async Task RemoveGameProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        if (_gameMonitors.Remove(profileId, out var monitor))
        {
            await monitor.DisposeAsync().ConfigureAwait(false);
        }

        Configuration = Configuration with
        {
            GameProfiles = Configuration.GameProfiles.Where(profile => profile.Id != profileId).ToArray(),
        };
        await _configurationStore.SaveAsync(Configuration, cancellationToken).ConfigureAwait(false);
        var row = GameProfiles.FirstOrDefault(item => item.Definition.Id == profileId);
        if (row is not null)
        {
            GameProfiles.Remove(row);
        }
    }

    public async Task<SyncRunResult> RunSyncNowAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var task = Configuration.SyncTasks.Single(item => item.Id == taskId);
        var row = SyncTasks.Single(item => item.Definition.Id == taskId);
        row.Status = "正在同步";
        try
        {
            var baseline = await _baselineStore.LoadAsync(task.Id, cancellationToken).ConfigureAwait(false);
            var engine = GetSyncEngine(task.Id);
            var result = await engine.RunAsync(task, baseline, true, cancellationToken).ConfigureAwait(false);
            await _baselineStore.SaveAsync(task.Id, result.Baseline, cancellationToken).ConfigureAwait(false);
            row.Status = FormatSyncResult(result);
            return result;
        }
        catch
        {
            row.Status = "同步失败";
            throw;
        }
    }

    public async Task<SnapshotCreationResult> BackupNowAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var profile = Configuration.GameProfiles.Single(item => item.Id == profileId);
        var row = GameProfiles.Single(item => item.Definition.Id == profileId);
        row.Status = "正在备份";
        try
        {
            var service = new GameBackupService(profile, Configuration.DefaultBackupDirectory, _snapshotStore);
            var result = await service.CreateAsync(BackupTrigger.None, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("手动备份被意外跳过。");
            row.Status = $"已备份 {result.FileCount} 个文件";
            await RefreshHistoryAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            row.Status = "备份失败";
            throw;
        }
    }

    public async Task<RestoreResult> RestoreAsync(RestoreRequest request, CancellationToken cancellationToken = default)
    {
        var service = new SnapshotRestoreService(new RestartManagerOccupancyService());
        return await service.RestoreAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveSettingsAsync(
        bool startWithWindows,
        bool startMinimized,
        bool checkForUpdates,
        string defaultBackupDirectory,
        UpdateNetworkOptions updateNetwork,
        CancellationToken cancellationToken = default)
    {
        var newBackupRoot = Path.GetFullPath(defaultBackupDirectory);
        var oldBackupRoot = Path.GetFullPath(Configuration.DefaultBackupDirectory);
        var movedProfiles = new List<Guid>();
        if (!string.Equals(oldBackupRoot, newBackupRoot, StringComparison.OrdinalIgnoreCase))
        {
            var manager = new BackupDirectoryManager();
            try
            {
                foreach (var profile in Configuration.GameProfiles.Where(profile => string.IsNullOrWhiteSpace(profile.BackupDirectory)))
                {
                    if (await manager.MoveProfileAsync(profile.Id, oldBackupRoot, newBackupRoot, cancellationToken).ConfigureAwait(false))
                    {
                        movedProfiles.Add(profile.Id);
                    }
                }
            }
            catch
            {
                foreach (var profileId in movedProfiles.AsEnumerable().Reverse())
                {
                    await manager.MoveProfileAsync(profileId, newBackupRoot, oldBackupRoot, cancellationToken).ConfigureAwait(false);
                }

                throw;
            }
        }

        Configuration = Configuration with
        {
            StartWithWindows = startWithWindows,
            StartMinimized = startMinimized,
            CheckForUpdates = checkForUpdates,
            DefaultBackupDirectory = newBackupRoot,
            UpdateNetwork = UpdateRoutePlanner.Normalize(updateNetwork),
        };
        await _configurationStore.SaveAsync(Configuration, cancellationToken).ConfigureAwait(false);
        if (!_previewMode)
        {
            _startup.SetEnabled(Environment.ProcessPath!, startWithWindows);
        }
    }

    public async Task RefreshHistoryAsync(CancellationToken cancellationToken = default)
    {
        var rows = new List<HistoryRow>();
        foreach (var profile in Configuration.GameProfiles)
        {
            var backupRoot = profile.ResolveBackupDirectory(Configuration.DefaultBackupDirectory);
            foreach (var version in await _snapshotStore.ListAsync(profile.Id, backupRoot, cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new HistoryRow(profile, version));
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
        foreach (var monitor in _syncMonitors.Values)
        {
            await monitor.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var monitor in _gameMonitors.Values)
        {
            await monitor.DisposeAsync().ConfigureAwait(false);
        }

        _syncMonitors.Clear();
        _gameMonitors.Clear();
    }

    private async Task StartSyncMonitorAsync(SyncTaskDefinition task, CancellationToken cancellationToken)
    {
        if (_syncMonitors.ContainsKey(task.Id))
        {
            return;
        }

        var monitor = new SyncTaskMonitor(task, GetSyncEngine(task.Id), _baselineStore);
        monitor.Synchronized += (_, result) => SetSyncStatus(task.Id, FormatSyncResult(result));
        monitor.SynchronizationFailed += (_, exception) => SetSyncStatus(task.Id, $"失败：{exception.Message}");
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

    private void StartGameMonitor(GameBackupProfile profile)
    {
        if (_gameMonitors.ContainsKey(profile.Id))
        {
            return;
        }

        var service = new GameBackupService(profile, Configuration.DefaultBackupDirectory, _snapshotStore);
        var monitor = new GameBackupMonitor(profile, service);
        monitor.BackupCreated += (_, result) =>
        {
            SetGameStatus(profile.Id, $"已备份 {result.FileCount} 个文件");
            _ = RefreshHistoryAsync();
        };
        monitor.BackupFailed += (_, exception) => SetGameStatus(profile.Id, $"失败：{exception.Message}");
        monitor.Start();
        _gameMonitors.Add(profile.Id, monitor);
        SetGameStatus(profile.Id, "监听中");
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

    private static string FormatSyncResult(SyncRunResult result) =>
        result.CopiedFiles == 0 && result.DeletedFiles == 0 && result.Conflicts == 0
            ? "已同步"
            : $"复制 {result.CopiedFiles} · 删除 {result.DeletedFiles} · 冲突 {result.Conflicts}";
}

public sealed class SyncTaskRow : NotifyObject
{
    private string _status = "等待启动";

    public SyncTaskRow(SyncTaskDefinition definition) => Definition = definition;

    public SyncTaskDefinition Definition { get; }
    public string Name => Definition.Name;
    public string LeftPath => Definition.LeftPath;
    public string RightPath => Definition.RightPath;
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
