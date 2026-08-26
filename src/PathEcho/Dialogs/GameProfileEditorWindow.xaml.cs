using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PathEcho.Core.GameCatalog;
using PathEcho.Core.Models;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using OpenFolderDialog = Microsoft.Win32.OpenFolderDialog;

namespace PathEcho.Dialogs;

public partial class GameProfileEditorWindow : Window
{
    private readonly GameBackupProfile? _existingProfile;
    private readonly UnsavedChangesGuard _unsavedChanges;

    public GameProfileEditorWindow(
        DiscoveredGame? discoveredGame = null,
        BackupNotificationSettings? defaultNotificationSettings = null)
    {
        InitializeComponent();
        WindowBackdrop.Attach(this);
        InitializeNotificationControls(
            BackupNotificationMode.Inherit,
            defaultNotificationSettings ?? new BackupNotificationSettings());
        if (discoveredGame is not null)
        {
            NameBox.Text = discoveredGame.CatalogEntry.Name;
            SavePathBox.Text = discoveredGame.PreferredSaveDirectory;
            ProcessPathBox.Text = discoveredGame.ExecutablePath;
            ProcessCheck.IsChecked = true;
        }

        _unsavedChanges = new UnsavedChangesGuard(this, CaptureState);
    }

    public GameProfileEditorWindow(
        GameBackupProfile profile,
        BackupNotificationSettings? defaultNotificationSettings = null)
    {
        _existingProfile = profile;
        InitializeComponent();
        WindowBackdrop.Attach(this);
        Title = "编辑游戏存档";
        TitleText.Text = "编辑游戏存档";
        SubmitButton.Content = "保存";
        NameBox.Text = profile.Name;
        SavePathBox.Text = profile.SaveDirectory;
        BackupPathBox.Text = profile.BackupDirectory ?? string.Empty;
        ScheduledCheck.IsChecked = profile.Triggers.HasFlag(BackupTrigger.Scheduled);
        ImportantCheck.IsChecked = profile.Triggers.HasFlag(BackupTrigger.ImportantFileChanged);
        ChangedCheck.IsChecked = profile.Triggers.HasFlag(BackupTrigger.ChangedFiles);
        ProcessCheck.IsChecked = profile.Triggers.HasFlag(BackupTrigger.ProcessExited);
        ScheduleBox.Text = profile.ScheduleInterval.TotalMinutes.ToString("0.##");
        MinimumBox.Text = profile.MinimumBackupInterval.TotalMinutes.ToString("0.##");
        VersionsBox.Text = profile.RetainedVersions.ToString();
        HourlyVersionsBox.Text = profile.RetainedHourlyVersions.ToString();
        DailyVersionsBox.Text = profile.RetainedDailyVersions.ToString();
        PatternsBox.Text = string.Join(Environment.NewLine, profile.ImportantFilePatterns);
        ProcessPathBox.Text = profile.ProcessExecutablePath ?? string.Empty;
        InitializeNotificationControls(
            profile.BackupNotificationMode,
            profile.BackupNotificationSettings ?? defaultNotificationSettings ?? new BackupNotificationSettings());
        _unsavedChanges = new UnsavedChangesGuard(this, CaptureState);
    }

    public GameBackupProfile? Result { get; private set; }

    internal void SelectCustomNotificationForPreview()
    {
        NotificationModeBox.SelectedIndex = 2;
        Height = 820;
    }

    internal void ScrollNotificationIntoViewForPreview() => RootScrollViewer.ScrollToEnd();

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

    private void OnSelectRunningProcess(object sender, RoutedEventArgs e)
    {
        var dialog = new RunningProcessSelectionWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedProcess is null)
        {
            return;
        }

        ProcessPathBox.Text = dialog.SelectedProcess.ExecutablePath;
        ProcessCheck.IsChecked = true;
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            NameBox.Text = dialog.SelectedProcess.Name;
        }
    }

    private void InitializeNotificationControls(
        BackupNotificationMode mode,
        BackupNotificationSettings settings)
    {
        NotificationEditor.LoadSettings(settings);
        NotificationEditor.PreviewRequested += (_, previewSettings) =>
            ((App)System.Windows.Application.Current).PreviewBackupNotification(previewSettings);
        NotificationModeBox.SelectedIndex = mode switch
        {
            BackupNotificationMode.Disabled => 1,
            BackupNotificationMode.Custom => 2,
            _ => 0,
        };
        UpdateNotificationEditorVisibility();
    }

    private void OnNotificationModeChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateNotificationEditorVisibility();

    private void UpdateNotificationEditorVisibility()
    {
        if (NotificationEditor is not null)
        {
            NotificationEditor.Visibility = NotificationModeBox.SelectedIndex == 2
                ? Visibility.Visible
                : Visibility.Collapsed;
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

            var updated = new GameBackupProfile
            {
                Name = NameBox.Text.Trim(),
                SaveDirectory = SavePathBox.Text.Trim(),
                BackupDirectory = string.IsNullOrWhiteSpace(BackupPathBox.Text) ? null : BackupPathBox.Text.Trim(),
                Triggers = triggers,
                ScheduleInterval = TimeSpan.FromMinutes(ParsePositive(ScheduleBox.Text, "定时间隔")),
                MinimumBackupInterval = TimeSpan.FromMinutes(ParseNonNegative(MinimumBox.Text, "最低间隔")),
                RetainedVersions = ParsePositiveInteger(VersionsBox.Text, "最近版本"),
                RetainedHourlyVersions = ParseNonNegativeInteger(HourlyVersionsBox.Text, "每小时锚点"),
                RetainedDailyVersions = ParseNonNegativeInteger(DailyVersionsBox.Text, "每日锚点"),
                ImportantFilePatterns = PatternsBox.Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                ProcessExecutablePath = string.IsNullOrWhiteSpace(ProcessPathBox.Text) ? null : ProcessPathBox.Text.Trim(),
                BackupNotificationMode = NotificationModeBox.SelectedIndex switch
                {
                    1 => BackupNotificationMode.Disabled,
                    2 => BackupNotificationMode.Custom,
                    _ => BackupNotificationMode.Inherit,
                },
                BackupNotificationSettings = NotificationEditor.GetSettings(),
            };
            Result = _existingProfile is null
                ? updated
                : updated with { Id = _existingProfile.Id, IsEnabled = _existingProfile.IsEnabled };
            Result.Validate();
            _unsavedChanges.MarkSaved();
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

    private static int ParsePositiveInteger(string value, string name)
    {
        if (!int.TryParse(value, out var parsed) || parsed < 1)
        {
            throw new InvalidOperationException($"{name}必须是大于零的整数。");
        }

        return parsed;
    }

    private static int ParseNonNegativeInteger(string value, string name)
    {
        if (!int.TryParse(value, out var parsed) || parsed < 0)
        {
            throw new InvalidOperationException($"{name}必须是大于或等于零的整数。");
        }

        return parsed;
    }

    private string CaptureState() => string.Join('\u001f',
        NameBox.Text,
        SavePathBox.Text,
        BackupPathBox.Text,
        ScheduledCheck.IsChecked,
        ImportantCheck.IsChecked,
        ChangedCheck.IsChecked,
        ProcessCheck.IsChecked,
        ScheduleBox.Text,
        MinimumBox.Text,
        VersionsBox.Text,
        HourlyVersionsBox.Text,
        DailyVersionsBox.Text,
        PatternsBox.Text,
        ProcessPathBox.Text,
        NotificationModeBox.SelectedIndex,
        NotificationEditor.CaptureState());
}
