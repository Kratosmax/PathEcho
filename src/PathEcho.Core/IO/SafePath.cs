namespace PathEcho.Core.IO;

public static class SafePath
{
    public static string NormalizeDirectory(string path, string emptyMessage)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(emptyMessage);
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static string CombineUnderRoot(string root, string relativePath, string escapeMessage)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var combined = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(escapeMessage);
        }

        return combined;
    }

    public static bool IsSameOrNested(string parent, string candidate)
    {
        var relative = Path.GetRelativePath(parent, candidate);
        return relative == "." ||
            (!relative.Equals("..", StringComparison.Ordinal) &&
             !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
             !Path.IsPathRooted(relative));
    }

    public static bool TryGetRelativePath(string root, string candidate, out string relativePath)
    {
        relativePath = Path.GetRelativePath(root, candidate);
        return relativePath != "." &&
            !relativePath.Equals("..", StringComparison.Ordinal) &&
            !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !Path.IsPathRooted(relativePath);
    }
}
