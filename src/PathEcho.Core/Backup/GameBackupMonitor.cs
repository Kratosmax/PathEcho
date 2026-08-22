using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using PathEcho.Core.Models;

namespace PathEcho.Core.Backup;

public sealed class GameBackupMonitor : IAsyncDisposable
{
    private readonly GameBackupProfile _profile;
    private readonly GameBackupService _service;
    private readonly Regex[] _importantPatterns;
    private readonly Channel<string> _fileChanges = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Task> _workers = new();
    private FileSystemWatcher? _watcher;
    private bool _processWasRunning;

    public GameBackupMonitor(GameBackupProfile profile, GameBackupService service)
    {
        profile.Validate();
        _profile = profile;
        _service = service;
        _importantPatterns = profile.ImportantFilePatterns
            .Select(pattern => new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            .ToArray();
    }

    public event EventHandler<SnapshotCreationResult>? BackupCreated;

    public event EventHandler<Exception>? BackupFailed;

    public void Start()
    {
        if (_workers.Count > 0)
        {
            throw new InvalidOperationException("游戏备份监听已经启动。");
        }

        Directory.CreateDirectory(Path.GetFullPath(_profile.SaveDirectory));
        if (_profile.Triggers.HasFlag(BackupTrigger.ImportantFileChanged) ||
            _profile.Triggers.HasFlag(BackupTrigger.ChangedFiles))
        {
            _watcher = CreateWatcher();
            _watcher.EnableRaisingEvents = true;
            _workers.Add(RunFileChangesAsync(_stopping.Token));
        }

        if (_profile.Triggers.HasFlag(BackupTrigger.Scheduled))
        {
            _workers.Add(RunScheduleAsync(_stopping.Token));
        }

        if (_profile.Triggers.HasFlag(BackupTrigger.ProcessExited))
        {
            _processWasRunning = HasMatchingProcess();
            _workers.Add(RunProcessPollingAsync(_stopping.Token));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _watcher?.Dispose();
        _fileChanges.Writer.TryComplete();
        try
        {
            await Task.WhenAll(_workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }

        _stopping.Dispose();
    }

    private async Task RunFileChangesAsync(CancellationToken cancellationToken)
    {
        while (await _fileChanges.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (_fileChanges.Reader.TryRead(out var path))
            {
                paths.Add(path);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken).ConfigureAwait(false);
            while (_fileChanges.Reader.TryRead(out var path))
            {
                paths.Add(path);
            }

            var trigger = _profile.Triggers.HasFlag(BackupTrigger.ImportantFileChanged) &&
                paths.Any(IsImportantFile)
                    ? BackupTrigger.ImportantFileChanged
                    : _profile.Triggers.HasFlag(BackupTrigger.ChangedFiles)
                        ? BackupTrigger.ChangedFiles
                        : BackupTrigger.None;
            if (trigger != BackupTrigger.None)
            {
                await TryCreateAsync(trigger, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RunScheduleAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_profile.ScheduleInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await TryCreateAsync(BackupTrigger.Scheduled, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunProcessPollingAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var running = HasMatchingProcess();
            if (_processWasRunning && !running)
            {
                await TryCreateAsync(BackupTrigger.ProcessExited, cancellationToken).ConfigureAwait(false);
            }

            _processWasRunning = running;
        }
    }

    private async Task TryCreateAsync(BackupTrigger trigger, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _service.CreateAsync(trigger, cancellationToken).ConfigureAwait(false);
            if (result is not null)
            {
                BackupCreated?.Invoke(this, result);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            BackupFailed?.Invoke(this, exception);
        }
    }

    private FileSystemWatcher CreateWatcher()
    {
        var root = Path.GetFullPath(_profile.SaveDirectory);
        var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 16 * 1024,
            NotifyFilter = NotifyFilters.FileName |
                NotifyFilters.DirectoryName |
                NotifyFilters.LastWrite |
                NotifyFilters.Size |
                NotifyFilters.CreationTime,
        };
        watcher.Changed += OnFileChanged;
        watcher.Created += OnFileChanged;
        watcher.Deleted += OnFileChanged;
        watcher.Renamed += OnFileRenamed;
        watcher.Error += OnWatcherError;
        return watcher;
    }

    private bool IsImportantFile(string relativePath) =>
        _importantPatterns.Any(pattern => pattern.IsMatch(relativePath));

    private bool HasMatchingProcess()
    {
        var executablePath = Path.GetFullPath(_profile.ProcessExecutablePath!);
        var processName = Path.GetFileNameWithoutExtension(executablePath);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, executablePath, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                }
            }
        }

        return false;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs eventArgs) =>
        _fileChanges.Writer.TryWrite(Path.GetRelativePath(_profile.SaveDirectory, eventArgs.FullPath));

    private void OnFileRenamed(object sender, RenamedEventArgs eventArgs) =>
        _fileChanges.Writer.TryWrite(Path.GetRelativePath(_profile.SaveDirectory, eventArgs.FullPath));

    private void OnWatcherError(object sender, ErrorEventArgs eventArgs) =>
        BackupFailed?.Invoke(this, eventArgs.GetException());
}
