using System.IO;
using System.Windows;
using System.Windows.Input;
using PathEcho.Core.GameCatalog;
using PathEcho.Services;

namespace PathEcho.Dialogs;

public partial class GameDiscoveryWindow : Window
{
    public GameDiscoveryWindow(GameDiscoveryOutcome outcome)
    {
        InitializeComponent();
        WindowBackdrop.Attach(this);
        SourceText.Text = outcome.UsedCachedCopy
            ? $"规则修订 {outcome.CatalogRevision} · 在线线路不可用，已使用上次验证通过的缓存"
            : $"规则修订 {outcome.CatalogRevision} · 已从 GitHub 获取并验证签名";
        GamesGrid.ItemsSource = outcome.Matches.Select(match => new DiscoveredGameRow(match)).ToArray();
        GamesGrid.SelectedIndex = outcome.Matches.Count > 0 ? 0 : -1;
        GamesGrid.Visibility = outcome.Matches.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Visibility = outcome.Matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public DiscoveredGame? SelectedGame { get; private set; }

    private void OnSelect(object sender, RoutedEventArgs e)
    {
        if (GamesGrid.SelectedItem is DiscoveredGameRow row)
        {
            SelectedGame = row.Match;
            DialogResult = true;
        }
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e) => OnSelect(sender, e);
}

public sealed class DiscoveredGameRow
{
    public DiscoveredGameRow(DiscoveredGame match) => Match = match;

    public DiscoveredGame Match { get; }
    public string Name => Match.CatalogEntry.Name;
    public string Process => $"{Path.GetFileName(Match.ExecutablePath)} ({Match.ProcessId})";
    public string SaveDirectory => Match.PreferredSaveDirectory;
    public string DirectoryState => Directory.Exists(SaveDirectory) ? "已找到" : "待确认";
}
