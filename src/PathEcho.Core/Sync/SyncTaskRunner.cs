using PathEcho.Core.Models;
using PathEcho.Core.Storage;

namespace PathEcho.Core.Sync;

public sealed class SyncTaskRunner
{
    private readonly Func<Guid, CancellationToken, Task<SyncBaseline>> _loadBaseline;
    private readonly Func<Guid, SyncBaseline, CancellationToken, Task> _saveBaseline;
    private readonly Func<SyncTaskDefinition, SyncBaseline, bool, CancellationToken, Task<SyncRunResult>> _run;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SyncTaskRunner(SyncEngine engine, SyncBaselineStore baselineStore)
        : this(
            baselineStore.LoadAsync,
            baselineStore.SaveAsync,
            engine.RunAsync)
    {
    }

    internal SyncTaskRunner(
        Func<Guid, CancellationToken, Task<SyncBaseline>> loadBaseline,
        Func<Guid, SyncBaseline, CancellationToken, Task> saveBaseline,
        Func<SyncTaskDefinition, SyncBaseline, bool, CancellationToken, Task<SyncRunResult>> run)
    {
        _loadBaseline = loadBaseline;
        _saveBaseline = saveBaseline;
        _run = run;
    }

    public async Task<SyncRunResult> RunAsync(
        SyncTaskDefinition task,
        bool forceFullScan,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var baseline = await _loadBaseline(task.Id, cancellationToken).ConfigureAwait(false);
            var result = await _run(task, baseline, forceFullScan, cancellationToken).ConfigureAwait(false);
            await _saveBaseline(task.Id, result.Baseline, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }
}
