using System.Text.Json;
using PathEcho.Core.Models;

namespace PathEcho.Core.Storage;

public sealed class JsonConfigurationStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _path;

    public JsonConfigurationStore(string path)
    {
        _path = Path.GetFullPath(path);
    }

    public async Task<AppConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return new AppConfiguration();
        }

        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        return await JsonSerializer.DeserializeAsync<AppConfiguration>(stream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false) ?? new AppConfiguration();
    }

    public async Task SaveAsync(AppConfiguration configuration, CancellationToken cancellationToken = default) =>
        _ = await SaveAndVerifyAsync(configuration, cancellationToken).ConfigureAwait(false);

    public async Task<AppConfiguration> SaveAndVerifyAsync(
        AppConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("配置路径缺少父目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(stream, configuration, SerializerOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            var verified = await LoadFileAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                    JsonSerializer.Serialize(configuration, SerializerOptions),
                    JsonSerializer.Serialize(verified, SerializerOptions),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("配置写入校验失败，未替换原配置。");
            }

            File.Move(temporaryPath, _path, true);
            return verified;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task<AppConfiguration> LoadFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        return await JsonSerializer.DeserializeAsync<AppConfiguration>(stream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("配置内容为空。");
    }
}
