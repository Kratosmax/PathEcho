using System.Text.Json;

namespace PathEcho.Core.Storage;

public sealed record SyncRunRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid TaskId { get; init; }

    public string TaskName { get; init; } = string.Empty;

    public string Trigger { get; init; } = "手动";

    public DateTimeOffset StartedAtUtc { get; init; }

    public DateTimeOffset CompletedAtUtc { get; init; }

    public bool Succeeded { get; init; }

    public int CopiedFiles { get; init; }

    public int DeletedFiles { get; init; }

    public int Conflicts { get; init; }

    public string? Error { get; init; }
}

public sealed class SyncRunHistoryStore
{
    private const int MaximumRecords = 200;
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;

    public SyncRunHistoryStore(string path) => _path = Path.GetFullPath(path);

    public async Task<IReadOnlyList<SyncRunRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AppendAsync(SyncRunRecord record, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = (await LoadCoreAsync(cancellationToken).ConfigureAwait(false))
                .Prepend(record)
                .Take(MaximumRecords)
                .ToArray();
            await SaveCoreAsync(records, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<SyncRunRecord>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return Array.Empty<SyncRunRecord>();
        }

        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        return await JsonSerializer.DeserializeAsync<SyncRunRecord[]>(stream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false) ?? Array.Empty<SyncRunRecord>();
    }

    private async Task SaveCoreAsync(IReadOnlyList<SyncRunRecord> records, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("运行历史路径缺少父目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(stream, records, SerializerOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
