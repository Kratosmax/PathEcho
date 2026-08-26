using System.Threading.Channels;
using PathEcho.Core.Models;
using PathEcho.Core.Storage;

namespace PathEcho.Core.Sync;

public sealed class SyncTaskMonitor : IAsyncDisposable
{
    private readonly SyncTaskDefinition _task;
    private readonly SyncTaskRunner _runner;
    private readonly TimeSpan _debounce;
    private readonly Channel<byte> _signals = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly object _changesGate = new();
    private readonly HashSet<string> _changedPaths = new(StringComparer.OrdinalIgnoreCase);
    private int _requiresFullScan;
    private Task? _worker;

    public SyncTaskMonitor(
        SyncTaskDefinition task,
        SyncEngine engine,
        SyncBaselineStore baselineStore,
        TimeSpan? debounce = null)
        : this(task, new SyncTaskRunner(engine, baselineStore), debounce)
    {
    }

    public SyncTaskMonitor(
        SyncTaskDefinition task,
        SyncTaskRunner runner,
        TimeSpan? debounce = null)
    {
        _task = task;
        _runner = runner;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(650);
    }

    public event EventHandler<SyncRunResult>? Synchronized;

    public event EventHandler<Exception>? SynchronizationFailed;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_worker is not null)
        {
            throw new InvalidOperationException("同步监听已经启动。");
        }

        _task.Validate();
        foreach (var root in GetWatchedRoots())
        {
            var watcher = CreateWatcher(root);
            _watchers.Add(watcher);
            watcher.EnableRaisingEvents = true;
        }

        _worker = RunWorkerAsync(_stopping.Token);
        var initial = await _runner.RunAsync(_task, true, cancellationToken).ConfigureAwait(false);
        Synchronized?.Invoke(this, initial);
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _watchers.Clear();
        _signals.Writer.TryComplete();
        if (_worker is not null)
        {
            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
            }
        }

        _stopping.Dispose();
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        while (await _signals.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_signals.Reader.TryRead(out _))
            {
            }

            await Task.Delay(_debounce, cancellationToken).ConfigureAwait(false);
            while (_signals.Reader.TryRead(out _))
            {
            }

            string[] changedPaths;
            lock (_changesGate)
            {
                changedPaths = _changedPaths.ToArray();
                _changedPaths.Clear();
            }

            var forceFullScan = Interlocked.Exchange(ref _requiresFullScan, 0) != 0;
            if (!forceFullScan)
            {
                _runner.InvalidatePaths(_task, changedPaths);
            }

            try
            {
                var result = await _runner.RunAsync(_task, forceFullScan, cancellationToken).ConfigureAwait(false);
                Synchronized?.Invoke(this, result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                SynchronizationFailed?.Invoke(this, exception);
            }
        }
    }

    private FileSystemWatcher CreateWatcher(string root)
    {
        Directory.CreateDirectory(root);
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
        watcher.Changed += OnChanged;
        watcher.Created += OnChanged;
        watcher.Deleted += OnChanged;
        watcher.Renamed += OnRenamed;
        watcher.Error += OnError;
        return watcher;
    }

    private IEnumerable<string> GetWatchedRoots()
    {
        yield return SyncTaskDefinition.NormalizeRoot(_task.LeftPath);
        yield return SyncTaskDefinition.NormalizeRoot(_task.RightPath);
    }

    private void OnChanged(object sender, FileSystemEventArgs eventArgs) => TrackChange(eventArgs.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs eventArgs)
    {
        TrackChange(eventArgs.OldFullPath);
        TrackChange(eventArgs.FullPath);
    }

    private void OnError(object sender, ErrorEventArgs eventArgs)
    {
        Interlocked.Exchange(ref _requiresFullScan, 1);
        _signals.Writer.TryWrite(0);
    }

    private void TrackChange(string path)
    {
        lock (_changesGate)
        {
            _changedPaths.Add(Path.GetFullPath(path));
            if (_changedPaths.Count > 4096)
            {
                _changedPaths.Clear();
                Interlocked.Exchange(ref _requiresFullScan, 1);
            }
        }

        _signals.Writer.TryWrite(0);
    }
}
