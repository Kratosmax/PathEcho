using System.Windows;
using PathEcho.Core.Sync;

namespace PathEcho.Dialogs;

public partial class SyncPreviewWindow : Window
{
    public SyncPreviewWindow(string taskName, SyncPreviewResult preview)
    {
        InitializeComponent();
        WindowBackdrop.Attach(this);
        HeadingText.Text = $"{taskName} · 同步预演";
        SummaryText.Text = $"将复制 {preview.CopiedFiles} 个、删除 {preview.DeletedFiles} 个、处理冲突 {preview.Conflicts} 个文件；预演不会修改目录。";
        ActionsGrid.ItemsSource = preview.Actions.Select(action => new SyncPreviewRow(action)).ToArray();
        ActionsGrid.Visibility = preview.Actions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Visibility = preview.Actions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}

public sealed class SyncPreviewRow
{
    public SyncPreviewRow(SyncAction action)
    {
        RelativePath = action.RelativePath;
        Reason = action.Reason ?? string.Empty;
        Action = action.Kind switch
        {
            SyncActionKind.CopyLeftToRight => "复制 左 → 右",
            SyncActionKind.CopyRightToLeft => "复制 右 → 左",
            SyncActionKind.DeleteLeft => "删除左侧",
            SyncActionKind.DeleteRight => "删除右侧",
            _ => "保留冲突副本",
        };
    }

    public string Action { get; }
    public string RelativePath { get; }
    public string Reason { get; }
}
