namespace PathEcho.Core.Backup;

internal enum BackupProfileMoveFaultPoint
{
    CopiedObject,
    BeforeInstall,
    Installed,
    CleanupEntry,
    RollbackEntry
}

/// <summary>
/// A verified destination and an untouched source. Persist the destination configuration before Commit;
/// on persistence failure call Rollback. The first terminal operation wins. No finalizer deletes data.
/// </summary>
public sealed class BackupProfileMove
{
    private readonly object gate = new();
    private readonly Action<BackupProfileMoveFaultPoint, string> fault;
    private int state; // 0 prepared, 1 committed, 2 rollback selected (retryable), 3 rolled back.
    private string? retainedSource;

    internal BackupProfileMove(string sourceDirectory, string destinationDirectory,
        Action<BackupProfileMoveFaultPoint, string> fault)
    {
        SourceDirectory = sourceDirectory;
        DestinationDirectory = destinationDirectory;
        this.fault = fault;
    }

    public string SourceDirectory { get; }
    public string DestinationDirectory { get; }

    /// <summary>
    /// Best-effort source cleanup after configuration persistence. Returns the retained source path on
    /// failure, never throws, and never deletes the destination. Repeated calls return the first result.
    /// If rollback was already selected, no cleanup occurs and the preserved source path is returned.
    /// </summary>
    public string? Commit()
    {
        lock (gate)
        {
            if (state is 2 or 3)
            {
                return SourceDirectory;
            }
            if (state == 1)
            {
                return retainedSource;
            }

            state = 1; // Cleanup failure must never enable rollback of the committed destination.
            try
            {
                EnsureNoRedirectingAncestors(SourceDirectory);
                EnsureNoRedirectingAncestors(DestinationDirectory);
                DeleteOwnedTree(SourceDirectory, BackupProfileMoveFaultPoint.CleanupEntry, fault);
            }
            catch (Exception)
            {
                retainedSource = SourceDirectory;
            }
            return retainedSource;
        }
    }

    /// <summary>
    /// Deletes only this move's destination and preserves the original source. Failed cleanup reports
    /// both paths and may be retried. Once committed this is a no-op, including after a cleanup warning.
    /// </summary>
    public void Rollback()
    {
        lock (gate)
        {
            if (state is 1 or 3)
            {
                return;
            }
            state = 2;
            try
            {
                EnsureNoRedirectingAncestors(SourceDirectory);
                EnsureNoRedirectingAncestors(DestinationDirectory);
                DeleteOwnedTree(DestinationDirectory, BackupProfileMoveFaultPoint.RollbackEntry, fault);
                state = 3;
            }
            catch (Exception failure)
            {
                throw new BackupProfileMoveRollbackException(SourceDirectory, DestinationDirectory, failure);
            }
        }
    }

    internal static void EnsureNoRedirectingAncestors(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                FileSystemInfo item = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(current) : new FileInfo(current);
                if (item.LinkTarget is not null) throw new InvalidDataException($"迁移路径包含重定向链接，未删除任何源备份：{current}");
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { }
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent ?? "";
        }
    }

    internal static void DeleteOwnedTree(string path, BackupProfileMoveFaultPoint point,
        Action<BackupProfileMoveFaultPoint, string> fault)
    {
        EnsureNoRedirectingAncestors(Path.GetDirectoryName(path)!);
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (DirectoryNotFoundException) { return; }
        catch (FileNotFoundException) { return; }
        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            throw new IOException($"待清理目录已被文件占用，未删除：{path}");
        }
        fault(point, path);
        if (!attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                var entryAttributes = File.GetAttributes(entry);
                if (entryAttributes.HasFlag(FileAttributes.Directory))
                {
                    DeleteOwnedTree(entry, point, fault);
                }
                else
                {
                    fault(point, entry);
                    File.SetAttributes(entry, entryAttributes & ~FileAttributes.ReadOnly);
                    File.Delete(entry);
                }
            }
        }
        File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        Directory.Delete(path, false); // Never traverse directory links.
    }
}

/// <summary>The move is committed; the complete usable backup is at DestinationDirectory.</summary>
public sealed class BackupProfileMoveCleanupException : IOException
{
    public BackupProfileMoveCleanupException(string destinationDirectory, string retainedSourceDirectory)
        : base($"备份已完整迁移至：{destinationDirectory}。旧备份清理未完成，保留路径：{retainedSourceDirectory}。请使用新目标，不要反向迁移。")
    {
        DestinationDirectory = destinationDirectory;
        RetainedSourceDirectory = retainedSourceDirectory;
    }

    public string DestinationDirectory { get; }
    public string RetainedSourceDirectory { get; }
}

public sealed class BackupProfileMoveRollbackException : IOException
{
    public BackupProfileMoveRollbackException(string sourceDirectory, string retainedDirectory, Exception innerException)
        : base($"迁移撤销清理未完成。原备份完整保留于：{sourceDirectory}；本次副本残留于：{retainedDirectory}。请保留原目录，恢复后仅清理本次残留。", innerException)
    {
        SourceDirectory = sourceDirectory;
        RetainedDirectory = retainedDirectory;
    }

    public string SourceDirectory { get; }
    public string RetainedDirectory { get; }
}
