using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PathEcho.Core.Backup;
using PathEcho.Services;

namespace PathEcho.Dialogs;

public partial class SnapshotFilesWindow : Window
{
    private readonly ICollectionView _filesView;

    public SnapshotFilesWindow(HistoryRow history)
    {
        InitializeComponent();
        WindowBackdrop.Attach(this);
        TitleText.Text = history.GameName;
        SummaryText.Text = $"{history.CreatedAt} · {history.Trigger} · {history.FileCount} 个文件";
        var rows = history.Version.Manifest.Files
            .Select(entry => new SnapshotFileRow(entry))
            .OrderBy(row => row.RelativePath, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        _filesView = CollectionViewSource.GetDefaultView(rows);
        _filesView.Filter = MatchesSearch;
        FilesGrid.ItemsSource = _filesView;
        UpdateEmptyState();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        _filesView.Refresh();
        UpdateEmptyState();
    }

    private bool MatchesSearch(object item)
    {
        if (item is not SnapshotFileRow row)
        {
            return false;
        }

        var query = SearchBox.Text.Trim();
        return query.Length == 0 || new[]
        {
            row.FileName,
            row.DirectoryName,
            row.LastWriteTime,
            row.Size,
        }.Any(value => value.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    private void OnClearSearch(object sender, RoutedEventArgs e) => SearchBox.Clear();

    private void UpdateEmptyState()
    {
        var hasFiles = FilesGrid.Items.Count > 0;
        EmptyState.Visibility = _filesView.SourceCollection.Cast<object>().Any()
            ? Visibility.Collapsed
            : Visibility.Visible;
        NoResultsState.Visibility = !hasFiles && EmptyState.Visibility != Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        FilesGrid.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
    }
}

public sealed class SnapshotFileRow
{
    public SnapshotFileRow(SnapshotFileEntry entry)
    {
        RelativePath = entry.RelativePath;
        FileName = Path.GetFileName(entry.RelativePath);
        DirectoryName = Path.GetDirectoryName(entry.RelativePath) is { Length: > 0 } directory ? directory : ".";
        LastWriteTime = FormatLastWriteTime(entry.LastWriteUtcTicks);
        Size = FormatSize(entry.Length);
    }

    public string RelativePath { get; }

    public string FileName { get; }

    public string DirectoryName { get; }

    public string LastWriteTime { get; }

    public string Size { get; }

    private static string FormatLastWriteTime(long utcTicks)
    {
        try
        {
            return new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        }
        catch (ArgumentOutOfRangeException)
        {
            return "时间无效";
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 0)
        {
            return "大小无效";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]} · {bytes:N0} 字节";
    }
}
