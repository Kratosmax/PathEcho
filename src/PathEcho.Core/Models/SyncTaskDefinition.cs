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
