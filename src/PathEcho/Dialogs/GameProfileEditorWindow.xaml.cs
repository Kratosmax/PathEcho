using System.Windows;
using Microsoft.Win32;
using PathEcho.Core.Models;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using OpenFolderDialog = Microsoft.Win32.OpenFolderDialog;

namespace PathEcho.Dialogs;

public partial class GameProfileEditorWindow : Window
{
    public GameProfileEditorWindow()
    {
        InitializeComponent();
        WindowBackdrop.Attach(this);
    }

    public GameBackupProfile? Result { get; private set; }

    private void OnBrowseSave(object sender, RoutedEventArgs e) => BrowseFolder(SavePathBox);
    private void OnBrowseBackup(object sender, RoutedEventArgs e) => BrowseFolder(BackupPathBox);

    private static void BrowseFolder(System.Windows.Controls.TextBox target)
    {
        var dialog = new OpenFolderDialog();
        if (dialog.ShowDialog() == true)
        {
            target.Text = dialog.FolderName;
        }
    }

    private void OnBrowseProcess(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Windows 程序 (*.exe)|*.exe|所有文件 (*.*)|*.*" };
        if (dialog.ShowDialog() == true)
        {
            ProcessPathBox.Text = dialog.FileName;
        }
    }

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        try
        {
            var triggers = BackupTrigger.None;
            if (ScheduledCheck.IsChecked == true) triggers |= BackupTrigger.Scheduled;
            if (ImportantCheck.IsChecked == true) triggers |= BackupTrigger.ImportantFileChanged;
            if (ChangedCheck.IsChecked == true) triggers |= BackupTrigger.ChangedFiles;
            if (ProcessCheck.IsChecked == true) triggers |= BackupTrigger.ProcessExited;

            Result = new GameBackupProfile
            {
                Name = NameBox.Text.Trim(),
                SaveDirectory = SavePathBox.Text.Trim(),
                BackupDirectory = string.IsNullOrWhiteSpace(BackupPathBox.Text) ? null : BackupPathBox.Text.Trim(),
                Triggers = triggers,
                ScheduleInterval = TimeSpan.FromMinutes(ParsePositive(ScheduleBox.Text, "定时间隔")),
                MinimumBackupInterval = TimeSpan.FromMinutes(ParseNonNegative(MinimumBox.Text, "最低间隔")),
                RetainedVersions = (int)ParsePositive(VersionsBox.Text, "保留版本"),
                ImportantFilePatterns = PatternsBox.Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                ProcessExecutablePath = string.IsNullOrWhiteSpace(ProcessPathBox.Text) ? null : ProcessPathBox.Text.Trim(),
            };
            Result.Validate();
            DialogResult = true;
        }
        catch (Exception exception)
        {
            ErrorText.Text = exception.Message;
        }
    }

    private static double ParsePositive(string value, string name)
    {
        if (!double.TryParse(value, out var parsed) || parsed <= 0)
        {
            throw new InvalidOperationException($"{name}必须大于零。");
        }

        return parsed;
    }

    private static double ParseNonNegative(string value, string name)
    {
        if (!double.TryParse(value, out var parsed) || parsed < 0)
        {
            throw new InvalidOperationException($"{name}不能小于零。");
        }

        return parsed;
    }
}
