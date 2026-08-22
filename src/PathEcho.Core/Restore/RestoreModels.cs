namespace PathEcho.Core.Restore;

public enum RestoreMode
{
    CleanDirectory,
    ChangedFiles,
    FilteredFiles,
}

public enum OccupiedFileAction
{
    Cancel,
    EndProcesses,
    ForceAttempt,
}

public sealed record RestoreRequest
{
    public required string SnapshotDirectory { get; init; }

    public required string TargetDirectory { get; init; }

    public RestoreMode Mode { get; init; } = RestoreMode.CleanDirectory;

    public IReadOnlyList<string> IncludePatterns { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ExcludePatterns { get; init; } = Array.Empty<string>();

    public OccupiedFileAction OccupiedFileAction { get; init; } = OccupiedFileAction.Cancel;
}

public sealed record LockingProcess(
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    string Name,
    bool CanTerminate,
    string? ReasonCannotTerminate = null);

public sealed record OccupiedFile(string Path, IReadOnlyList<LockingProcess> Processes);

public interface IFileOccupancyService
{
    Task<IReadOnlyList<OccupiedFile>> FindAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default);

    Task TerminateAsync(
        IReadOnlyList<LockingProcess> processes,
        CancellationToken cancellationToken = default);
}

public sealed class FilesOccupiedException : IOException
{
    public FilesOccupiedException(IReadOnlyList<OccupiedFile> occupiedFiles)
        : base("部分存档文件正被其他程序占用，尚未修改存档目录。")
    {
        OccupiedFiles = occupiedFiles;
    }

    public IReadOnlyList<OccupiedFile> OccupiedFiles { get; }
}

public sealed record RestoreResult(int RestoredFiles, int RemovedFiles, string? PreservedRollbackDirectory);
