using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PathEcho.Core.Models;
using PathEcho.Services;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Point = System.Windows.Point;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using BindingFlags = System.Reflection.BindingFlags;

internal static class Program
{
    private static readonly List<object> Results = [];
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var exit = 0;
        app.Startup += async (_, _) =>
        {
            try
            {
                Directory.CreateDirectory("temp/desktop-tests");
                LoadResources(app);
                await LayoutAsync();
                await RuntimeAsync();
                await ImportedProfileHistoryAsync();
                await MissingManifestDiagnosticsAsync();
                await NotificationAsync();
                var output = JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(args.ElementAtOrDefault(0) ?? "temp/desktop-tests/results.json", output);
                Console.WriteLine(output);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
            finally { app.Shutdown(); }
        };
        app.Run();
        return exit;
    }

    private static void LoadResources(Application app)
    {
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var doc = XDocument.Load("src/PathEcho/App.xaml");
        var dictionary = new XElement(ns + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"), doc.Root!.Element(ns + "Application.Resources")!.Elements());
        app.Resources.MergedDictionaries.Add((ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString().Replace("/Assets/", "/PathEcho;component/Assets/", StringComparison.Ordinal)));
    }

    private static async Task LayoutAsync()
    {
        await using var runtime = new PathEchoRuntime(true, true);
        await runtime.InitializeAsync();
        var window = new PathEcho.MainWindow(runtime) { Width = 920, Height = 620 };
        window.Show();
        foreach (var page in new[] { "Sync", "History", "Game" })
        {
            window.SelectPreviewView(page);
            await Idle();
            var grid = (DataGrid)window.FindName(page == "Sync" ? "SyncGrid" : page == "History" ? "HistoryGrid" : "GameGrid");
            Results.Add(new { probe = "grid_columns", page, columns = grid.Columns.Select(c => new { header = c.Header?.ToString(), actualWidth = c.ActualWidth, minWidth = c.MinWidth, visibility = c.Visibility.ToString() }), scroll = Descendants<ScrollViewer>(grid).Select(s => new { s.ScrollableWidth, s.HorizontalOffset, visibility = s.ComputedHorizontalScrollBarVisibility.ToString() }) });
            var last = grid.Columns.Last();
            grid.ScrollIntoView(grid.Items[0], last);
            await Idle();
            var cell = last.GetCellContent(grid.Items[0]);
            Assert(cell is not null && cell.ActualWidth > 0, page + " final action is unreachable after horizontal scroll.");
            await CaptureAsync(window, "temp/desktop-tests/" + page + "-920-right.png");
        }
        window.SelectPreviewView("Settings");
        await Idle();
        var save = Descendants<Button>(window).Single(b => b.Content as string == "保存设置");
        var top = save.TranslatePoint(new Point(), window).Y;
        var settings = (FrameworkElement)window.FindName("SettingsView");
        var scroll = Descendants<ScrollViewer>(settings).First(s => s.ScrollableHeight > 0);
        scroll.ScrollToEnd();
        await Idle();
        Assert(Math.Abs(save.TranslatePoint(new Point(), window).Y - top) < 0.5 && save.IsVisible, "Settings actions moved with the document.");
        await CaptureAsync(window, "temp/desktop-tests/Settings-920-bottom.png");
        var routes = (DataGrid)window.FindName("UpdateRoutesGrid");
        routes.BringIntoView();
        await Idle();
        routes.ScrollIntoView(routes.Items[0]); routes.UpdateLayout();
        var priority = Descendants<ComboBox>(routes).First();
        priority.IsDropDownOpen = true; await Idle();
        Assert(priority.IsDropDownOpen && priority.Template.FindName("PART_Popup", priority) is Popup { IsOpen: true }, "Styled ComboBox popup failed to open.");
        priority.IsDropDownOpen = false;
        priority.SelectedItem = 6;
        await Idle();
        Assert(window.UpdateRoutes[0].Priority == 6, "Priority TwoWay binding did not update the row.");
        priority.Focus();
        var previous = priority.SelectedIndex;
        var source = PresentationSource.FromVisual(priority)!;
        priority.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Down) { RoutedEvent = Keyboard.KeyDownEvent });
        await Idle();
        Assert(priority.SelectedIndex != previous, "ComboBox Down key did not select the next item.");
        priority.IsDropDownOpen = true;
        priority.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
        await Idle();
        Assert(!priority.IsDropDownOpen, "ComboBox Escape key did not close the popup.");
        Results.Add(new { probe = "settings_and_combobox", fixedFooter = true, popup = true, priorityBinding = true, downKey = true, escapeKey = true });
        runtime.SyncTasks.Clear(); window.SelectPreviewView("Sync"); await Idle();
        await CaptureAsync(window, "temp/desktop-tests/Sync-920-empty.png");
        window.Close(); // The harness uses a plain Application, so production close-to-tray is not active.
    }

    private static async Task MissingManifestDiagnosticsAsync()
    {
        var root = Path.GetFullPath(Path.Combine("temp", "desktop-tests", "missing-manifest-" + Guid.NewGuid().ToString("N")));
        var id = Guid.NewGuid(); var backup = Path.Combine(root, "cloud");
        var profile = new GameBackupProfile
        {
            Id = id,
            Name = "清单同步验证",
            SaveDirectory = Path.Combine(root, "save"),
            BackupDirectory = backup,
            Triggers = BackupTrigger.ChangedFiles
        };
        var snapshot = Path.Combine(backup, id.ToString("N"), "snapshots", "20261110-synchronizing");
        Directory.CreateDirectory(snapshot);
        var diagnostics = Path.Combine(root, "diagnostics");
        await using var runtime = (PathEchoRuntime)Activator.CreateInstance(typeof(PathEchoRuntime), BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { false, false, (Func<string, bool>)Directory.Exists, diagnostics }, null)!;
        typeof(PathEchoRuntime).GetProperty(nameof(PathEchoRuntime.Configuration))!.SetValue(runtime,
            new AppConfiguration { EnableDebugLogging = false, GameProfiles = [profile] });
        runtime.GameProfiles.Add(new GameProfileRow(profile));
        AppLogger.Configure(false);
        await runtime.CheckHistoryAsync();
        Assert(runtime.History.Count == 0 && runtime.HistoryReports.Single().Summary.AwaitingManifest == 1,
            "An existing snapshot without its manifest was hidden or became an executable version.");
        var logPath = Path.Combine(diagnostics, "diagnostics.jsonl");
        var periodicMethod = typeof(PathEchoRuntime).GetMethod("RefreshHistorySafelyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)periodicMethod.Invoke(runtime, new object[] { CancellationToken.None, true })!;
        var afterFirstPoll = File.ReadAllLines(logPath).Length;
        await (Task)periodicMethod.Invoke(runtime, new object[] { CancellationToken.None, true })!;
        Assert(File.ReadAllLines(logPath).Length == afterFirstPoll, "Repeated periodic scan bypassed default journal deduplication.");
        Assert(File.Exists(logPath) && (await File.ReadAllTextAsync(logPath)).Contains("MANIFEST_MISSING", StringComparison.Ordinal),
            "Debug-off mode lost the missing-manifest evidence.");
        var window = new PathEcho.MainWindow(runtime) { Width = 1180, Height = 760 };
        window.Show(); window.SelectPreviewView("History"); await Idle();
        Assert(((TextBlock)window.FindName("HistoryEmptyTitle")).Text.Contains("清单", StringComparison.Ordinal),
            "Actual history view still described a missing manifest as no backups.");
        Assert(((TextBlock)window.FindName("HistoryDiagnosticSummary")).Text.Contains("1 个等待备份清单", StringComparison.Ordinal),
            "Actual history view omitted pending metadata count.");
        await CaptureAsync(window, "temp/desktop-tests/missing-manifest-history.png");
        window.Width = 920; window.Height = 620; await Idle();
        await CaptureAsync(window, "temp/desktop-tests/missing-manifest-history-920.png");
        var dialog = new PathEcho.Dialogs.HistoryDiagnosticsWindow(runtime) { Owner = window };
        dialog.Show(); await Idle();
        Assert(((TextBox)dialog.FindName("DetailsBox")).Text.Contains("MANIFEST_MISSING", StringComparison.Ordinal),
            "Diagnostic window did not show the structured reason.");
        Assert(((System.Windows.Controls.CheckBox)dialog.FindName("IncludePathsCheck")).IsChecked != true,
            "Original paths were opted in by default.");
        await CaptureAsync(dialog, "temp/desktop-tests/missing-manifest-diagnostic.png");
        var zip = Path.Combine(root, "diagnostics.zip");
        await runtime.ExportHistoryDiagnosticsAsync(zip);
        using (var archive = System.IO.Compression.ZipFile.OpenRead(zip))
        {
            Assert(archive.Entries.Count == 5, "Runtime export included unexpected files.");
            foreach (var entry in archive.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                var text = await reader.ReadToEndAsync();
                Assert(!text.Contains(root, StringComparison.OrdinalIgnoreCase) && !text.Contains(id.ToString("N"), StringComparison.OrdinalIgnoreCase),
                    "Runtime export leaked the source path or identity.");
            }
        }
        dialog.Close();
        var bytes = System.Text.Encoding.UTF8.GetBytes("progress");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var manifest = new PathEcho.Core.Backup.SnapshotManifest
        {
            ProfileId = id,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Trigger = "fixture",
            Files = [new() { RelativePath = "slot.sav", Sha256 = hash, Length = bytes.Length, LastWriteUtcTicks = DateTime.UtcNow.Ticks }],
        };
        await File.WriteAllTextAsync(Path.Combine(snapshot, "manifest.json"), System.Text.Json.JsonSerializer.Serialize(manifest));
        await runtime.CheckHistoryAsync();
        Assert(runtime.History.Count == 1 && !runtime.History[0].IsContentAvailable && runtime.HistoryReports.Single().Summary.AwaitingContent == 1,
            "Arriving metadata did not transition to pending content.");
        var blob = PathEcho.Core.Backup.SnapshotContent.GetObjectPath(Path.Combine(backup, id.ToString("N")), hash);
        Directory.CreateDirectory(Path.GetDirectoryName(blob)!); await File.WriteAllBytesAsync(blob, bytes);
        await runtime.CheckHistoryAsync(); await Idle();
        Assert(runtime.History.Single().IsContentAvailable && runtime.HistoryReports.Single().Summary.ReadyVersions == 1,
            "Arriving content did not recover the actual Runtime/UI state.");
        ((TextBox)window.FindName("HistorySearchBox")).Text = "not-a-matching-game"; await Idle();
        Assert(((FrameworkElement)window.FindName("HistoryNoResults")).Visibility == Visibility.Visible,
            "Filtered-out results were confused with missing backup data.");
        Assert((await File.ReadAllTextAsync(logPath)).Contains("RESULT_FILTERED_OUT", StringComparison.Ordinal),
            "Filtered-out results had no diagnostic evidence.");
        window.Close();
        Assert(!Directory.Exists(profile.SaveDirectory), "Diagnostics touched the actual save path.");
        Results.Add(new
        {
            probe = "missing_manifest_real_ui_and_export",
            candidate = 1,
            waitingManifest = 1,
            noExecutableVersionWithoutManifest = true,
            debugOffEvidence = true,
            defaultRedactedZip = true,
            metadataArrivesThenContentReady = true,
            filterReasonRecorded = true
        });
    }

    private static async Task ImportedProfileHistoryAsync()
    {
        var root = Path.GetFullPath(Path.Combine("temp", "desktop-tests", "import-history-" + Guid.NewGuid().ToString("N")));
        var sourceSave = Path.Combine(root, "old-save");
        var oldBackup = Path.Combine(root, "old-backup");
        var newBackup = Path.Combine(root, "new-backup");
        Directory.CreateDirectory(sourceSave);
        await File.WriteAllTextAsync(Path.Combine(sourceSave, "slot.sav"), "switch progress");
        var original = new GameBackupProfile
        {
            Id = Guid.Parse("9b74b71f-244f-47da-84c6-7e0abff7f3e3"),
            Name = "switch2模拟器存档",
            SaveDirectory = sourceSave,
            BackupDirectory = oldBackup,
            IncludeOtherDeviceBackups = true,
            Triggers = BackupTrigger.ChangedFiles,
        };
        await new PathEcho.Core.Backup.SnapshotStore().CreateAsync(original.Id, sourceSave, oldBackup, "导入回归");
        var export = Path.Combine(root, "game.pathecho-game.json");
        await PathEcho.Core.Storage.GameProfileTransfer.ExportAsync(original, oldBackup, export);
        var draft = await PathEcho.Core.Storage.GameProfileTransfer.ReadImportDraftAsync(export, []);
        Directory.Move(oldBackup, newBackup);
        var nestedRoot = Path.Combine(newBackup, "PathEcho", original.Id.ToString("D"));
        Directory.CreateDirectory(Path.GetDirectoryName(nestedRoot)!);
        Directory.Move(Path.Combine(newBackup, original.Id.ToString("N")), nestedRoot);
        var dialog = new PathEcho.Dialogs.GameProfileEditorWindow(draft, importing: true);
        ((TextBox)dialog.FindName("SavePathBox")).Text = Path.Combine(root, "new-local-save");
        ((TextBox)dialog.FindName("BackupPathBox")).Text = newBackup;
        _ = dialog.Dispatcher.BeginInvoke(() => ((Button)dialog.FindName("SubmitButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), DispatcherPriority.ApplicationIdle);
        Assert(dialog.ShowDialog() == true && dialog.Result is not null, "Import confirmation did not produce a profile.");
        var profile = dialog.Result!;
        Assert(profile.LinkedBackupProfileIds.Contains(original.Id) && profile.Id == draft.Id, "Confirmation dropped source identity after path edits.");
        var config = new AppConfiguration { DefaultBackupDirectory = Path.Combine(root, "default-backup"), GameProfiles = [profile] };
        var store = new PathEcho.Core.Storage.JsonConfigurationStore(Path.Combine(root, "configuration.json"));
        await store.SaveAsync(config);
        config = await store.LoadAsync();
        await using var runtime = (PathEchoRuntime)Activator.CreateInstance(typeof(PathEchoRuntime), BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { false, false, (Func<string, bool>)Directory.Exists, Path.Combine(root, "diagnostics") }, null)!;
        // No Initialize: inject only a test configuration, avoiding real config, startup registration and monitors.
        typeof(PathEchoRuntime).GetProperty(nameof(PathEchoRuntime.Configuration))!.SetValue(runtime, config);
        runtime.GameProfiles.Add(new GameProfileRow(config.GameProfiles.Single()));
        await runtime.RefreshHistoryAsync();
        Assert(runtime.History.Count == 1 && runtime.History[0].Version.Manifest.ProfileId == original.Id,
            "Runtime failed to read the imported source after confirmation, path changes and reload.");
        Assert(runtime.History[0].Profile.SaveDirectory == profile.SaveDirectory && runtime.History[0].IsContentAvailable,
            "Imported history did not target the new local save directory.");
        var window = new PathEcho.MainWindow(runtime) { Width = 1180, Height = 760 };
        window.Show(); window.SelectPreviewView("History"); await Idle();
        await CaptureAsync(window, "temp/desktop-tests/imported-history.png");
        window.Close();
        Assert(!Directory.Exists(profile.SaveDirectory), "History lookup created or changed the local save directory.");
        Results.Add(new
        {
            probe = "import_confirm_change_paths_reload_history",
            actualImportDialog = true,
            linkedSourcePreserved = true,
            backupPathChanged = true,
            savePathChanged = true,
            nestedHyphenatedBackup = true,
            historyVersions = runtime.History.Count,
            noRealConfiguration = true
        });
    }

    private static async Task RuntimeAsync()
    {
        var diagnosticRoot = Path.GetFullPath(Path.Combine("temp", "desktop-tests", "runtime-diagnostics-" + Guid.NewGuid().ToString("N")));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        Func<string, bool> probe = _ => { Interlocked.Increment(ref count); entered.TrySetResult(); release.Wait(); probeExited.TrySetResult(); return true; };
        var runtime = (PathEchoRuntime)Activator.CreateInstance(typeof(PathEchoRuntime), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { false, false, probe, Path.Combine(diagnosticRoot, "normal") }, null)!;
        var row = new SyncTaskRow(new PathEcho.Core.Models.SyncTaskDefinition { LeftPath = "mock-left", RightPath = "mock-right" });
        runtime.SyncTasks.Add(row);
        var first = runtime.RefreshSyncDirectoryStatesAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await first.WaitAsync(TimeSpan.FromSeconds(3)); // The OS probe is still held, but the caller's two-second wait ends.
        await runtime.RefreshSyncDirectoryStatesAsync();
        Assert(count == 1, "Timeout opened the gate to overlapping probes.");
        await runtime.DisposeAsync();
        release.Set();
        await probeExited.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitForRefreshCompletionAsync(runtime);
        Assert(count == 1 && !row.IsDirectoryStateKnown, "Shutdown continued the right probe or applied late results.");

        using var releaseCanceled = new ManualResetEventSlim();
        var enteredCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceledRuntime = (PathEchoRuntime)Activator.CreateInstance(typeof(PathEchoRuntime), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { false, false, (Func<string, bool>)(_ => { enteredCanceled.TrySetResult(); releaseCanceled.Wait(); return true; }), Path.Combine(diagnosticRoot, "canceled") }, null)!;
        canceledRuntime.SyncTasks.Add(new SyncTaskRow(new PathEcho.Core.Models.SyncTaskDefinition { LeftPath = "mock-left", RightPath = "mock-right" }));
        using var cts = new CancellationTokenSource();
        var canceled = canceledRuntime.RefreshSyncDirectoryStatesAsync(cts.Token);
        await enteredCanceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cts.Cancel();
        var observedCancellation = false;
        try { await canceled.WaitAsync(TimeSpan.FromSeconds(1)); } catch (OperationCanceledException) { observedCancellation = true; }
        Assert(observedCancellation, "Caller cancellation did not interrupt the bounded wait.");
        await canceledRuntime.DisposeAsync(); releaseCanceled.Set(); await WaitForRefreshCompletionAsync(canceledRuntime);
        Results.Add(new { probe = "runtime_directory_lifecycle", boundedWait = true, singleFlight = true, noRightProbeAfterStop = true, noLateApply = true, callerCancellation = true });
    }

    private static async Task NotificationAsync()
    {
        var assembly = typeof(PathEcho.MainWindow).Assembly;
        var type = assembly.GetType("PathEcho.Notifications.BackupNotificationWindow")!;
        var requestType = assembly.GetType("PathEcho.Notifications.BackupNotificationRequest")!;
        var window = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { new BackupNotificationSettings() }, null)!;
        var request = Activator.CreateInstance(requestType, "Probe game", true, "Probe detail")!;
        var present = type.GetMethod("Present", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var timer = (DispatcherTimer)type.GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var ticks = 0; timer.Tick += (_, _) => ticks++;
        present.Invoke(window, new[] { request });
        var rows = (StackPanel)window.FindName("QueueRows");
        var card = rows.Children[0];
        await Task.Delay(1100);
        var sameCard = ReferenceEquals(card, rows.Children[0]);
        var idleTicks = ticks;
        Assert(sameCard && idleTicks == 0, "Notification idle render behavior differs from expectation.");
        await Task.Delay(4300);
        Assert(!window.IsVisible, "Notification did not expire after five seconds.");
        present.Invoke(window, new[] { request }); present.Invoke(window, new[] { request });
        Assert(rows.Children.Count == 1 && Descendants<TextBlock>(rows).Any(t => t.Text.Contains("重复 2 次")), "Notification duplicate count failed after reuse.");
        window.Close();
        Assert(!timer.IsEnabled, "Closed notification retained its timer.");
        Results.Add(new { probe = "notification_actual_wpf", idleTicksAt1100ms = idleTicks, sameCardAt1100ms = sameCard, expiredAt5400ms = true, duplicateCount = 2, timerAfterClose = timer.IsEnabled });
    }

    private static async Task WaitForRefreshCompletionAsync(PathEchoRuntime runtime)
    {
        var running = typeof(PathEchoRuntime).GetField("_directoryRefreshRunning", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while ((int)running.GetValue(runtime)! != 0)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Directory refresh did not finish after releasing the controlled probe.");
            await Task.Delay(10);
        }
    }

    private static async Task CaptureAsync(Window window, string path)
    {
        await Idle();
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        await using var output = File.Create(path); encoder.Save(output);
    }

    private static async Task Idle() { await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Assert(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
