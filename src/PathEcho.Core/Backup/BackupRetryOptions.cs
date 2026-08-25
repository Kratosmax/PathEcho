namespace PathEcho.Core.Backup;

public enum BackupRetryStage
{
    ReadingSource,
    WritingBackup,
    PruningBackup,
}

public sealed record BackupRetryPrompt(
    string GameName,
    BackupRetryStage Stage,
    int FailedAttempts,
    string? StagingDirectory,
    Exception LastError);

public sealed class BackupRetryOptions
{
    public static BackupRetryOptions Default { get; } = new();

    public TimeSpan Delay { get; init; } = TimeSpan.FromSeconds(5);

    public int AttemptsPerPrompt { get; init; } = 10;

    public Func<BackupRetryPrompt, CancellationToken, Task<bool>>? ConfirmContinueAsync { get; init; }

    internal void Validate()
    {
        if (Delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Delay), "重试间隔不能小于零。");
        }

        if (AttemptsPerPrompt < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(AttemptsPerPrompt), "询问间隔至少为一次。");
        }
    }
}

public sealed class BackupRetryStoppedException : IOException
{
    public BackupRetryStoppedException(string message, string? stagingDirectory, Exception innerException)
        : base(message, innerException)
    {
        StagingDirectory = stagingDirectory;
    }

    public string? StagingDirectory { get; }
}
