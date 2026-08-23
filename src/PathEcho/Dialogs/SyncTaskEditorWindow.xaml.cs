using System.Windows;
using Microsoft.Win32;
using PathEcho.Core.Models;
using OpenFolderDialog = Microsoft.Win32.OpenFolderDialog;

namespace PathEcho.Dialogs;

public partial class SyncTaskEditorWindow : Window
{
    private readonly Guid? _existingId;
    private readonly UnsavedChangesGuard _unsavedChanges;

    public SyncTaskEditorWindow(SyncTaskDefinition? task = null, bool duplicate = false)
    {
        InitializeComponent();
        WindowBackdrop.Attach(this);
        ModeBox.ItemsSource = new[] { "左 → 右", "右 → 左", "双向" };
        DeletionBox.ItemsSource = new[] { "不传播删除", "传播删除", "删除前备份" };
        ConflictBox.ItemsSource = new[] { "保留两份", "优先左侧", "优先右侧", "较新文件" };
        ModeBox.SelectedIndex = 0;
        DeletionBox.SelectedIndex = 0;
        ConflictBox.SelectedIndex = 0;
        if (task is not null)
        {
            _existingId = duplicate ? null : task.Id;
            Title = duplicate ? "复制同步任务" : "编辑同步任务";
            HeadingText.Text = Title;
            SaveButton.Content = duplicate ? "创建副本" : "保存";
            NameBox.Text = duplicate ? $"{task.Name} 副本" : task.Name;
            LeftPathBox.Text = task.LeftPath;
            RightPathBox.Text = task.RightPath;
            ModeBox.SelectedIndex = (int)task.Mode;
            DeletionBox.SelectedIndex = (int)task.DeletionMode;
            ConflictBox.SelectedIndex = (int)task.ConflictPolicy;
            AutoStartCheck.IsChecked = duplicate ? false : task.StartWithApplication;
            IncludePatternsBox.Text = string.Join(Environment.NewLine, task.Filters?.IncludePatterns ?? Array.Empty<string>());
            ExcludePatternsBox.Text = string.Join(Environment.NewLine, task.Filters?.ExcludePatterns ?? Array.Empty<string>());
        }

        _unsavedChanges = new UnsavedChangesGuard(this, CaptureState);
    }

    public SyncTaskDefinition? Result { get; private set; }

    private void OnBrowseLeft(object sender, RoutedEventArgs e) => BrowseInto(LeftPathBox);

    private void OnBrowseRight(object sender, RoutedEventArgs e) => BrowseInto(RightPathBox);

    private static void BrowseInto(System.Windows.Controls.TextBox textBox)
    {
        var dialog = new OpenFolderDialog { Multiselect = false };
        if (dialog.ShowDialog() == true)
        {
            textBox.Text = dialog.FolderName;
        }
    }

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        try
        {
            Result = new SyncTaskDefinition
            {
                Id = _existingId ?? Guid.NewGuid(),
                Name = NameBox.Text.Trim(),
                LeftPath = LeftPathBox.Text.Trim(),
                RightPath = RightPathBox.Text.Trim(),
                Mode = (SyncMode)ModeBox.SelectedIndex,
                DeletionMode = (DeletionMode)DeletionBox.SelectedIndex,
                ConflictPolicy = (ConflictPolicy)ConflictBox.SelectedIndex,
                StartWithApplication = AutoStartCheck.IsChecked == true,
                Filters = new SyncFilterRules
                {
                    IncludePatterns = SplitPatterns(IncludePatternsBox.Text),
                    ExcludePatterns = SplitPatterns(ExcludePatternsBox.Text),
                },
            };
            Result.Validate();
            _unsavedChanges.MarkSaved();
            DialogResult = true;
        }
        catch (Exception exception)
        {
            ErrorText.Text = exception.Message;
        }
    }

    private static string[] SplitPatterns(string value) =>
        value.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private string CaptureState() => string.Join('\u001f',
        NameBox.Text,
        LeftPathBox.Text,
        RightPathBox.Text,
        ModeBox.SelectedIndex,
        DeletionBox.SelectedIndex,
        ConflictBox.SelectedIndex,
        AutoStartCheck.IsChecked,
        IncludePatternsBox.Text,
        ExcludePatternsBox.Text);
}
