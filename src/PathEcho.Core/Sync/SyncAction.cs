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

public sealed record SyncRunResult(int CopiedFiles, int DeletedFiles, int Conflicts, SyncBaseline Baseline);
