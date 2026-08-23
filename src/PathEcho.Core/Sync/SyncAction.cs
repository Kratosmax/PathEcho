namespace PathEcho.Core.Sync;

public enum SyncActionKind
{
    CopyLeftToRight,
    CopyRightToLeft,
    DeleteLeft,
    DeleteRight,
    KeepBothConflict,
}

public sealed record SyncAction(SyncActionKind Kind, string RelativePath, string? Reason = null);

public sealed record SyncPlan(IReadOnlyList<SyncAction> Actions);

public sealed record SyncPreviewResult(IReadOnlyList<SyncAction> Actions)
{
    public int CopiedFiles => Actions.Count(action => action.Kind is SyncActionKind.CopyLeftToRight or SyncActionKind.CopyRightToLeft);

    public int DeletedFiles => Actions.Count(action => action.Kind is SyncActionKind.DeleteLeft or SyncActionKind.DeleteRight);

    public int Conflicts => Actions.Count(action => action.Kind == SyncActionKind.KeepBothConflict);
}

public sealed record SyncRunResult(int CopiedFiles, int DeletedFiles, int Conflicts, SyncBaseline Baseline);
