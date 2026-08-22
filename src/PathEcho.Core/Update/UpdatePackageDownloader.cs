using System.Net;
using System.Security.Cryptography;

namespace PathEcho.Core.Update;

public readonly record struct UpdateDownloadProgress(long BytesReceived, long MaximumBytes);

public sealed class UpdatePackageDownloader(HttpClient httpClient)
{
    public async Task DownloadAsync(
        Uri originalUri,
        string destinationPath,
        string expectedSha256,
        long maximumBytes,
        UpdateNetworkOptions? options = null,
        TimeSpan? stallTimeout = null,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(originalUri);
        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        if (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("SHA-256 必须是 64 位十六进制文本。", nameof(expectedSha256));
        }

        var destination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("下载目标缺少父目录。"));
        var failures = new List<string>();

        foreach (var route in UpdateRoutePlanner.CreateRoutes(originalUri, options))
        {
            var temporary = $"{destination}.{Guid.NewGuid():N}.download";
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
                    throw new InvalidDataException("更新响应重定向到了不允许的域名。");
                }

                if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
                {
                    throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
                }

                if (response.Content.Headers.ContentLength is long length && length > maximumBytes)
                {
                    throw new InvalidDataException("更新包超过允许的下载大小。");
                }

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    await CopyLimitedAsync(source, target, maximumBytes, stallTimeout ?? TimeSpan.FromSeconds(20), progress, cancellationToken)
                        .ConfigureAwait(false);
                    await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                string actualHash;
                await using (var verification = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                {
                    actualHash = Convert.ToHexString(await SHA256.HashDataAsync(verification, cancellationToken).ConfigureAwait(false));
                }

                if (!string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("更新包 SHA-256 校验失败。");
                }

                File.Move(temporary, destination, true);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryDelete(temporary);
                throw;
            }
            catch (OperationCanceledException)
            {
                TryDelete(temporary);
                failures.Add($"{route.Name}：请求超时。");
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or TimeoutException)
            {
                TryDelete(temporary);
                failures.Add($"{route.Name}：{exception.Message}");
            }
        }

        throw new InvalidOperationException($"所有更新线路均失败。{string.Join(" ", failures)}");
    }

    private static async Task CopyLimitedAsync(
        Stream source,
        Stream target,
        long maximumBytes,
        TimeSpan stallTimeout,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).AsTask()
                .WaitAsync(stallTimeout, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            total = checked(total + read);
            if (total > maximumBytes)
            {
                throw new InvalidDataException("更新包超过允许的下载大小。");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            progress?.Report(new UpdateDownloadProgress(total, maximumBytes));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
