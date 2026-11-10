using System.ComponentModel;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using PathEcho.Services;

namespace PathEcho.Dialogs;

public partial class HistoryDiagnosticsWindow : Window
{
    private readonly PathEchoRuntime _runtime;
    private CancellationTokenSource? _operation;

    public HistoryDiagnosticsWindow(PathEchoRuntime runtime)
    {
        _runtime = runtime;
        InitializeComponent();
        WindowBackdrop.Attach(this);
        ShowReports();
        Loaded += async (_, _) => { if (_runtime.HistoryReports.Count == 0) await CheckAsync(); };
    }

    private void ShowReports()
    {
        SummaryText.Text = HistoryDiagnosticPresentation.Summary(_runtime.HistoryReports);
        var text = new StringBuilder();
        foreach (var report in _runtime.HistoryReports)
        {
            var name = _runtime.Configuration.GameProfiles.FirstOrDefault(profile => profile.Id == report.ProfileId)?.Name ?? "游戏档案";
            text.AppendLine(name).AppendLine("读取目录：" + report.BackupRoot)
                .AppendLine($"操作 ID：{report.OperationId:N} · 耗时 {report.DurationMs} ms · {report.Outcome}");
            foreach (var item in report.Issues.Concat(report.Inspections).Where(item => item.ReasonCode != "READY").Take(100))
            {
                text.AppendLine("• " + HistoryDiagnosticPresentation.Reason(item.ReasonCode))
                    .AppendLine("  " + item.SnapshotDirectory)
                    .AppendLine($"  阶段 {item.Stage} · 原因 {item.ReasonCode}" + (item.HResult is { } code ? $" · 错误码 0x{code:X8}" : ""));
            }
            text.AppendLine();
        }
        DetailsBox.Text = text.Length == 0 ? "尚无检查记录。重新检查只读取目录与清单，不会复制、移动或删除备份。" : text.ToString();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var path = _runtime.HistoryReports.SelectMany(report => report.Issues.Concat(report.Inspections))
            .FirstOrDefault(item => item.ReasonCode != "READY")?.SnapshotDirectory ?? _runtime.HistoryReports.FirstOrDefault()?.BackupRoot;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.Directory.Exists(path))
            {
                ResultText.Text = "该目录当前不可访问，可从检查详情复制路径核对。";
                return;
            }
            var start = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add(path);
            System.Diagnostics.Process.Start(start);
        }
        catch (Exception exception) { ResultText.Text = "无法打开目录：" + exception.Message; }
    }

    private async void OnCheck(object sender, RoutedEventArgs e) => await CheckAsync();

    private async Task CheckAsync()
    {
        if (_operation is not null) return;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        Busy(true);
        ResultText.Text = "正在检查目录、清单和内容元数据…";
        try
        {
            await _runtime.CheckHistoryAsync(operation.Token);
            ShowReports();
            ResultText.Text = _runtime.HistoryReports.All(report => report.IsComplete)
                ? "检查结束。缺清单和缺内容的候选不会进入可回档版本。"
                : "检查未完成，已保留可观察结果；不能据此判断备份不存在。";
        }
        catch (OperationCanceledException) { ResultText.Text = "已取消检查，不能将不完整结果解释为没有备份。"; ShowReports(); }
        catch (Exception exception) { ResultText.Text = "检查未完成：" + exception.Message; }
        finally { _operation = null; Busy(false); }
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (_operation is not null) return;
        var picker = new Microsoft.Win32.SaveFileDialog { Title = "保存本地诊断包", Filter = "诊断包 (*.zip)|*.zip", FileName = $"PathEcho-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip" };
        if (picker.ShowDialog(this) != true) return;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        Busy(true);
        ResultText.Text = "正在保存诊断包…";
        try
        {
            await _runtime.ExportHistoryDiagnosticsAsync(picker.FileName, IncludePathsCheck.IsChecked == true, operation.Token);
            ResultText.Text = "已保存诊断 ZIP。请自行查看后分享；程序不会上传。";
        }
        catch (OperationCanceledException) { ResultText.Text = "导出已取消，原目标文件保持不变。"; }
        catch (Exception exception) { ResultText.Text = "导出失败：" + exception.Message; }
        finally { _operation = null; Busy(false); }
    }

    private void OnCancelCheck(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private void OnClosing(object? sender, CancelEventArgs e) => _operation?.Cancel();
    private void Busy(bool value)
    {
        CheckButton.IsEnabled = !value; ExportButton.IsEnabled = !value; IncludePathsCheck.IsEnabled = !value;
        CancelCheckButton.IsEnabled = value;
    }
}
