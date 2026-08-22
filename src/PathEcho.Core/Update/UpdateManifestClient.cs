using System.Net;

namespace PathEcho.Core.Update;

public sealed class UpdateManifestClient(HttpClient httpClient)
{
    private const int MaximumManifestBytes = 256 * 1024;

    public async Task<UpdateManifest> FetchAsync(
        Uri originalUri,
        string expectedChannel,
        UpdateNetworkOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var failures = new List<string>();
        foreach (var route in UpdateRoutePlanner.CreateRoutes(originalUri, options))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, route.RequestUri);
                using var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
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

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var target = new MemoryStream();
                var buffer = new byte[8192];
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    if (target.Length + read > MaximumManifestBytes)
                    {
                        throw new InvalidDataException("更新清单超出大小限制。");
                    }

                    target.Write(buffer, 0, read);
                }

                return UpdateManifestVerifier.ParseAndVerify(System.Text.Encoding.UTF8.GetString(target.ToArray()), expectedChannel);
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

        throw new InvalidOperationException($"所有更新线路均失败。{string.Join(" ", failures)}");
    }
}
