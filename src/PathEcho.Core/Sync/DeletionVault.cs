namespace PathEcho.Core.Sync;

public sealed class DeletionVault
{
    private readonly string _root;

    public DeletionVault(string root)
    {
        _root = Path.GetFullPath(root);
    }

    public async Task<string> BackupAsync(Guid taskId, string relativePath, string source, CancellationToken cancellationToken = default)
    {
        var batch = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var destination = Path.Combine(_root, taskId.ToString("N"), batch, relativePath);
        await AtomicFileOperations.CopyAsync(source, destination, cancellationToken).ConfigureAwait(false);
        return destination;
    }
}
