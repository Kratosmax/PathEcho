namespace PathEcho.Core.Models;

public enum SyncMode
{
    LeftToRight,
    RightToLeft,
    Bidirectional,
}
public enum DeletionMode
{
    Ignore,
    Propagate,
    BackupThenPropagate,
}

public enum ConflictPolicy
{
    KeepBoth,
    PreferLeft,
    PreferRight,
    NewestWins,
}

public sealed record SyncTaskDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = "新同步任务";

    public string LeftPath { get; init; } = string.Empty;

    public string RightPath { get; init; } = string.Empty;

    public SyncMode Mode { get; init; } = SyncMode.LeftToRight;

    public DeletionMode DeletionMode { get; init; } = DeletionMode.Ignore;

    public ConflictPolicy ConflictPolicy { get; init; } = ConflictPolicy.KeepBoth;

    public SyncFilterRules Filters { get; init; } = new();

    public bool StartWithApplication { get; init; } = true;

    public bool IsEnabled { get; init; } = true;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidOperationException("任务名称不能为空。");
        }

        var left = NormalizeRoot(LeftPath);
        var right = NormalizeRoot(RightPath);
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("同步两端不能是同一目录。");
        }

        if (IsNested(left, right) || IsNested(right, left))
        {
            throw new InvalidOperationException("同步目录不能互相包含，否则会形成递归复制。");
        }

        Filters?.Validate();
    }

    public static string NormalizeRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("同步目录不能为空。");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool IsNested(string parent, string candidate)
    {
        var relative = Path.GetRelativePath(parent, candidate);
        return relative != "." &&
            !relative.Equals("..", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
    }
}

public sealed record SyncFilterRules
{
    public IReadOnlyList<string> IncludePatterns { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ExcludePatterns { get; init; } = Array.Empty<string>();

    public void Validate()
    {
        ValidatePatterns(IncludePatterns, "包含");
        ValidatePatterns(ExcludePatterns, "排除");
    }

    public bool Includes(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var included = IncludePatterns.Count == 0 || IncludePatterns.Any(pattern => Matches(pattern, normalized));
        return included && !ExcludePatterns.Any(pattern => Matches(pattern, normalized));
    }

    private static bool Matches(string pattern, string path) =>
        System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(
            pattern.Replace('\\', '/'),
            path,
            ignoreCase: true);

    private static void ValidatePatterns(IReadOnlyList<string> patterns, string kind)
    {
        if (patterns.Count > 100)
        {
            throw new InvalidOperationException($"同步{kind}规则不能超过 100 条。");
        }

        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > 256 || Path.IsPathRooted(pattern))
            {
                throw new InvalidOperationException($"同步{kind}规则必须是长度不超过 256 的相对路径 glob。");
            }
        }
    }
}
