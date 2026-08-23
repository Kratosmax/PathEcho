using System.Net;
using System.Text;
using PathEcho.Core.Update;

namespace PathEcho.Core.GameCatalog;

public sealed record GameCatalogFetchResult(
    GameCatalogDocument Catalog,
    bool UsedCachedCopy,
    IReadOnlyList<string> RouteFailures);

public sealed class GameCatalogClient
{
    public static readonly Uri DefaultCatalogUri = new(
        "https://raw.githubusercontent.com/Kratosmax/PathEcho/main/config/game-catalog.json");

    private const int MaximumCatalogBytes = 2 * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly string _cachePath;
    private readonly string _publicKeyPem;

    public GameCatalogClient(HttpClient httpClient, string cachePath, string publicKeyPem = UpdateTrust.PublicKeyPem)
    {
        _httpClient = httpClient;
        _cachePath = Path.GetFullPath(cachePath);
        _publicKeyPem = publicKeyPem;
    }

    public async Task<GameCatalogFetchResult> FetchAsync(
        UpdateNetworkOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var failures = new List<string>();
        foreach (var route in UpdateRoutePlanner.CreateRoutes(DefaultCatalogUri, options))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, route.RequestUri);
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                var responseUri = response.RequestMessage?.RequestUri ?? route.RequestUri;
                if (!UpdateRoutePlanner.IsAllowedResponseUri(route.RequestUri, responseUri))
                {
                    throw new InvalidDataException("游戏目录重定向到了不允许的域名。");
                }

                if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
                {
                    throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
                }

                var json = await ReadLimitedAsync(response.Content, cancellationToken).ConfigureAwait(false);
                var catalog = GameCatalogVerifier.ParseAndVerify(json, _publicKeyPem);
                var cachedCatalog = await TryReadCacheAsync(cancellationToken).ConfigureAwait(false);
                if (cachedCatalog is not null && catalog.Revision < cachedCatalog.Revision)
                {
                    throw new InvalidDataException("游戏目录修订号低于本地可信缓存，已拒绝回退。");
                }

                await SaveCacheAsync(json, cancellationToken).ConfigureAwait(false);
                return new GameCatalogFetchResult(catalog, false, failures);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
            {
                failures.Add($"{route.Name}：{exception.Message}");
            }
        }

        try
        {
            var cachedJson = await File.ReadAllTextAsync(_cachePath, cancellationToken).ConfigureAwait(false);
            return new GameCatalogFetchResult(GameCatalogVerifier.ParseAndVerify(cachedJson, _publicKeyPem), true, failures);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            failures.Add($"本地可信缓存：{exception.Message}");
            throw new InvalidOperationException($"游戏规则获取失败，且没有可用的可信缓存。{string.Join(" ", failures)}", exception);
        }
    }

    private async Task<GameCatalogDocument?> TryReadCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(_cachePath, cancellationToken).ConfigureAwait(false);
            return GameCatalogVerifier.ParseAndVerify(json, _publicKeyPem);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    private static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumCatalogBytes)
        {
            throw new InvalidDataException("游戏目录超出大小限制。");
        }

        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var target = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (target.Length + read > MaximumCatalogBytes)
            {
                throw new InvalidDataException("游戏目录超出大小限制。");
            }

            target.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(target.ToArray());
    }

    private async Task SaveCacheAsync(string json, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_cachePath) ?? throw new InvalidOperationException("游戏目录缓存路径无效。");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_cachePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, _cachePath, true);
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
