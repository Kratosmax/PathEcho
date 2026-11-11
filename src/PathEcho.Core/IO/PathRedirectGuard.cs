namespace PathEcho.Core.IO;

internal static class PathRedirectGuard
{
    internal static void EnsureNoRedirectingAncestors(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(current) : new FileInfo(current);
                if (info.LinkTarget is not null) throw new InvalidDataException($"路径通过符号链接或目录联接重定向，已停止文件操作：{current}");
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current)) ?? "";
        }
    }
}
