namespace PathEcho.Core.Backup;

internal static class DirectoryTree
{
    public static void DeleteIfPresent(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        var rootAttributes = File.GetAttributes(path);
        if (rootAttributes.HasFlag(FileAttributes.ReparsePoint))
        {
            File.SetAttributes(path, rootAttributes & ~FileAttributes.ReadOnly);
            Directory.Delete(path, false);
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(entry);
            File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    Directory.Delete(entry, false);
                }
                else
                {
                    DeleteIfPresent(entry);
                }
            }
            else
            {
                File.Delete(entry);
            }
        }

        File.SetAttributes(path, rootAttributes & ~FileAttributes.ReadOnly);
        Directory.Delete(path, false);
    }
}
