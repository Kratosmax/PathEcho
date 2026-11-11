using System.Net;

namespace PathEcho.Core.Update;

public sealed class UpdateManifestClient(HttpClient httpClient)
{
    private const int MaximumManifestBytes = 256 * 1024;

    public async Task<UpdateManifest> FetchAsync(
        Uri originalUri,
        string expectedChannel,
        UpdateNetworkOptions? options = null,
        CancellationToken cancellationToken = default,
        TimeSpan? stallTimeout = null,
        TimeSpan? routeTimeout = null)
    {
        var stallDeadline = stallTimeout ?? UpdateBodyReader.DefaultStallTimeout;
        var routeDeadline = routeTimeout ?? UpdateBodyReader.DefaultRouteTimeout;
        if (stallDeadline <= TimeSpan.Zero || routeDeadline <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(stallTimeout), "更新清单的停滞与线路时限必须为正。");
        }

        var failures = new List<string>();
        foreach (var route in UpdateRoutePlanner.CreateRoutes(originalUri, options))
        {
            try
            {
                // 每条线路独立总截止：响应头与正文都受限。停滞或超时会释放当前流并切换下一线路；
                // 只有用户取消会立即终止整个获取流程。
                using var routeGuard = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                routeGuard.CancelAfter(routeDeadline);
                using var request = new HttpRequestMessage(HttpMethod.Get, route.RequestUri);
                using var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    routeGuard.Token).ConfigureAwait(false);
                var responseUri = response.RequestMessage?.RequestUri ?? route.RequestUri;
                if (!UpdateRoutePlanner.IsAllowedResponseUri(route.RequestUri, responseUri))
                {
                    throw new InvalidDataException("更新清单重定向到了不允许的域名。");
                }

                if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
                {
                    throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
                }

                if (response.Content.Headers.ContentLength is > MaximumManifestBytes)
                {
                    throw new InvalidDataException("更新清单超出大小限制。");
                }

                await using var source = await response.Content.ReadAsStreamAsync(routeGuard.Token).ConfigureAwait(false);
                var body = await UpdateBodyReader.ReadAsync(
                    source,
                    MaximumManifestBytes,
                    stallDeadline,
                    "更新清单超出大小限制。",
                    routeGuard.Token,
                    cancellationToken).ConfigureAwait(false);

                return UpdateManifestVerifier.ParseAndVerify(System.Text.Encoding.UTF8.GetString(body), expectedChannel);
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

        throw new InvalidOperationException($"所有更新线路均失败。{string.Join(" ", failures)}");
    }
}
