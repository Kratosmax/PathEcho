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

    private static async Task RuntimeAsync()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        Func<string, bool> probe = _ => { Interlocked.Increment(ref count); entered.TrySetResult(); release.Wait(); probeExited.TrySetResult(); return true; };
        var runtime = (PathEchoRuntime)Activator.CreateInstance(typeof(PathEchoRuntime), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { false, false, probe }, null)!;
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
        var canceledRuntime = (PathEchoRuntime)Activator.CreateInstance(typeof(PathEchoRuntime), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { false, false, (Func<string, bool>)(_ => { enteredCanceled.TrySetResult(); releaseCanceled.Wait(); return true; }) }, null)!;
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
