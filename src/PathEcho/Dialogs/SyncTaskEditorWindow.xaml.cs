using System.Windows;
using Microsoft.Win32;
using PathEcho.Core.Models;
using OpenFolderDialog = Microsoft.Win32.OpenFolderDialog;

namespace PathEcho.Dialogs;

public partial class SyncTaskEditorWindow : Window
{
    public SyncTaskEditorWindow()
    {
        InitializeComponent();
        WindowBackdrop.Attach(this);
        ModeBox.ItemsSource = new[] { "左 → 右", "右 → 左", "双向" };
        DeletionBox.ItemsSource = new[] { "不传播删除", "传播删除", "删除前备份" };
        ConflictBox.ItemsSource = new[] { "保留两份", "优先左侧", "优先右侧", "较新文件" };
        ModeBox.SelectedIndex = 0;
        DeletionBox.SelectedIndex = 0;
        ConflictBox.SelectedIndex = 0;
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
                Name = NameBox.Text.Trim(),
                LeftPath = LeftPathBox.Text.Trim(),
                RightPath = RightPathBox.Text.Trim(),
                Mode = (SyncMode)ModeBox.SelectedIndex,
                DeletionMode = (DeletionMode)DeletionBox.SelectedIndex,
                ConflictPolicy = (ConflictPolicy)ConflictBox.SelectedIndex,
                StartWithApplication = AutoStartCheck.IsChecked == true,
            };
            Result.Validate();
            DialogResult = true;
        }
        catch (Exception exception)
        {
            ErrorText.Text = exception.Message;
        }
    }
}
