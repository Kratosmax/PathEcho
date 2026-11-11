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
        CancellationToken cancellationToken = default,
        TimeSpan? stallTimeout = null,
        TimeSpan? routeTimeout = null)
    {
        var stallDeadline = stallTimeout ?? UpdateBodyReader.DefaultStallTimeout;
        var routeDeadline = routeTimeout ?? UpdateBodyReader.DefaultRouteTimeout;
        if (stallDeadline <= TimeSpan.Zero || routeDeadline <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(stallTimeout), "游戏目录的停滞与线路时限必须为正。");
        }

        var failures = new List<string>();
        foreach (var route in UpdateRoutePlanner.CreateRoutes(DefaultCatalogUri, options))
        {
            try
            {
                // 每条线路独立总截止：响应头与正文都受限。停滞或超时会释放当前流并切换下一线路；
                // 只有用户取消会立即终止整个获取流程。
                using var routeGuard = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                routeGuard.CancelAfter(routeDeadline);
                using var request = new HttpRequestMessage(HttpMethod.Get, route.RequestUri);
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    routeGuard.Token).ConfigureAwait(false);
                var responseUri = response.RequestMessage?.RequestUri ?? route.RequestUri;
                if (!UpdateRoutePlanner.IsAllowedResponseUri(route.RequestUri, responseUri))
                {
                    throw new InvalidDataException("游戏目录重定向到了不允许的域名。");
                }

                if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
                {
                    throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
                }

                if (response.Content.Headers.ContentLength is > MaximumCatalogBytes)
                {
                    throw new InvalidDataException("游戏目录超出大小限制。");
                }

                await using var source = await response.Content.ReadAsStreamAsync(routeGuard.Token).ConfigureAwait(false);
                var body = await UpdateBodyReader.ReadAsync(
                    source,
                    MaximumCatalogBytes,
                    stallDeadline,
                    "游戏目录超出大小限制。",
                    routeGuard.Token,
                    cancellationToken).ConfigureAwait(false);
                var json = Encoding.UTF8.GetString(body);
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
            catch (TaskCanceledException exception)
            {
                failures.Add($"{route.Name}：{exception.Message}");
            }
            catch (OperationCanceledException)
            {
                failures.Add($"{route.Name}：线路总时限超时。");
            }
            catch (TimeoutException exception)
            {
                failures.Add($"{route.Name}：{exception.Message}");
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException)
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
