namespace PathEcho.Core.GameCatalog;

public sealed record RunningGameProcess(int ProcessId, string ExecutablePath);

public sealed record DiscoveredGame(
    GameCatalogEntry CatalogEntry,
    int ProcessId,
    string ExecutablePath,
    IReadOnlyList<string> SaveDirectories)
{
    public string PreferredSaveDirectory => SaveDirectories.FirstOrDefault(Directory.Exists) ?? SaveDirectories[0];
}

public static class GameDiscoveryService
{
    public static IReadOnlyList<DiscoveredGame> Match(
        GameCatalogDocument catalog,
        IEnumerable<RunningGameProcess> processes)
    {
        var byExecutable = catalog.Games
            .SelectMany(game => game.Executables.Select(executable => (executable, game)))
            .GroupBy(item => item.executable, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(item => item.game).ToArray(), StringComparer.OrdinalIgnoreCase);
        var matches = new List<DiscoveredGame>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in processes)
        {
            var executable = Path.GetFileName(process.ExecutablePath);
            if (!byExecutable.TryGetValue(executable, out var games))
            {
                continue;
            }

            foreach (var game in games)
            {
                if (!seen.Add(game.Id))
                {
                    continue;
                }

                matches.Add(new DiscoveredGame(
                    game,
                    process.ProcessId,
                    process.ExecutablePath,
                    game.SavePathTemplates.Select(ResolveTemplate).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
            }
        }

        return matches.OrderBy(match => match.CatalogEntry.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static string ResolveTemplate(string template)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{UserProfile}"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ["{Documents}"] = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            ["{SavedGames}"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games"),
            ["{LocalAppData}"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ["{RoamingAppData}"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ["{ProgramData}"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        };
        var path = values.Aggregate(template, (current, pair) => current.Replace(pair.Key, pair.Value, StringComparison.Ordinal));
        return Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar));
    }
}
