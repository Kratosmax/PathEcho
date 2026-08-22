using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using PathEcho.Core.Restore;
using PathEcho.Services;

namespace PathEcho.Dialogs;

public partial class RestoreWindow : Window
{
    private readonly HistoryRow _history;

    public RestoreWindow(HistoryRow history)
    {
        _history = history;
        InitializeComponent();
        WindowBackdrop.Attach(this);
        VersionText.Text = $"{history.GameName} · {history.CreatedAt} · {history.FileCount} 个文件";
        ModeBox.ItemsSource = new[] { "清空当前目录后完整恢复", "仅恢复与该版本不同的文件", "仅恢复正则匹配的文件" };
        ModeBox.SelectedIndex = 0;
    }

    public RestoreRequest? Result { get; private set; }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IncludeBox is null)
        {
            return;
        }

        IncludeBox.IsEnabled = ModeBox.SelectedIndex == 2;
        ExcludeBox.IsEnabled = ModeBox.SelectedIndex != 0;
        WarningText.Text = ModeBox.SelectedIndex == 0
            ? "整目录恢复会先暂存完整版本，再用目录交换替换当前存档。"
            : "文件级恢复会先暂存和校验全部选中文件；提交失败时自动恢复已替换文件。";
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        try
        {
            var include = ParsePatterns(IncludeBox.Text);
            var exclude = ParsePatterns(ExcludeBox.Text);
            if (ModeBox.SelectedIndex == 2 && include.Count == 0)
            {
                throw new InvalidOperationException("正则文件恢复至少需要一个匹配表达式。");
            }

            Result = new RestoreRequest
            {
                SnapshotDirectory = _history.SnapshotDirectory,
                TargetDirectory = _history.Profile.SaveDirectory,
                Mode = (RestoreMode)ModeBox.SelectedIndex,
                IncludePatterns = include,
                ExcludePatterns = exclude,
            };
            DialogResult = true;
        }
        catch (Exception exception)
        {
            ErrorText.Text = exception.Message;
        }
    }

    private static IReadOnlyList<string> ParsePatterns(string text)
    {
        var patterns = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var pattern in patterns)
        {
            _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }

        return patterns;
    }
}
