namespace PathEcho.Core.Restore;

// Instance-scoped fault injection for deterministic transaction safety tests.
internal enum RestoreFaultPoint
{
    Staged,
    WholeTargetMoved,
    SelectedTargetMoved,
    SelectedFileCommitted,
    Committed,
    CleanupEnumerate,
    CleanupFile,
    WholeRollback,
    SelectedRollback,
}
