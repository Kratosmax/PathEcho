using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;

namespace PathEcho.Dialogs;

public partial class RunningProcessSelectionWindow : Window
{
    private readonly ICollectionView _view;

    public RunningProcessSelectionWindow()
    {
        InitializeComponent();
        WindowBackdrop.Attach(this);
        DataContext = this;
        _view = CollectionViewSource.GetDefaultView(Processes);
        _view.Filter = MatchesSearch;
    }

    public ObservableCollection<RunningProcessRow> Processes { get; } = new();

    public RunningProcessRow? SelectedProcess { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        ProcessCountText.Text = "正在读取进程...";
        ProcessGrid.IsEnabled = false;
        var rows = await Task.Run(EnumerateProcesses);
        Processes.Clear();
        foreach (var row in rows)
        {
            Processes.Add(row);
        }

        ProcessGrid.IsEnabled = true;
        ProcessCountText.Text = $"可选择 {Processes.Count} 个程序";
    }

    private static IReadOnlyList<RunningProcessRow> EnumerateProcesses()
    {
        var rows = new List<RunningProcessRow>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId)
                    {
                        continue;
                    }

                    var path = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    rows.Add(new RunningProcessRow(
                        process.ProcessName,
                        process.Id,
                        process.MainWindowTitle,
                        path));
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Protected and already-exited processes are not selectable.
                }
            }
        }

        return rows
            .OrderByDescending(row => !string.IsNullOrWhiteSpace(row.WindowTitle))
            .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.ProcessId)
            .ToArray();
    }

    private bool MatchesSearch(object item)
    {
        if (item is not RunningProcessRow row || string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            return true;
        }

        var search = SearchBox.Text.Trim();
        return row.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
               row.ProcessId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) ||
               row.WindowTitle.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
               row.ExecutablePath.Contains(search, StringComparison.CurrentCultureIgnoreCase);
    }

    private void OnSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => _view.Refresh();

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        SelectButton.IsEnabled = ProcessGrid.SelectedItem is RunningProcessRow;

    private void OnProcessDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ProcessGrid.SelectedItem is RunningProcessRow)
        {
            SelectCurrent();
        }
    }

    private void OnSelect(object sender, RoutedEventArgs e) => SelectCurrent();

    private void SelectCurrent()
    {
        if (ProcessGrid.SelectedItem is not RunningProcessRow row)
        {
            return;
        }

        SelectedProcess = row;
        DialogResult = true;
    }
}

public sealed record RunningProcessRow(string Name, int ProcessId, string WindowTitle, string ExecutablePath);
