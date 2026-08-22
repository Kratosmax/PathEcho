namespace PathEcho.Core.Models;

using PathEcho.Core.Update;

public sealed record AppConfiguration
{
    public int SchemaVersion { get; init; } = 1;

    public bool StartWithWindows { get; init; } = true;

    public bool StartMinimized { get; init; }

    public bool CheckForUpdates { get; init; } = true;

    public UpdateNetworkOptions UpdateNetwork { get; init; } = new();

    public string DefaultBackupDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PathEcho",
        "Backups");

    public IReadOnlyList<SyncTaskDefinition> SyncTasks { get; init; } = Array.Empty<SyncTaskDefinition>();

    public IReadOnlyList<GameBackupProfile> GameProfiles { get; init; } = Array.Empty<GameBackupProfile>();
}
