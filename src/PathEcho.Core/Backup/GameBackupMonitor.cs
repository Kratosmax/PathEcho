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
    private readonly Channel<byte> _fileChanges = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Task> _workers = new();
    private readonly object _changesGate = new();
    private readonly HashSet<string> _changedPaths = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private bool _processWasRunning;
    // A new watcher cannot trust paths missed while it was stopped (including restore).
    private int _requiresFullSnapshot = 1;

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
            while (_fileChanges.Reader.TryRead(out _))
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken).ConfigureAwait(false);
            while (_fileChanges.Reader.TryRead(out _))
            {
            }

            string[] paths;
            lock (_changesGate)
            {
                paths = _changedPaths.ToArray();
                _changedPaths.Clear();
            }

            var forceFullSnapshot = Interlocked.Exchange(ref _requiresFullSnapshot, 0) != 0;
            if (paths.Length == 0 && !forceFullSnapshot)
            {
                // 只有唤醒信号而没有实际变化（例如变化已被尾随备份合并处理），不产生快照。
                continue;
            }

            var trigger = ResolveTrigger(forceFullSnapshot, paths);
            if (trigger != BackupTrigger.None)
            {
                var result = await TryCreateAsync(
                    trigger,
                    cancellationToken,
                    forceFullSnapshot || trigger == BackupTrigger.ImportantFileChanged
                        ? null
                        : paths).ConfigureAwait(false);
                if (result == BackupAttemptOutcome.Throttled)
                {
                    await RunTrailingBackupAsync(trigger, paths, forceFullSnapshot, cancellationToken).ConfigureAwait(false);
                }
                else if (result != BackupAttemptOutcome.Succeeded)
                {
                    // The failed batch is finished. Reconcile all paths on the next genuine event.
                    Interlocked.Exchange(ref _requiresFullSnapshot, 1);
                }
            }
        }
    }

    private async Task RunTrailingBackupAsync(
        BackupTrigger trigger,
        IReadOnlyCollection<string> throttledPaths,
        bool forceFullSnapshot,
        CancellationToken cancellationToken)
    {
        // 备份被最低间隔跳过时，等待窗口结束后补一次，避免最后一次变化永久丢失。
        var pendingPaths = new HashSet<string>(throttledPaths, StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var remaining = _service.GetChangeThrottleRemaining(DateTimeOffset.UtcNow);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();

            lock (_changesGate)
            {
                foreach (var path in _changedPaths)
                {
                    pendingPaths.Add(path);
                }
                _changedPaths.Clear();
            }

            forceFullSnapshot |= Interlocked.Exchange(ref _requiresFullSnapshot, 0) != 0;
            var trailingTrigger = ResolveTrigger(forceFullSnapshot, pendingPaths);
            if (trailingTrigger == BackupTrigger.None)
            {
                trailingTrigger = trigger;
            }

            var result = await TryCreateAsync(
                trailingTrigger,
                cancellationToken,
                forceFullSnapshot || trailingTrigger == BackupTrigger.ImportantFileChanged
                    ? null
                    : pendingPaths.ToArray()).ConfigureAwait(false);
            if (result != BackupAttemptOutcome.Throttled)
            {
                if (result != BackupAttemptOutcome.Succeeded)
                {
                    Interlocked.Exchange(ref _requiresFullSnapshot, 1);
                }

                return;
            }
        }
    }

    private BackupTrigger ResolveTrigger(bool forceFullSnapshot, IReadOnlyCollection<string> paths) =>
        _profile.Triggers.HasFlag(BackupTrigger.ImportantFileChanged) &&
        (forceFullSnapshot || paths.Any(IsImportantFile))
            ? BackupTrigger.ImportantFileChanged
            : _profile.Triggers.HasFlag(BackupTrigger.ChangedFiles)
                ? BackupTrigger.ChangedFiles
                : BackupTrigger.None;

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

    private enum BackupAttemptOutcome
    {
        Succeeded,
        Throttled,
        Failed,
        RetryStopped,
    }

    private async Task<BackupAttemptOutcome> TryCreateAsync(
        BackupTrigger trigger,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? changedPaths = null)
    {
        try
        {
            var result = await _service.CreateAsync(trigger, cancellationToken, changedPaths).ConfigureAwait(false);
            if (result is not null)
            {
                BackupCreated?.Invoke(this, result);
            }

            return result is null ? BackupAttemptOutcome.Throttled : BackupAttemptOutcome.Succeeded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (BackupRetryStoppedException exception)
        {
            BackupFailed?.Invoke(this, exception);
            return BackupAttemptOutcome.RetryStopped;
        }
        catch (Exception exception)
        {
            BackupFailed?.Invoke(this, exception);
            return BackupAttemptOutcome.Failed;
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

    private bool IsImportantFile(string relativePath)
    {
        try { return _importantPatterns.Any(pattern => pattern.IsMatch(relativePath)); }
        catch (RegexMatchTimeoutException exception)
        {
            // Conservatively include this change rather than silently losing the file worker.
            BackupFailed?.Invoke(this, exception);
            return true;
        }
    }

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
        TrackChange(eventArgs.FullPath);

    private void OnFileRenamed(object sender, RenamedEventArgs eventArgs)
    {
        TrackChange(eventArgs.OldFullPath);
        TrackChange(eventArgs.FullPath);
    }

    private void OnWatcherError(object sender, ErrorEventArgs eventArgs)
    {
        Interlocked.Exchange(ref _requiresFullSnapshot, 1);
        _fileChanges.Writer.TryWrite(0);
        BackupFailed?.Invoke(this, eventArgs.GetException());
    }

    private void TrackChange(string fullPath)
    {
        var relativePath = Path.GetRelativePath(_profile.SaveDirectory, fullPath);
        lock (_changesGate)
        {
            _changedPaths.Add(relativePath);
            if (_changedPaths.Count > 4096)
            {
                _changedPaths.Clear();
                Interlocked.Exchange(ref _requiresFullSnapshot, 1);
            }
        }

        _fileChanges.Writer.TryWrite(0);
    }
}
