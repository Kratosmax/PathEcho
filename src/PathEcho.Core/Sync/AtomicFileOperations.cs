using PathEcho.Core.IO;
namespace PathEcho.Core.Sync;

public static class AtomicFileOperations
{
    public static async Task CopyAsync(string source, string destination, CancellationToken cancellationToken = default)
    {
        PathRedirectGuard.EnsureNoRedirectingAncestors(source);
        PathRedirectGuard.EnsureNoRedirectingAncestors(destination);
        var destinationDirectory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("目标文件缺少父目录。");
        Directory.CreateDirectory(destinationDirectory);
        var temporary = Path.Combine(destinationDirectory, $".pathecho-{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, true))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.SetLastWriteTimeUtc(temporary, File.GetLastWriteTimeUtc(source));
            cancellationToken.ThrowIfCancellationRequested();
            PathRedirectGuard.EnsureNoRedirectingAncestors(destination);
            PathRedirectGuard.EnsureNoRedirectingAncestors(temporary);
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
