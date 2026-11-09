using PathEcho.Core.Models;
using PathEcho.Services;

internal static class RuntimeRowTests
{
    internal static Task RunAsync(string testRoot)
    {
        var root = Path.Combine(testRoot, "row-state");
        var left = Path.Combine(root, "left");
        var right = Path.Combine(root, "right");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        var row = new SyncTaskRow(new SyncTaskDefinition { LeftPath = left, RightPath = right });
        var changes = new List<string?>();
        row.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        Assert(!row.IsDirectoryStateKnown && !row.AreDirectoriesAvailable && !row.HasDirectoryIssue, "New rows must remain unknown until a background probe completes.");
        row.ApplyDirectoryAvailability(Directory.Exists(left), Directory.Exists(right));
        Assert(row.AreDirectoriesAvailable, "Existing directories should become available after a probe.");
        changes.Clear();
        Directory.Move(right, right + "-offline");
        Assert(row.ApplyDirectoryAvailability(Directory.Exists(left), Directory.Exists(right)), "Offline directory should change state.");
        Assert(row.HasDirectoryIssue && row.DirectoryStateDetail == "目标目录不可用", "Directory details should reflect the unavailable side.");
        Assert(new[] { "HasDirectoryIssue", "DirectoryState", "DirectoryStateDetail" }.All(changes.Contains), "Dependent bindings must receive notifications.");
        changes.Clear();
        Assert(!row.ApplyDirectoryAvailability(true, false) && changes.Count == 0, "Unchanged probes should not refresh bindings.");
        Directory.Move(right + "-offline", right);
        row.ApplyDirectoryAvailability(Directory.Exists(left), Directory.Exists(right));
        Assert(!row.HasDirectoryIssue, "Reconnected directory should become available without replacing the row.");
        row.SetStatus(SyncTaskActivity.Running, "传输中");
        Assert(row.Activity == SyncTaskActivity.Running && row.Status == "传输中", "Activity must not depend on Chinese matching words.");
        row.SetStatus(SyncTaskActivity.Failed, "Access denied");
        Assert(row.Activity == SyncTaskActivity.Failed, "Localized error text must not change the failed category.");
        row.SetStatus(SyncTaskActivity.Succeeded, "同步失败文件夹 已复制");
        Assert(row.Activity == SyncTaskActivity.Succeeded, "Incidental error words must not change successful activity.");
        return Task.CompletedTask;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
