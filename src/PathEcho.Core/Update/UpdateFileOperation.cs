namespace PathEcho.Core.Update;

public static class UpdateFileOperation
{
    public static async Task RetryAsync(
        string stage,
        string path,
        Action operation,
        int maximumAttempts = 8,
        TimeSpan? retryDelay = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAttempts, 1);

        Exception? lastFailure = null;
        var delay = retryDelay ?? TimeSpan.FromMilliseconds(250);
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                operation();
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                lastFailure = exception;
                if (attempt < maximumAttempts)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw new UpdateFileAccessException(stage, path, lastFailure!);
    }
}

public sealed class UpdateFileAccessException(string stage, string path, Exception innerException)
    : IOException(
        $"更新在“{stage}”阶段无法访问“{path}”。已有限重试；请关闭仍在使用 PathEcho 安装目录的程序后重试。原始错误：{innerException.Message}",
        innerException)
{
    public string Stage { get; } = stage;

    public string AffectedPath { get; } = path;
}
