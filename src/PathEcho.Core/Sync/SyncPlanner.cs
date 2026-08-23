using PathEcho.Core.Models;

namespace PathEcho.Core.Sync;

public sealed class SyncPlanner
{
    public SyncPlan CreatePlan(
        SyncTaskDefinition task,
        IReadOnlyDictionary<string, FileStamp> left,
        IReadOnlyDictionary<string, FileStamp> right,
        SyncBaseline baseline)
    {
        var paths = left.Keys.Concat(right.Keys).Concat(baseline.Files.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => task.Filters?.Includes(path) ?? true)
            .Order(StringComparer.OrdinalIgnoreCase);
        var actions = new List<SyncAction>();

        foreach (var path in paths)
        {
            left.TryGetValue(path, out var leftNow);
            right.TryGetValue(path, out var rightNow);
            baseline.Files.TryGetValue(path, out var before);

            if (task.Mode != SyncMode.Bidirectional)
            {
                PlanOneWay(task, path, leftNow, rightNow, before, actions);
                continue;
            }

            PlanBidirectional(task, path, leftNow, rightNow, before, actions);
        }

        return new SyncPlan(actions);
    }

    private static void PlanOneWay(
        SyncTaskDefinition task,
        string path,
        FileStamp? left,
        FileStamp? right,
        SyncBaselineEntry? before,
        ICollection<SyncAction> actions)
    {
        var reverse = task.Mode == SyncMode.RightToLeft;
        var source = reverse ? right : left;
        var target = reverse ? left : right;
        var sourceBefore = reverse ? before?.Right : before?.Left;
        var targetDelete = reverse ? SyncActionKind.DeleteLeft : SyncActionKind.DeleteRight;
        var copy = reverse ? SyncActionKind.CopyRightToLeft : SyncActionKind.CopyLeftToRight;

        if (source is not null)
        {
            if (!source.ContentEquals(target))
            {
                actions.Add(new SyncAction(copy, path));
            }

            return;
        }

        if (target is not null && task.DeletionMode != DeletionMode.Ignore)
        {
            actions.Add(new SyncAction(targetDelete, path, "源端文件已删除"));
        }
    }

    private static void PlanBidirectional(
        SyncTaskDefinition task,
        string path,
        FileStamp? left,
        FileStamp? right,
        SyncBaselineEntry? before,
        ICollection<SyncAction> actions)
    {
        if (before is null)
        {
            if (left is not null && right is null)
            {
                actions.Add(new SyncAction(SyncActionKind.CopyLeftToRight, path));
            }
            else if (right is not null && left is null)
            {
                actions.Add(new SyncAction(SyncActionKind.CopyRightToLeft, path));
            }
            else if (left is not null && right is not null && !left.ContentEquals(right))
            {
                ResolveConflict(task, path, left, right, actions);
            }

            return;
        }

        var leftChanged = !Equivalent(left, before.Left);
        var rightChanged = !Equivalent(right, before.Right);
        if (!leftChanged && !rightChanged)
        {
            return;
        }

        if (leftChanged && rightChanged)
        {
            if (Equivalent(left, right))
            {
                return;
            }

            ResolveConflict(task, path, left, right, actions);
            return;
        }

        if (leftChanged)
        {
            PropagateChange(task, path, left, right, true, actions);
        }
        else
        {
            PropagateChange(task, path, right, left, false, actions);
        }
    }

    private static void PropagateChange(
        SyncTaskDefinition task,
        string path,
        FileStamp? changed,
        FileStamp? other,
        bool fromLeft,
        ICollection<SyncAction> actions)
    {
        if (changed is not null)
        {
            actions.Add(new SyncAction(fromLeft ? SyncActionKind.CopyLeftToRight : SyncActionKind.CopyRightToLeft, path));
        }
        else if (other is not null && task.DeletionMode != DeletionMode.Ignore)
        {
            actions.Add(new SyncAction(fromLeft ? SyncActionKind.DeleteRight : SyncActionKind.DeleteLeft, path));
        }
    }

    private static void ResolveConflict(
        SyncTaskDefinition task,
        string path,
        FileStamp? left,
        FileStamp? right,
        ICollection<SyncAction> actions)
    {
        switch (task.ConflictPolicy)
        {
            case ConflictPolicy.PreferLeft:
                actions.Add(left is null
                    ? new SyncAction(SyncActionKind.DeleteRight, path)
                    : new SyncAction(SyncActionKind.CopyLeftToRight, path));
                break;
            case ConflictPolicy.PreferRight:
                actions.Add(right is null
                    ? new SyncAction(SyncActionKind.DeleteLeft, path)
                    : new SyncAction(SyncActionKind.CopyRightToLeft, path));
                break;
            case ConflictPolicy.NewestWins:
                if (left is null || (right is not null && right.LastWriteUtcTicks > left.LastWriteUtcTicks))
                {
                    actions.Add(right is null
                        ? new SyncAction(SyncActionKind.DeleteLeft, path)
                        : new SyncAction(SyncActionKind.CopyRightToLeft, path));
                }
                else
                {
                    actions.Add(new SyncAction(SyncActionKind.CopyLeftToRight, path));
                }

                break;
            default:
                actions.Add(new SyncAction(SyncActionKind.KeepBothConflict, path, "两端自上次同步后均发生变化"));
                break;
        }
    }

    private static bool Equivalent(FileStamp? first, FileStamp? second) =>
        first is null ? second is null : first.ContentEquals(second);
}
