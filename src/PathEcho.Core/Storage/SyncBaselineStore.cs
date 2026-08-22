using System.Text.Json;
using PathEcho.Core.Sync;

namespace PathEcho.Core.Storage;

public sealed class SyncBaselineStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _root;

    public SyncBaselineStore(string root)
    {
        _root = Path.GetFullPath(root);
    }

    public async Task<SyncBaseline> LoadAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var path = GetPath(taskId);
        if (!File.Exists(path))
        {
            return SyncBaseline.Empty;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        return await JsonSerializer.DeserializeAsync<SyncBaseline>(stream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false) ?? SyncBaseline.Empty;
    }

    public async Task SaveAsync(Guid taskId, SyncBaseline baseline, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_root);
        var path = GetPath(taskId);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(stream, baseline, SerializerOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private string GetPath(Guid taskId) => Path.Combine(_root, $"{taskId:N}.json");
}
