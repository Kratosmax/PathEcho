namespace PathEcho.Core.Update;

/// <summary>
/// 更新响应正文的受限读取：复用更新包下载器的停滞取消思想，为每次读取设置独立停滞时限，
/// 并由调用方传入的线路令牌承担每条线路的总体截止时间。用户取消始终优先于任何时限。
/// </summary>
internal static class UpdateBodyReader
{
    internal static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(20);

    internal static readonly TimeSpan DefaultRouteTimeout = TimeSpan.FromSeconds(120);

    internal static async Task<byte[]> ReadAsync(
        Stream source,
        long maximumBytes,
        TimeSpan stallTimeout,
        string oversizedMessage,
        CancellationToken routeToken,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var target = new MemoryStream();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            routeToken.ThrowIfCancellationRequested();
            using var readGuard = CancellationTokenSource.CreateLinkedTokenSource(routeToken);
            readGuard.CancelAfter(stallTimeout);
            int read;
            try
            {
                read = await source.ReadAsync(buffer, readGuard.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
                when (!cancellationToken.IsCancellationRequested && !routeToken.IsCancellationRequested)
            {
                throw new TimeoutException("响应正文读取停滞超时。", exception);
            }

            if (read == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                routeToken.ThrowIfCancellationRequested();
                return target.ToArray();
            }

            if (target.Length + read > maximumBytes)
            {
                throw new InvalidDataException(oversizedMessage);
            }

            await target.WriteAsync(buffer.AsMemory(0, read), routeToken).ConfigureAwait(false);
        }
    }
}
