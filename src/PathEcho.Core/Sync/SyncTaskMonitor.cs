using System.Threading.Channels;
using PathEcho.Core.Models;
using PathEcho.Core.Storage;

namespace PathEcho.Core.Sync;

public sealed class SyncTaskMonitor : IAsyncDisposable
{
    private readonly SyncTaskDefinition _task;
    private readonly SyncEngine _engine;
    private readonly SyncBaselineStore _baselineStore;
    private readonly TimeSpan _debounce;
    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private Task? _worker;

    public SyncTaskMonitor(
        SyncTaskDefinition task,
        SyncEngine engine,
        SyncBaselineStore baselineStore,
        TimeSpan? debounce = null)
    {
        _task = task;
        _engine = engine;
        _baselineStore = baselineStore;
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
        var baseline = await _baselineStore.LoadAsync(_task.Id, cancellationToken).ConfigureAwait(false);
        var initial = await _engine.RunAsync(_task, baseline, true, cancellationToken).ConfigureAwait(false);
        await _baselineStore.SaveAsync(_task.Id, initial.Baseline, cancellationToken).ConfigureAwait(false);

        foreach (var root in GetWatchedRoots())
        {
            var watcher = CreateWatcher(root);
            _watchers.Add(watcher);
            watcher.EnableRaisingEvents = true;
        }

        _worker = RunWorkerAsync(initial.Baseline, _stopping.Token);
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

    private async Task RunWorkerAsync(SyncBaseline baseline, CancellationToken cancellationToken)
    {
        while (await _signals.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var forceFullScan = false;
            while (_signals.Reader.TryRead(out var force))
            {
                forceFullScan |= force;
            }

            await Task.Delay(_debounce, cancellationToken).ConfigureAwait(false);
            while (_signals.Reader.TryRead(out var force))
            {
                forceFullScan |= force;
            }

            try
            {
                var result = await _engine.RunAsync(_task, baseline, forceFullScan, cancellationToken).ConfigureAwait(false);
                await _baselineStore.SaveAsync(_task.Id, result.Baseline, cancellationToken).ConfigureAwait(false);
                baseline = result.Baseline;
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
        if (_task.Mode is SyncMode.LeftToRight or SyncMode.Bidirectional)
        {
            yield return SyncTaskDefinition.NormalizeRoot(_task.LeftPath);
        }

        if (_task.Mode is SyncMode.RightToLeft or SyncMode.Bidirectional)
        {
            yield return SyncTaskDefinition.NormalizeRoot(_task.RightPath);
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs eventArgs) => _signals.Writer.TryWrite(false);

    private void OnRenamed(object sender, RenamedEventArgs eventArgs) => _signals.Writer.TryWrite(false);

    private void OnError(object sender, ErrorEventArgs eventArgs) => _signals.Writer.TryWrite(true);
}
