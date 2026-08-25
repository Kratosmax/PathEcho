using PathEcho.Core.Backup;
using PathEcho.Core.GameCatalog;
using PathEcho.Core.Models;
using PathEcho.Core.Restore;
using PathEcho.Core.Storage;
using PathEcho.Core.Sync;
using PathEcho.Core.Update;
using PathEcho.Platform.Windows.Restore;
using PathEcho.Platform.Windows.Instance;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

var repositoryRoot = FindRepositoryRoot();
var testRoot = Path.Combine(repositoryRoot, "temp", "smoke", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);

var tests = new (string Name, Func<Task> Run)[]
{
    ("配置可原子保存并读取", TestConfigurationStoreAsync),
    ("单向同步可复制并在删除前备份", TestOneWaySyncAsync),
    ("双向冲突默认保留两份", TestBidirectionalConflictAsync),
    ("游戏快照可去重、浏览并清理旧版本", TestSnapshotStoreAsync),
    ("备份失败会完整暂存并恢复写入", TestBackupRetryRecoveryAsync),
    ("备份重试每批询问且停止时保留完整副本", TestBackupRetryPromptAsync),
    ("备份重试可取消且清理失败不重复快照", TestBackupRetryCancellationAndPruneAsync),
    ("目录变化可合并触发自动同步", TestSyncMonitorAsync),
    ("同一任务并发同步会串行更新基线", TestSyncTaskRunnerSerializationAsync),
    ("同步过滤与预演共用规划且不修改目录", TestSyncFiltersAndPreviewAsync),
    ("同步运行历史有上限并原子持久化", TestSyncRunHistoryStoreAsync),
    ("只读表格不会进入编辑模式", TestReadOnlyDataGridContractAsync),
    ("设置保存会提交线路表格编辑", TestSettingsGridCommitContractAsync),
    ("未保存编辑在关闭或离开页面前会确认", TestUnsavedChangesContractAsync),
    ("存档历史可筛选且游戏支持双击编辑", TestGameHistoryAndEditingContractAsync),
    ("失败的界面操作不会覆盖成成功状态", TestUiActionResultContractAsync),
    ("重复启动会激活主实例且不会错误释放锁", TestSingleInstanceCoordinatorAsync),
    ("更新事务锁可跨线程安全交接且阻止并发更新", TestUpdateTransactionGateAsync),
    ("更新文件操作可等待短暂占用并报告长期占用路径", TestUpdateFileOperationAsync),
    ("更新器离开安装目录后可移动整个目录", TestUpdaterWorkingDirectoryAsync),
    ("Lite 安装器正确检测 x64 Desktop Runtime", TestLiteInstallerRuntimeDetectionContractAsync),
    ("游戏文件变化可触发备份并限制重点备份频率", TestGameBackupMonitorAsync),
    ("整目录与正则文件回档可事务恢复", TestSnapshotRestoreAsync),
    ("Restart Manager 可识别占用且保护当前进程", TestRestartManagerAsync),
    ("备份目录可迁移、发现并导入", TestBackupDirectoryManagerAsync),
    ("更新线路可规范化并稳定排序", TestUpdateRoutePlanningAsync),
    ("签名更新清单可验证并拒绝篡改", TestUpdateManifestSignatureAsync),
    ("签名游戏目录可代理获取、识别并回退可信缓存", TestGameCatalogAsync),
    ("更新下载可故障转移并清理失败暂存", TestUpdatePackageDownloadAsync),
    ("更新器拒绝路径穿越包", TestUpdaterPackageValidationAsync),
    ("更新交接会复制依赖、握手并记录失败", TestUpdateHandoffContractAsync),
    ("正式构建会用包内更新器验证两种通道", TestReleasePackageVerificationContractAsync),
};

try
{
    foreach (var test in tests)
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }

    Console.WriteLine($"全部通过：{tests.Length} 项");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"FAIL {exception.Message}");
    Console.Error.WriteLine(exception);
    return 1;
}
finally
{
    DeleteTree(testRoot);
}

Task TestLiteInstallerRuntimeDetectionContractAsync()
{
    var installerPath = Path.Combine(repositoryRoot, "build", "PathEcho.iss");

    var installer = File.ReadAllText(installerPath);

    True(installer.Contains("RegGetValueNames", StringComparison.Ordinal), "Lite 安装器未枚举 .NET 运行时版本值名。");
    False(installer.Contains("RegGetSubkeyNames", StringComparison.Ordinal), "Lite 安装器仍把 .NET 运行时版本误当成注册表子项。");
    foreach (var root in new[] { "HKLM64", "HKLM32", "HKCU64", "HKCU32" })
    {
        True(installer.Contains($"HasDesktopRuntime8({root})", StringComparison.Ordinal), $"Lite 安装器未检查 {root} 注册表视图。");
    }

    return Task.CompletedTask;
}

async Task TestConfigurationStoreAsync()
{
    var path = Path.Combine(testRoot, "configuration", "settings.json");
    var task = new SyncTaskDefinition
    {
        Name = "测试同步",
        LeftPath = Path.Combine(testRoot, "configuration-left"),
        RightPath = Path.Combine(testRoot, "configuration-right"),
    };
    var expected = new AppConfiguration
    {
        StartWithWindows = false,
        StartMinimized = true,
        CheckForUpdates = false,
        EnableDebugLogging = true,
        DefaultBackupDirectory = Path.Combine(testRoot, "configuration-backups"),
        SyncTasks = new[] { task },
        UpdateNetwork = new UpdateNetworkOptions
        {
            UrlRoutes = new[]
            {
                UpdateUrlRoute.Direct,
                new UpdateUrlRoute { BaseUrl = "https://proxy.example/github", Priority = 8 },
            },
            HttpProxy = "http://127.0.0.1:7890",
        },
    };
    var store = new JsonConfigurationStore(path);

    var verified = await store.SaveAndVerifyAsync(expected);
    var actual = await new JsonConfigurationStore(path).LoadAsync();

    False(verified.StartWithWindows, "写入校验结果丢失了开机自启设置。");
    False(actual.StartWithWindows, "重载后开机自启设置未保持一致。");
    True(actual.StartMinimized, "重载后最小化启动设置未保持一致。");
    False(actual.CheckForUpdates, "重载后自动更新设置未保持一致。");
    Equal(1, actual.SyncTasks.Count, "配置任务数量不正确。");
    Equal(task.Id, actual.SyncTasks[0].Id, "配置任务 ID 未保持一致。");
    True(actual.EnableDebugLogging, "Debug 日志开关未保持一致。");
    Equal(expected.DefaultBackupDirectory, actual.DefaultBackupDirectory, "默认备份目录未保持一致。");
    Equal(2, actual.UpdateNetwork.UrlRoutes.Count, "更新线路配置未保持一致。");
    Equal("http://127.0.0.1:7890", actual.UpdateNetwork.HttpProxy!, "HTTP 代理配置未保持一致。");
    False(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any(), "配置保存留下了暂存文件。");

    var legacyPath = Path.Combine(testRoot, "configuration", "legacy.json");
    await File.WriteAllTextAsync(legacyPath, "{\"schemaVersion\":1}");
    var legacy = await new JsonConfigurationStore(legacyPath).LoadAsync();
    Equal(1, legacy.UpdateNetwork.UrlRoutes.Count, "旧配置未恢复直连更新线路。");
    True(legacy.UpdateNetwork.UrlRoutes[0].IsDirect, "旧配置恢复的更新线路不是直连。");
    False(legacy.EnableDebugLogging, "旧配置错误启用了 Debug 日志。");
}

async Task TestOneWaySyncAsync()
{
    var root = Path.Combine(testRoot, "one-way");
    var left = Path.Combine(root, "left");
    var right = Path.Combine(root, "right");
    var vault = Path.Combine(root, "vault");
    Directory.CreateDirectory(left);
    await File.WriteAllTextAsync(Path.Combine(left, "save.dat"), "version-one");
    Directory.CreateDirectory(right);
    await File.WriteAllTextAsync(Path.Combine(right, "extra.tmp"), "remove-me");
    var task = new SyncTaskDefinition
    {
        Name = "单向测试",
        LeftPath = left,
        RightPath = right,
        DeletionMode = DeletionMode.BackupThenPropagate,
    };
    var engine = new SyncEngine(vault);

    var first = await engine.RunAsync(task, SyncBaseline.Empty);
    Equal("version-one", await File.ReadAllTextAsync(Path.Combine(right, "save.dat")), "文件未复制到右侧。");
    False(File.Exists(Path.Combine(right, "extra.tmp")), "首次同步未清理目标端多余文件。");

    File.Delete(Path.Combine(left, "save.dat"));
    var second = await engine.RunAsync(task, first.Baseline);
    False(File.Exists(Path.Combine(right, "save.dat")), "删除未传播到右侧。");
    Equal(1, second.DeletedFiles, "删除计数不正确。");
    True(Directory.EnumerateFiles(vault, "save.dat", SearchOption.AllDirectories).Any(), "删除前备份不存在。");
}

async Task TestBidirectionalConflictAsync()
{
    var root = Path.Combine(testRoot, "bidirectional");
    var left = Path.Combine(root, "left");
    var right = Path.Combine(root, "right");
    Directory.CreateDirectory(left);
    Directory.CreateDirectory(right);
    var leftFile = Path.Combine(left, "slot.sav");
    var rightFile = Path.Combine(right, "slot.sav");
    await File.WriteAllTextAsync(leftFile, "same");
    await File.WriteAllTextAsync(rightFile, "same");
    var task = new SyncTaskDefinition
    {
        Name = "双向测试",
        LeftPath = left,
        RightPath = right,
        Mode = SyncMode.Bidirectional,
        ConflictPolicy = ConflictPolicy.KeepBoth,
    };
    var engine = new SyncEngine(Path.Combine(root, "vault"));
    var initial = await engine.RunAsync(task, SyncBaseline.Empty);

    await File.WriteAllTextAsync(leftFile, "changed-left");
    await File.WriteAllTextAsync(rightFile, "changed-right");
    var result = await engine.RunAsync(task, initial.Baseline);

    Equal(1, result.Conflicts, "未识别双向冲突。");
    True(Directory.EnumerateFiles(left, "*.conflict-right-*", SearchOption.TopDirectoryOnly).Any(), "左侧缺少右侧冲突副本。");
    True(Directory.EnumerateFiles(right, "*.conflict-left-*", SearchOption.TopDirectoryOnly).Any(), "右侧缺少左侧冲突副本。");
}

async Task TestSnapshotStoreAsync()
{
    var root = Path.Combine(testRoot, "snapshot");
    var source = Path.Combine(root, "source");
    var backup = Path.Combine(root, "backup");
    Directory.CreateDirectory(Path.Combine(source, "nested"));
    await File.WriteAllTextAsync(Path.Combine(source, "first.sav"), "duplicate-content");
    await File.WriteAllTextAsync(Path.Combine(source, "nested", "second.sav"), "duplicate-content");
    var profileId = Guid.NewGuid();
    var store = new SnapshotStore();

    var first = await store.CreateAsync(profileId, source, backup, "测试");
    Equal(2, first.FileCount, "快照文件数量不正确。");
    Equal(1, first.NewObjectCount, "相同内容没有在首个快照内去重。");
    True(File.Exists(Path.Combine(first.SnapshotDirectory, "files", "first.sav")), "普通目录快照视图不存在。");
    True(File.Exists(Path.Combine(first.SnapshotDirectory, "manifest.json")), "快照清单不存在。");

    var second = await store.CreateAsync(profileId, source, backup, "测试");
    Equal(0, second.NewObjectCount, "未复用已有内容对象。");
    Equal(2, second.ReusedObjectCount, "复用对象数量不正确。");
    var removed = await store.PruneAsync(profileId, backup, 1);
    Equal(1, removed, "旧快照未按版本数清理。");
    Equal(1, Directory.EnumerateDirectories(Path.Combine(backup, profileId.ToString("N"), "snapshots")).Count(), "保留快照数量不正确。");
    Equal(1, Directory.EnumerateFiles(Path.Combine(backup, profileId.ToString("N"), "objects"), "*.blob", SearchOption.AllDirectories).Count(), "去重对象清理不正确。");
}

async Task TestBackupRetryRecoveryAsync()
{
    var root = Path.Combine(testRoot, "backup-retry-recovery");
    var save = Path.Combine(root, "save");
    var backup = Path.Combine(root, "backup");
    Directory.CreateDirectory(Path.Combine(save, "nested"));
    await File.WriteAllTextAsync(Path.Combine(save, "slot.sav"), "stable-save");
    await File.WriteAllTextAsync(Path.Combine(save, "nested", "profile.dat"), "profile");
    var profile = new GameBackupProfile { Name = "重试测试", SaveDirectory = save, Triggers = BackupTrigger.None };
    var snapshotStore = new FaultInjectingSnapshotStore(createFailures: 1);
    var stagingStore = new FaultInjectingStagingStore(createFailures: 2);
    var service = new GameBackupService(
        profile,
        backup,
        snapshotStore,
        stagingStore,
        new BackupRetryOptions { Delay = TimeSpan.Zero, AttemptsPerPrompt = 10 });

    var result = await service.CreateAsync(BackupTrigger.None);

    True(result is not null, "重试后没有创建快照。");
    Equal(3, stagingStore.CreateAttempts, "源存档读取失败后没有持续重试。");
    Equal(2, result!.FileCount, "暂存副本没有包含全部文件。");
    True(File.Exists(Path.Combine(result.SnapshotDirectory, "files", "nested", "profile.dat")), "嵌套存档未从暂存副本写入正式备份。");
    var profileTemp = Path.Combine(backup, "temp", profile.Id.ToString("N"));
    True(!Directory.Exists(profileTemp) || !Directory.EnumerateDirectories(profileTemp).Any(), "成功后没有清理本次临时副本。");
}

async Task TestBackupRetryPromptAsync()
{
    var root = Path.Combine(testRoot, "backup-retry-prompt");
    var save = Path.Combine(root, "save");
    var backup = Path.Combine(root, "backup");
    Directory.CreateDirectory(save);
    await File.WriteAllTextAsync(Path.Combine(save, "slot.sav"), "keep-me");
    var profile = new GameBackupProfile { Name = "提示测试", SaveDirectory = save, Triggers = BackupTrigger.None };
    var continuePrompts = 0;
    var recoveringStore = new FaultInjectingSnapshotStore(createFailures: 11);
    var recoveringService = new GameBackupService(
        profile,
        backup,
        recoveringStore,
        retryOptions: new BackupRetryOptions
        {
            Delay = TimeSpan.Zero,
            AttemptsPerPrompt = 10,
            ConfirmContinueAsync = (prompt, _) =>
            {
                continuePrompts++;
                Equal(BackupRetryStage.WritingBackup, prompt.Stage, "写入失败提示的阶段不正确。");
                Equal(10, prompt.FailedAttempts, "没有在连续失败十次时询问。");
                return Task.FromResult(true);
            },
        });

    var recovered = await recoveringService.CreateAsync(BackupTrigger.None);
    True(recovered is not null, "用户选择继续后未恢复备份。");
    Equal(1, continuePrompts, "每十次失败的询问次数不正确。");

    var stoppingStore = new FaultInjectingSnapshotStore(createFailures: int.MaxValue);
    var stoppingService = new GameBackupService(
        profile,
        backup,
        stoppingStore,
        retryOptions: new BackupRetryOptions
        {
            Delay = TimeSpan.Zero,
            AttemptsPerPrompt = 2,
            ConfirmContinueAsync = (_, _) => Task.FromResult(false),
        });
    try
    {
        await stoppingService.CreateAsync(BackupTrigger.None);
        throw new InvalidOperationException("用户停止后备份仍继续运行。");
    }
    catch (BackupRetryStoppedException exception)
    {
        True(exception.StagingDirectory is not null, "停止写入重试时没有报告临时副本位置。");
        True(File.Exists(Path.Combine(exception.StagingDirectory!, "files", "slot.sav")), "停止后未保留完整临时副本。");
        Equal("keep-me", await File.ReadAllTextAsync(Path.Combine(exception.StagingDirectory!, "files", "slot.sav")), "保留的临时副本内容不正确。");
    }
}

async Task TestBackupRetryCancellationAndPruneAsync()
{
    var root = Path.Combine(testRoot, "backup-retry-cancel");
    var save = Path.Combine(root, "save");
    var backup = Path.Combine(root, "backup");
    Directory.CreateDirectory(save);
    await File.WriteAllTextAsync(Path.Combine(save, "slot.sav"), "cancel-me");
    var profile = new GameBackupProfile { Name = "取消测试", SaveDirectory = save, Triggers = BackupTrigger.None };
    var failingStore = new FaultInjectingSnapshotStore(createFailures: int.MaxValue);
    var service = new GameBackupService(
        profile,
        backup,
        failingStore,
        retryOptions: new BackupRetryOptions { Delay = TimeSpan.FromSeconds(5), AttemptsPerPrompt = 10 });
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
    var stopwatch = Stopwatch.StartNew();
    await ExpectThrowsAsync<OperationCanceledException>(
        () => service.CreateAsync(BackupTrigger.None, cancellation.Token),
        "取消没有结束备份重试。");
    True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), "取消后仍等待完整的五秒重试间隔。");

    var pruneStore = new FaultInjectingSnapshotStore(pruneFailures: 2);
    var pruneService = new GameBackupService(
        profile,
        backup,
        pruneStore,
        retryOptions: new BackupRetryOptions { Delay = TimeSpan.Zero, AttemptsPerPrompt = 10 });
    var result = await pruneService.CreateAsync(BackupTrigger.None);
    True(result is not null, "旧版本清理恢复后没有返回已创建快照。");
    Equal(1, pruneStore.CreateAttempts, "旧版本清理失败时重复创建了快照。");
    Equal(3, pruneStore.PruneAttempts, "旧版本清理没有持续重试到成功。");
}

async Task TestSyncMonitorAsync()
{
    var root = Path.Combine(testRoot, "monitor");
    var left = Path.Combine(root, "left");
    var right = Path.Combine(root, "right");
    Directory.CreateDirectory(left);
    Directory.CreateDirectory(right);
    var task = new SyncTaskDefinition
    {
        Name = "监听测试",
        LeftPath = left,
        RightPath = right,
    };
    var engine = new SyncEngine(Path.Combine(root, "vault"));
    var baselineStore = new SyncBaselineStore(Path.Combine(root, "baselines"));
    await using var monitor = new SyncTaskMonitor(task, engine, baselineStore, TimeSpan.FromMilliseconds(100));
    var synchronized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    monitor.Synchronized += (_, _) =>
    {
        if (File.Exists(Path.Combine(right, "watched.sav")))
        {
            synchronized.TrySetResult();
        }
    };

    await monitor.StartAsync();
    await File.WriteAllTextAsync(Path.Combine(left, "watched.sav"), "observed");
    await synchronized.Task.WaitAsync(TimeSpan.FromSeconds(10));

    Equal("observed", await File.ReadAllTextAsync(Path.Combine(right, "watched.sav")), "监听变化未同步到目标目录。");
    True(File.Exists(Path.Combine(root, "baselines", $"{task.Id:N}.json")), "监听同步后未持久化基线。");
}

async Task TestSyncTaskRunnerSerializationAsync()
{
    var firstBaseline = new SyncBaseline(new Dictionary<string, SyncBaselineEntry>
    {
        ["first.sav"] = new(null, null),
    });
    var secondBaseline = new SyncBaseline(new Dictionary<string, SyncBaselineEntry>
    {
        ["second.sav"] = new(null, null),
    });
    var persisted = SyncBaseline.Empty;
    var loadCount = 0;
    var runCount = 0;
    var firstRunEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseFirstRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var runner = new SyncTaskRunner(
        (_, _) =>
        {
            Interlocked.Increment(ref loadCount);
            return Task.FromResult(persisted);
        },
        (_, baseline, _) =>
        {
            persisted = baseline;
            return Task.CompletedTask;
        },
        async (_, baseline, _, _) =>
        {
            var currentRun = Interlocked.Increment(ref runCount);
            if (currentRun == 1)
            {
                firstRunEntered.TrySetResult();
                await releaseFirstRun.Task;
                return new SyncRunResult(1, 0, 0, firstBaseline);
            }

            True(ReferenceEquals(firstBaseline, baseline), "第二次同步读取了第一次保存前的旧基线。");
            return new SyncRunResult(1, 0, 0, secondBaseline);
        });
    var definition = new SyncTaskDefinition
    {
        Name = "并发基线测试",
        LeftPath = Path.Combine(testRoot, "runner-left"),
        RightPath = Path.Combine(testRoot, "runner-right"),
    };

    var first = runner.RunAsync(definition, true);
    await firstRunEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var second = runner.RunAsync(definition, true);
    await Task.Delay(100);
    Equal(1, loadCount, "第一次同步完成前，第二次同步提前读取了基线。");
    releaseFirstRun.TrySetResult();
    await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

    Equal(2, loadCount, "并发同步没有各自读取基线。");
    True(ReferenceEquals(secondBaseline, persisted), "最终持久化的不是最新同步基线。");
}

Task TestReadOnlyDataGridContractAsync()
{
    XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
    var app = XDocument.Load(Path.Combine(repositoryRoot, "src", "PathEcho", "App.xaml"));
    var dataGridStyle = app.Descendants(presentation + "Style")
        .Single(element => (string?)element.Attribute("TargetType") == "DataGrid");
    var readOnlySetter = dataGridStyle.Elements(presentation + "Setter")
        .SingleOrDefault(element => (string?)element.Attribute("Property") == "IsReadOnly");
    Equal("True", (string?)readOnlySetter?.Attribute("Value") ?? string.Empty, "全局 DataGrid 没有保持只读。");

    var mainWindow = XDocument.Load(Path.Combine(repositoryRoot, "src", "PathEcho", "MainWindow.xaml"));
    var routesGrid = mainWindow.Descendants(presentation + "DataGrid")
        .Single(element => (string?)element.Attribute(x + "Name") == "UpdateRoutesGrid");
    Equal("False", (string?)routesGrid.Attribute("IsReadOnly") ?? string.Empty, "更新线路表没有保留编辑能力。");
    return Task.CompletedTask;
}

Task TestSettingsGridCommitContractAsync()
{
    var source = File.ReadAllText(Path.Combine(repositoryRoot, "src", "PathEcho", "MainWindow.xaml.cs"));
    True(
        source.Contains("!UpdateRoutesGrid.CommitEdit(DataGridEditingUnit.Cell, true)", StringComparison.Ordinal),
        "设置保存前没有提交线路表格的当前单元格编辑。");
    True(
        source.Contains("!UpdateRoutesGrid.CommitEdit(DataGridEditingUnit.Row, true)", StringComparison.Ordinal),
        "设置保存前没有提交线路表格的当前行编辑。");
    var browseStart = source.IndexOf("private async void OnBrowseDefaultBackup", StringComparison.Ordinal);
    var saveStart = source.IndexOf("private async void OnSaveSettings", browseStart, StringComparison.Ordinal);
    True(browseStart >= 0 && saveStart > browseStart, "无法定位默认备份目录选择处理器。");
    var browseHandler = source[browseStart..saveStart];
    True(browseHandler.Contains("更改并保存", StringComparison.Ordinal), "选择默认备份目录后没有要求用户确认迁移。");
    True(
        browseHandler.Contains("await SaveSettingsFromControlsAsync();", StringComparison.Ordinal),
        "确认默认备份目录后没有立即保存设置。");
    return Task.CompletedTask;
}

Task TestUnsavedChangesContractAsync()
{
    var guard = File.ReadAllText(Path.Combine(repositoryRoot, "src", "PathEcho", "Dialogs", "UnsavedChangesGuard.cs"));
    True(guard.Contains("window.Closing += OnClosing", StringComparison.Ordinal), "未保存状态没有接入窗口关闭事件。");
    True(guard.Contains("ConfirmDiscard", StringComparison.Ordinal), "未保存确认没有形成可复用逻辑。");

    foreach (var editor in new[] { "SyncTaskEditorWindow.xaml.cs", "GameProfileEditorWindow.xaml.cs" })
    {
        var source = File.ReadAllText(Path.Combine(repositoryRoot, "src", "PathEcho", "Dialogs", editor));
        True(source.Contains("new UnsavedChangesGuard", StringComparison.Ordinal), $"{editor} 没有启用未保存确认。");
        True(source.Contains("MarkSaved()", StringComparison.Ordinal), $"{editor} 保存成功后仍可能误报未保存。");
    }

    var mainWindow = File.ReadAllText(Path.Combine(repositoryRoot, "src", "PathEcho", "MainWindow.xaml.cs"));
    True(mainWindow.Contains("HasUnsavedSettings()", StringComparison.Ordinal), "设置页离开时没有检查未保存内容。");
    True(mainWindow.Contains("UnsavedChangesGuard.ConfirmDiscard(promptOwner)", StringComparison.Ordinal), "设置页没有复用未保存确认逻辑。");
    True(mainWindow.Contains("internal bool TryDiscardUnsavedSettings(Window promptOwner)", StringComparison.Ordinal), "设置页未保存门禁没有形成统一入口。");

    var app = File.ReadAllText(Path.Combine(repositoryRoot, "src", "PathEcho", "App.xaml.cs"));
    True(app.Contains("!_mainWindow.TryDiscardUnsavedSettings(_mainWindow)", StringComparison.Ordinal), "应用退出会绕过未保存设置提醒。");
    True(app.Contains("ExitApplication(discardUnsavedSettings: true)", StringComparison.Ordinal), "致命异常退出仍可能被未保存提醒阻断。");

    var updateWindow = File.ReadAllText(Path.Combine(repositoryRoot, "src", "PathEcho", "Dialogs", "UpdateWindow.xaml.cs"));
    True(updateWindow.Contains("!mainWindow.TryDiscardUnsavedSettings(this)", StringComparison.Ordinal), "自动更新退出会绕过未保存设置提醒。");
    True(updateWindow.Contains("app.ExitApplication(discardUnsavedSettings: true)", StringComparison.Ordinal), "更新交接完成后可能重复提示并阻断退出。");
    return Task.CompletedTask;
}

Task TestUiActionResultContractAsync()
{
    var source = File.ReadAllText(Path.Combine(repositoryRoot, "src", "PathEcho", "MainWindow.xaml.cs"));
    True(source.Contains("private async Task<bool> RunUiActionAsync", StringComparison.Ordinal), "界面操作包装器没有向调用方返回成功状态。");
    var handlerIndex = source.IndexOf("private async Task<bool> RunUiActionAsync", StringComparison.Ordinal);
    True(source.IndexOf("return false;", handlerIndex, StringComparison.Ordinal) >= 0, "界面操作失败后没有返回失败状态。");

    foreach (var (operation, status) in new[]
    {
        ("正在保存同步任务", "已更新"),
        ("正在创建任务副本", "已创建"),
        ("正在删除同步任务", "同步任务已删除"),
        ("正在删除游戏", "游戏已删除"),
    })
    {
        var operationIndex = source.IndexOf(operation, StringComparison.Ordinal);
        var statusIndex = source.IndexOf(status, operationIndex, StringComparison.Ordinal);
        var guardIndex = source.LastIndexOf("if (await RunUiActionAsync", statusIndex, StringComparison.Ordinal);
        True(operationIndex >= 0 && statusIndex >= 0 && guardIndex <= operationIndex && operationIndex - guardIndex < 100, $"{status} 仍可能在操作失败后显示。");
    }

    return Task.CompletedTask;
}

async Task TestSingleInstanceCoordinatorAsync()
{
    var instanceName = $"Local\\PathEcho.Smoke.{Guid.NewGuid():N}";
    var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using (var primary = SingleInstanceCoordinator.Create(instanceName, () => activated.TrySetResult()))
    {
        True(primary.IsPrimary, "第一个实例没有获得单实例锁。");
        using var secondary = SingleInstanceCoordinator.Create(instanceName, () => { });
        False(secondary.IsPrimary, "第二个实例错误获得了单实例锁。");
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    using var reopened = SingleInstanceCoordinator.Create(instanceName, () => { });
    True(reopened.IsPrimary, "主实例退出后无法重新获得单实例锁。");
}

async Task TestUpdateTransactionGateAsync()
{
    var name = $"Local\\PathEcho.UpdateGate.Test.{Guid.NewGuid():N}";
    using var updater = UpdateTransactionGate.BeginAcquire(name, TimeSpan.Zero);
    True(updater.IsAcquired, "更新器没有取得独占事务锁。");

    using var blocked = UpdateTransactionGate.BeginAcquire(name, TimeSpan.Zero);
    False(blocked.IsAcquired, "并发更新器错误取得了已占用的事务锁。");

    var waiting = UpdateTransactionGate.BeginAcquire(name, TimeSpan.FromSeconds(5));
    await Task.Delay(100);
    updater.Dispose();
    True(waiting.IsAcquired, "前一个事务结束后等待的更新器没有接管锁。");
    waiting.Dispose();

    using var reopened = UpdateTransactionGate.BeginAcquire(name, TimeSpan.Zero);
    True(reopened.IsAcquired, "更新完成后事务锁没有释放。");
}

async Task TestUpdateFileOperationAsync()
{
    var directory = Path.Combine(testRoot, "update-file-operation");
    Directory.CreateDirectory(directory);
    var source = Path.Combine(directory, "source.bin");
    var destination = Path.Combine(directory, "destination.bin");
    await File.WriteAllTextAsync(source, "PathEcho");

    var transientLock = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None);
    var release = Task.Run(async () =>
    {
        await Task.Delay(150);
        transientLock.Dispose();
    });
    await UpdateFileOperation.RetryAsync(
        "复制测试文件",
        source,
        () => File.Copy(source, destination, false),
        maximumAttempts: 8,
        retryDelay: TimeSpan.FromMilliseconds(50));
    await release;
    Equal("PathEcho", await File.ReadAllTextAsync(destination), "短暂占用释放后文件操作没有成功。");

    using var persistentLock = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None);
    try
    {
        await UpdateFileOperation.RetryAsync(
            "复制测试文件",
            source,
            () => File.Copy(source, destination, true),
            maximumAttempts: 2,
            retryDelay: TimeSpan.FromMilliseconds(10));
        throw new InvalidOperationException("长期文件占用没有让更新操作失败。");
    }
    catch (UpdateFileAccessException exception)
    {
        Equal("复制测试文件", exception.Stage, "文件占用错误丢失了更新阶段。");
        Equal(source, exception.AffectedPath, "文件占用错误丢失了具体路径。");
    }
}

Task TestUpdaterWorkingDirectoryAsync()
{
    var root = Path.Combine(testRoot, "updater-working-directory");
    var target = Path.Combine(root, "install");
    var backup = Path.Combine(root, "backup");
    Directory.CreateDirectory(target);
    var originalDirectory = Environment.CurrentDirectory;
    try
    {
        Environment.CurrentDirectory = target;
        var blocked = false;
        try
        {
            Directory.Move(target, backup);
        }
        catch (IOException)
        {
            blocked = true;
        }

        True(blocked, "Windows 未复现当前工作目录阻止安装目录移动的前置条件。");
        Environment.CurrentDirectory = repositoryRoot;
        Directory.Move(target, backup);
        True(Directory.Exists(backup), "更新器离开安装目录后仍无法移动安装目录。");
    }
    finally
    {
        Environment.CurrentDirectory = originalDirectory;
        DeleteTree(root);
    }

    return Task.CompletedTask;
}

async Task TestSyncFiltersAndPreviewAsync()
{
    var root = Path.Combine(testRoot, "sync-filter-preview");
    var left = Path.Combine(root, "left");
    var right = Path.Combine(root, "right");
    Directory.CreateDirectory(Path.Combine(left, "Saves"));
    Directory.CreateDirectory(right);
    await File.WriteAllTextAsync(Path.Combine(left, "Saves", "slot.sav"), "save");
    await File.WriteAllTextAsync(Path.Combine(left, "Saves", "trace.tmp"), "ignore");
    var task = new SyncTaskDefinition
    {
        Name = "过滤预演",
        LeftPath = left,
        RightPath = right,
        DeletionMode = DeletionMode.Propagate,
        Filters = new SyncFilterRules
        {
            IncludePatterns = new[] { "Saves/*" },
            ExcludePatterns = new[] { "*.tmp" },
        },
    };
    var legacyBaseline = new SyncBaseline(new Dictionary<string, SyncBaselineEntry>(StringComparer.OrdinalIgnoreCase)
    {
        [Path.Combine("Saves", "legacy.tmp")] = new(null, new FileStamp(1, 1, "A")),
    });
    var engine = new SyncEngine(Path.Combine(root, "vault"));

    var preview = await engine.PreviewAsync(task, legacyBaseline);
    Equal(1, preview.CopiedFiles, "预演没有只包含符合规则的存档文件。");
    Equal(0, preview.DeletedFiles, "旧基线中的排除文件被错误规划为删除。");
    False(File.Exists(Path.Combine(right, "Saves", "slot.sav")), "预演修改了目标目录。");

    await engine.RunAsync(task, legacyBaseline, true);
    True(File.Exists(Path.Combine(right, "Saves", "slot.sav")), "符合规则的文件未同步。");
    False(File.Exists(Path.Combine(right, "Saves", "trace.tmp")), "排除文件被错误同步。");
}

async Task TestSyncRunHistoryStoreAsync()
{
    var path = Path.Combine(testRoot, "sync-history", "runs.json");
    var store = new SyncRunHistoryStore(path);
    for (var index = 0; index < 205; index++)
    {
        await store.AppendAsync(new SyncRunRecord
        {
            TaskId = Guid.NewGuid(),
            TaskName = $"任务 {index}",
            StartedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Succeeded = true,
        });
    }

    var records = await store.LoadAsync();
    Equal(200, records.Count, "同步运行历史没有限制为 200 条。");
    Equal("任务 204", records[0].TaskName, "同步运行历史顺序不正确。");
    False(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any(), "同步运行历史留下了暂存文件。");
}

async Task TestGameCatalogAsync()
{
    var sourceCatalog = JsonSerializer.Deserialize<GameCatalogDocument>(
        await File.ReadAllTextAsync(Path.Combine(repositoryRoot, "config", "game-catalog.source.json")),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidDataException("仓库游戏目录源文件为空。");
    GameCatalogVerifier.Validate(sourceCatalog);
    True(sourceCatalog.Games.Count >= 1, "仓库游戏目录源文件没有规则。");
    Equal(
        GameCatalogClient.DefaultCatalogUri,
        UpdateRoutePlanner.CreateRoutes(GameCatalogClient.DefaultCatalogUri, new UpdateNetworkOptions())[0].RequestUri,
        "raw.githubusercontent.com 游戏目录没有进入共用 GitHub 线路。可用域名列表可能未同步。");

    using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var catalog = new GameCatalogDocument
    {
        Revision = 7,
        Games = new[]
        {
            new GameCatalogEntry
            {
                Id = "test-game",
                Name = "测试游戏",
                Executables = new[] { "TestGame.exe" },
                SavePathTemplates = new[] { "{LocalAppData}\\TestGame\\Saves" },
            },
        },
    };
    var signature = signer.SignData(
        catalog.GetCanonicalPayload(),
        HashAlgorithmName.SHA256,
        DSASignatureFormat.Rfc3279DerSequence);
    catalog = catalog with { Signature = Convert.ToBase64String(signature) };
    var json = JsonSerializer.Serialize(catalog);
    var publicKey = signer.ExportSubjectPublicKeyInfoPem();

    Equal(7L, GameCatalogVerifier.ParseAndVerify(json, publicKey).Revision, "合法游戏目录未通过签名验证。");
    var tampered = JsonSerializer.Serialize(catalog with { Revision = 8 });
    Throws<InvalidDataException>(() => GameCatalogVerifier.ParseAndVerify(tampered, publicKey), "篡改游戏目录未被拒绝。");

    var matches = GameDiscoveryService.Match(catalog, new[]
    {
        new RunningGameProcess(42, Path.Combine(testRoot, "TestGame.exe")),
    });
    Equal(1, matches.Count, "运行中的已知游戏未被识别。");

    var cachePath = Path.Combine(testRoot, "catalog", "game-catalog.json");
    var requests = new List<Uri>();
    using (var onlineClient = new HttpClient(new DelegateHttpMessageHandler(request =>
    {
        requests.Add(request.RequestUri!);
        return request.RequestUri!.Host == "proxy.example"
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
    })))
    {
        var fetched = await new GameCatalogClient(onlineClient, cachePath, publicKey).FetchAsync(new UpdateNetworkOptions
        {
            UrlRoutes = new[]
            {
                new UpdateUrlRoute { BaseUrl = "https://proxy.example", Priority = 10 },
                UpdateUrlRoute.Direct with { Priority = 1 },
            },
        });
        False(fetched.UsedCachedCopy, "在线目录被错误标记为缓存。");
        Equal(2, requests.Count, "游戏目录没有在前缀线路失败后切换直连。");
    }

    using var offlineClient = new HttpClient(new DelegateHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
    var cached = await new GameCatalogClient(offlineClient, cachePath, publicKey).FetchAsync();
    True(cached.UsedCachedCopy, "在线失败后没有回退到已验证缓存。");

    var olderCatalog = catalog with { Revision = 6, Signature = string.Empty };
    var olderSignature = signer.SignData(
        olderCatalog.GetCanonicalPayload(),
        HashAlgorithmName.SHA256,
        DSASignatureFormat.Rfc3279DerSequence);
    var olderJson = JsonSerializer.Serialize(olderCatalog with { Signature = Convert.ToBase64String(olderSignature) });
    using var replayClient = new HttpClient(new DelegateHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(olderJson),
    }));
    var replayResult = await new GameCatalogClient(replayClient, cachePath, publicKey).FetchAsync();
    True(replayResult.UsedCachedCopy, "旧的合法签名目录覆盖了较新的可信缓存。");
    Equal(7L, replayResult.Catalog.Revision, "目录重放后可信缓存修订号发生回退。");
}

async Task TestGameBackupMonitorAsync()
{
    var root = Path.Combine(testRoot, "game-monitor");
    var save = Path.Combine(root, "save");
    var backup = Path.Combine(root, "backup");
    Directory.CreateDirectory(save);
    var watchedProfile = new GameBackupProfile
    {
        Name = "文件监听游戏",
        SaveDirectory = save,
        Triggers = BackupTrigger.ChangedFiles,
    };
    var watchedService = new GameBackupService(watchedProfile, backup);
    await using (var monitor = new GameBackupMonitor(watchedProfile, watchedService))
    {
        var created = new TaskCompletionSource<SnapshotCreationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.BackupCreated += (_, result) => created.TrySetResult(result);
        monitor.Start();
        await File.WriteAllTextAsync(Path.Combine(save, "slot.sav"), "new-save");
        var result = await created.Task.WaitAsync(TimeSpan.FromSeconds(10));
        True(File.Exists(Path.Combine(result.SnapshotDirectory, "manifest.json")), "游戏文件变化没有生成快照清单。");
    }

    var importantProfile = new GameBackupProfile
    {
        Name = "重点文件游戏",
        SaveDirectory = save,
        Triggers = BackupTrigger.ImportantFileChanged,
        ImportantFilePatterns = new[] { @"\.sav$" },
        MinimumBackupInterval = TimeSpan.FromHours(1),
    };
    var importantService = new GameBackupService(importantProfile, backup);
    var first = await importantService.CreateAsync(BackupTrigger.ImportantFileChanged);
    var throttled = await importantService.CreateAsync(BackupTrigger.ImportantFileChanged);
    True(first is not null, "首次重点文件备份被错误跳过。");
    True(throttled is null, "重点文件最低备份间隔未生效。");
}

async Task TestSnapshotRestoreAsync()
{
    var root = Path.Combine(testRoot, "restore");
    var save = Path.Combine(root, "save");
    var backup = Path.Combine(root, "backup");
    Directory.CreateDirectory(save);
    await File.WriteAllTextAsync(Path.Combine(save, "slot-a.sav"), "old-a");
    await File.WriteAllTextAsync(Path.Combine(save, "slot-b.sav"), "old-b");
    var profileId = Guid.NewGuid();
    var snapshot = await new SnapshotStore().CreateAsync(profileId, save, backup, "回档测试");

    await File.WriteAllTextAsync(Path.Combine(save, "slot-a.sav"), "new-a");
    await File.WriteAllTextAsync(Path.Combine(save, "extra.tmp"), "extra");
    var restore = new SnapshotRestoreService(new EmptyOccupancyService());
    var whole = await restore.RestoreAsync(new RestoreRequest
    {
        SnapshotDirectory = snapshot.SnapshotDirectory,
        TargetDirectory = save,
        Mode = RestoreMode.CleanDirectory,
    });
    Equal(2, whole.RestoredFiles, "整目录回档数量不正确。");
    Equal("old-a", await File.ReadAllTextAsync(Path.Combine(save, "slot-a.sav")), "整目录回档内容不正确。");
    False(File.Exists(Path.Combine(save, "extra.tmp")), "整目录回档未清理额外文件。");

    await File.WriteAllTextAsync(Path.Combine(save, "slot-a.sav"), "changed-a");
    await File.WriteAllTextAsync(Path.Combine(save, "slot-b.sav"), "changed-b");
    var filtered = await restore.RestoreAsync(new RestoreRequest
    {
        SnapshotDirectory = snapshot.SnapshotDirectory,
        TargetDirectory = save,
        Mode = RestoreMode.FilteredFiles,
        IncludePatterns = new[] { @"slot-a\.sav$" },
    });
    Equal(1, filtered.RestoredFiles, "正则回档数量不正确。");
    Equal("old-a", await File.ReadAllTextAsync(Path.Combine(save, "slot-a.sav")), "正则选中的文件未恢复。");
    Equal("changed-b", await File.ReadAllTextAsync(Path.Combine(save, "slot-b.sav")), "正则未选中的文件被错误修改。");

    var occupiedRestore = new SnapshotRestoreService(new AlwaysOccupiedService());
    await ExpectThrowsAsync<FilesOccupiedException>(() => occupiedRestore.RestoreAsync(new RestoreRequest
    {
        SnapshotDirectory = snapshot.SnapshotDirectory,
        TargetDirectory = save,
        Mode = RestoreMode.ChangedFiles,
    }), "文件占用时未在修改前取消回档。");
}

async Task TestRestartManagerAsync()
{
    var path = Path.Combine(testRoot, "restart-manager", "locked.sav");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await File.WriteAllTextAsync(path, "locked");
    await using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, true);
    var occupied = await new RestartManagerOccupancyService().FindAsync(new[] { path });
    var current = occupied.SelectMany(file => file.Processes)
        .SingleOrDefault(process => process.ProcessId == Environment.ProcessId);

    if (current is null)
    {
        throw new InvalidOperationException("Restart Manager 未返回当前独占文件的进程。");
    }

    False(current.CanTerminate, "当前 PathEcho 进程被错误标记为可结束。");
}

async Task TestBackupDirectoryManagerAsync()
{
    var root = Path.Combine(testRoot, "backup-directory");
    var save = Path.Combine(root, "save");
    var oldBackup = Path.Combine(root, "old");
    var newBackup = Path.Combine(root, "new");
    var importedBackup = Path.Combine(root, "imported");
    Directory.CreateDirectory(save);
    await File.WriteAllTextAsync(Path.Combine(save, "slot.sav"), "movable");
    var profileId = Guid.NewGuid();
    await new SnapshotStore().CreateAsync(profileId, save, oldBackup, "迁移测试");
    var manager = new BackupDirectoryManager();

    var emptyTarget = Path.Combine(root, "empty-target");
    await manager.EnsureWritableAsync(emptyTarget);
    True(Directory.Exists(emptyTarget), "无现有备份时没有创建并验证新目录。");
    False(Directory.EnumerateFiles(emptyTarget, ".pathecho-write-test-*.tmp").Any(), "目录可写性探针未清理。");
    False(await manager.MoveProfileAsync(Guid.NewGuid(), oldBackup, emptyTarget), "不存在的游戏备份被错误报告为已迁移。");

    var moved = await manager.MoveProfileAsync(profileId, oldBackup, newBackup);
    True(moved, "备份目录迁移未执行。");
    False(Directory.Exists(Path.Combine(oldBackup, profileId.ToString("N"))), "迁移成功后旧目录仍保留游戏备份。");
    True(Directory.Exists(Path.Combine(newBackup, profileId.ToString("N"))), "迁移后新目录缺少游戏备份。");

    var discovered = await manager.DiscoverAsync(newBackup);
    Equal(1, discovered.Count, "未发现已迁移的备份。");
    var imported = await manager.ImportAsync(discovered[0], importedBackup);
    True(Directory.Exists(imported.ProfileDirectory), "发现的备份未导入目标目录。");
    Equal(1, (await manager.DiscoverAsync(importedBackup)).Count, "导入后的备份无法再次发现。");
}

async Task TestUpdaterPackageValidationAsync()
{
    var root = Path.Combine(testRoot, "updater");
    Directory.CreateDirectory(root);
    var valid = Path.Combine(root, "valid.zip");
    var malicious = Path.Combine(root, "malicious.zip");
    CreateUpdatePackage(valid, false);
    CreateUpdatePackage(malicious, true);

    Equal(0, await RunUpdaterValidationAsync(valid), "合法更新包未通过预检。");
    True(await RunUpdaterValidationAsync(malicious) != 0, "路径穿越更新包被错误接受。");
    False(File.Exists(Path.Combine(root, "escape.txt")), "恶意更新包在目标外写入了文件。");
}

Task TestUpdateRoutePlanningAsync()
{
    var options = new UpdateNetworkOptions
    {
        UrlRoutes = new[]
        {
            new UpdateUrlRoute { BaseUrl = "https://proxy.example/github/", Priority = 9 },
            new UpdateUrlRoute { BaseUrl = "https://PROXY.example/github", Priority = 7 },
        },
        HttpProxy = "http://127.0.0.1:7890",
    };

    var normalized = UpdateRoutePlanner.Normalize(options);
    Equal(2, normalized.UrlRoutes.Count, "重复前缀线路未去重或缺少直连兜底。");
    Equal("http://127.0.0.1:7890", normalized.HttpProxy!, "HTTP 代理规范化结果不正确。");

    var original = new Uri("https://github.com/Kratosmax/PathEcho/releases/latest/download/update-lite.json");
    var routes = UpdateRoutePlanner.CreateRoutes(original, normalized);
    Equal(2, routes.Count, "启用线路数量不正确。");
    Equal("https://proxy.example/github/https://github.com/Kratosmax/PathEcho/releases/latest/download/update-lite.json", routes[0].RequestUri.AbsoluteUri, "前缀线路拼接不正确。");
    Equal(original, routes[1].RequestUri, "直连线路未作为兜底保留。");

    Throws<InvalidDataException>(() => UpdateRoutePlanner.Normalize(new UpdateNetworkOptions
    {
        UrlRoutes = new[] { new UpdateUrlRoute { BaseUrl = "https://user:secret@proxy.example/" } },
    }), "带凭据的 URL 前缀未被拒绝。");
    Throws<InvalidDataException>(() => UpdateRoutePlanner.Normalize(new UpdateNetworkOptions
    {
        HttpProxy = "socks5://127.0.0.1:1080",
    }), "不支持的 HTTP 代理 scheme 未被拒绝。");
    Throws<InvalidDataException>(() => UpdateRoutePlanner.CreateRoutes(new Uri("https://example.com/update.zip"), normalized), "非 allowlist 原始 URL 未被拒绝。");
    Throws<InvalidOperationException>(() => UpdateRoutePlanner.CreateRoutes(original, new UpdateNetworkOptions
    {
        UrlRoutes = new[] { UpdateUrlRoute.Direct with { Priority = 0 } },
    }), "全部线路禁用时未返回明确错误。");
    return Task.CompletedTask;
}

Task TestUpdateManifestSignatureAsync()
{
    using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var manifest = new UpdateManifest
    {
        Version = "0.2.0",
        Channel = "Lite",
        DownloadUrl = "https://github.com/Kratosmax/PathEcho/releases/download/v0.2.0/PathEcho-0.2.0-Lite.zip",
        Sha256 = new string('A', 64),
        PackageSize = 1024,
        ReleaseNotes = "可信更新公告",
    };
    var signature = signer.SignData(
        manifest.GetCanonicalPayload(),
        HashAlgorithmName.SHA256,
        DSASignatureFormat.Rfc3279DerSequence);
    manifest = manifest with { Signature = Convert.ToBase64String(signature) };
    var json = JsonSerializer.Serialize(manifest);
    var publicKey = signer.ExportSubjectPublicKeyInfoPem();

    Equal("0.2.0", UpdateManifestVerifier.ParseAndVerify(json, "Lite", publicKey).Version, "合法签名清单未通过验证。");
    var tampered = JsonSerializer.Serialize(manifest with { ReleaseNotes = "被篡改" });
    Throws<InvalidDataException>(
        () => UpdateManifestVerifier.ParseAndVerify(tampered, "Lite", publicKey),
        "篡改后的更新清单未被拒绝。");
    return Task.CompletedTask;
}

async Task TestUpdatePackageDownloadAsync()
{
    var root = Path.Combine(testRoot, "update-download");
    Directory.CreateDirectory(root);
    var content = "trusted-update-package"u8.ToArray();
    var hash = Convert.ToHexString(SHA256.HashData(content));
    var requested = new List<Uri>();
    using var client = new HttpClient(new DelegateHttpMessageHandler(request =>
    {
        requested.Add(request.RequestUri!);
        return request.RequestUri!.Host == "proxy.example"
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            };
    }));
    var downloader = new UpdatePackageDownloader(client);
    var destination = Path.Combine(root, "package.zip");
    var options = new UpdateNetworkOptions
    {
        UrlRoutes = new[]
        {
            new UpdateUrlRoute { BaseUrl = "https://proxy.example", Priority = 10 },
            UpdateUrlRoute.Direct with { Priority = 1 },
        },
    };
    var original = new Uri("https://github.com/Kratosmax/PathEcho/releases/download/v0.2.0/PathEcho-0.2.0-Lite.zip");

    await downloader.DownloadAsync(original, destination, hash, 1024, options);
    Equal(2, requested.Count, "首条线路失败后未切换直连。");
    Equal("trusted-update-package", await File.ReadAllTextAsync(destination), "下载完成文件内容不正确。");

    var rejected = Path.Combine(root, "rejected.zip");
    await ExpectThrowsAsync<InvalidOperationException>(
        () => downloader.DownloadAsync(original, rejected, new string('0', 64), 1024, new UpdateNetworkOptions()),
        "错误哈希的更新包未被拒绝。");
    False(File.Exists(rejected), "哈希失败后留下了正式更新包。");
    False(Directory.EnumerateFiles(root, "*.download").Any(), "哈希失败后留下了下载暂存文件。");

    using var redirectedClient = new HttpClient(new DelegateHttpMessageHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
    {
        RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://example.com/forged.zip"),
        Content = new ByteArrayContent(content),
    }));
    var redirected = Path.Combine(root, "redirected.zip");
    await ExpectThrowsAsync<InvalidOperationException>(
        () => new UpdatePackageDownloader(redirectedClient).DownloadAsync(original, redirected, hash, 1024),
        "非 allowlist 重定向未被拒绝。");
    False(File.Exists(redirected), "非法重定向留下了正式更新包。");
}

static void CreateUpdatePackage(string path, bool malicious)
{
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var item in new Dictionary<string, string>
    {
        ["PathEcho.exe"] = "placeholder",
        ["PathEcho.Updater.exe"] = "placeholder",
        ["channel.txt"] = "Lite",
        ["version.txt"] = "0.1.0",
        [".pathecho-install-root"] = "PathEcho",
    })
    {
        var entry = archive.CreateEntry(item.Key);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(item.Value);
    }

    if (malicious)
    {
        var entry = archive.CreateEntry("../escape.txt");
        using var writer = new StreamWriter(entry.Open());
        writer.Write("escape");
    }
}

async Task<int> RunUpdaterValidationAsync(string package)
    => await RunUpdaterProcessAsync(package, null, null);

async Task<int> RunUpdaterProcessAsync(string package, string? expectedHash, string? resultPath)
{
    var updater = Path.Combine(
        repositoryRoot,
        "temp",
        "build",
        "PathEcho.Updater",
        "Release",
        "net8.0",
        "PathEcho.Updater.exe");
    await using var packageStream = File.OpenRead(package);
    var hash = expectedHash ?? Convert.ToHexString(await SHA256.HashDataAsync(packageStream));
    var start = new ProcessStartInfo(updater)
    {
        UseShellExecute = false,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };
    foreach (var argument in new[]
    {
        "--package", package,
        "--sha256", hash,
        "--channel", "Lite",
        "--version", "0.1.0",
        "--verify-only", "true",
    })
    {
        start.ArgumentList.Add(argument);
    }

    if (resultPath is not null)
    {
        start.ArgumentList.Add("--result");
        start.ArgumentList.Add(resultPath);
    }

    using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动更新器预检。");
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    return process.ExitCode;
}

async Task TestUpdateHandoffContractAsync()
{
    var service = File.ReadAllText(Path.Combine(repositoryRoot, "src", "PathEcho", "Services", "ApplicationUpdateService.cs"));
    True(service.Contains("StartsWith(\"PathEcho.Core\"", StringComparison.Ordinal), "更新 launcher 没有复制更新器依赖。");
    True(service.Contains("UpdateFileOperation.RetryAsync(", StringComparison.Ordinal), "更新 launcher 没有处理短暂文件占用。");
    True(service.Contains("WorkingDirectory = launcher", StringComparison.Ordinal), "外部更新器仍可能继承安装目录作为当前工作目录。");
    True(service.Contains("TryDeleteTree(launcher)", StringComparison.Ordinal), "更新 launcher 复制失败会留下半成品目录。");
    True(service.Contains("CleanupStaleUpdateCache();", StringComparison.Ordinal), "启动检查不会清理过期更新缓存。");
    False(service.Contains("Task.Delay(TimeSpan.FromMilliseconds(750)", StringComparison.Ordinal), "主程序仍使用固定延时伪装更新器握手。");
    True(service.Contains("WaitForUpdaterHandoffAsync", StringComparison.Ordinal), "主程序退出前没有等待更新器落盘握手。");
    True(service.Contains("cancellationToken.ThrowIfCancellationRequested();", StringComparison.Ordinal), "启动外部更新器前没有最后检查取消请求。");
    True(service.Contains("handoffStarting?.Invoke();", StringComparison.Ordinal), "更新窗口无法进入不可取消的交接状态。");
    True(service.Contains("updaterProcess.HasExited", StringComparison.Ordinal), "主程序没有识别更新器立即退出。");
    True(service.Contains("\"--result\", resultPath", StringComparison.Ordinal), "主程序没有向更新器传递结果文件。");
    True(service.Contains("\"--handoff-ready\", handoffReadyPath", StringComparison.Ordinal), "主程序没有向更新器传递握手文件。");

    var updater = File.ReadAllText(Path.Combine(repositoryRoot, "src", "PathEcho.Updater", "Program.cs"));
    True(updater.Contains("TryWriteResult(resultPath, \"failed\"", StringComparison.Ordinal), "更新器失败时没有持久化结果。");
    True(updater.Contains("TryWriteFailureLog(exception)", StringComparison.Ordinal), "更新失败在结果消费后没有保留诊断日志。");
    True(updater.Contains("UpdateFileOperation.RetryAsync(\"备份当前安装目录\"", StringComparison.Ordinal), "安装目录替换没有处理短暂文件占用。");
    True(updater.Contains("Directory.SetCurrentDirectory(AppContext.BaseDirectory)", StringComparison.Ordinal), "更新器没有主动离开待移动的安装目录。");
    True(updater.Contains("UpdateTransactionGate.UpdaterMutexName", StringComparison.Ordinal), "并发更新器之间没有互斥。");
    var handoffSignal = updater.IndexOf("WriteStateSignal(handoffReadyPath", StringComparison.Ordinal);
    var parentWait = updater.IndexOf("await WaitForVerifiedProcessExitAsync", StringComparison.Ordinal);
    var updaterHash = updater.LastIndexOf("await VerifyHashAsync(package", StringComparison.Ordinal);
    True(handoffSignal >= 0 && parentWait > handoffSignal && updaterHash > parentWait, "更新器没有先握手、再等待旧进程、最后二次验包。");
    True(
        updater.Contains("installValidated &&", StringComparison.Ordinal) &&
        updater.Contains("parentExited &&", StringComparison.Ordinal),
        "更新器可能在父进程仍运行时错误重启第二实例。");
    True(updater.Contains("TryRestartAfterFailure", StringComparison.Ordinal), "父进程退出后的更新失败没有恢复可见界面。");
    True(updater.Contains("exception is not UpdateRollbackException", StringComparison.Ordinal), "回滚不完整时仍可能自动启动不可信目标目录。");
    True(updater.Contains("throw new UpdateRollbackException(updateFailure, rollbackFailure)", StringComparison.Ordinal), "更新器没有区分普通更新失败与回滚不完整。");
    True(updater.Contains("await WaitForReadyAsync(updatedProcess, readyPath)", StringComparison.Ordinal), "更新器没有等待新版报告就绪。");
    var readyWait = updater.IndexOf("await WaitForReadyAsync(updatedProcess, readyPath)", StringComparison.Ordinal);
    var backupCleanup = updater.IndexOf("TryDeleteTree(backup)", readyWait, StringComparison.Ordinal);
    True(readyWait >= 0 && backupCleanup > readyWait, "更新器在新版报告就绪前删除了旧版本备份。");

    var app = File.ReadAllText(Path.Combine(repositoryRoot, "src", "PathEcho", "App.xaml.cs"));
    True(app.Contains("SignalUpdateReady(e.Args)", StringComparison.Ordinal), "新版完成初始化后没有报告就绪。");
    True(app.Contains("HasTrustedUpdateHandoff(e.Args)", StringComparison.Ordinal), "主程序没有区分普通启动与可信更新交接。");
    True(app.Contains("UpdateTransactionGate.UpdaterMutexName", StringComparison.Ordinal), "普通启动没有探测正在运行的更新器。");
    False(app.Contains("private UpdateTransactionGate?", StringComparison.Ordinal), "主程序生命周期错误持有更新器独占锁，会破坏正常重复启动。");
    True(app.Contains("AppLogger.Critical(\"Unable to read update result.\"", StringComparison.Ordinal), "更新结果损坏时可能在 Debug 关闭状态下静默失败。");
    True(app.Contains("PathEcho 更新结果不可用", StringComparison.Ordinal), "更新结果损坏时没有可见反馈。");
    True(app.Contains("Unable to delete consumed update result.", StringComparison.Ordinal), "结果清理失败与结果解析失败没有分离处理。");

    var updateWindow = File.ReadAllText(Path.Combine(repositoryRoot, "src", "PathEcho", "Dialogs", "UpdateWindow.xaml.cs"));
    True(updateWindow.Contains("_handoffStarted = true", StringComparison.Ordinal), "外部更新器启动后更新窗口没有锁定取消操作。");
    True(updateWindow.Contains("e.Cancel = true", StringComparison.Ordinal), "更新交接期间仍可关闭窗口并中断生命周期。");

    var package = Path.Combine(testRoot, "handoff-package.zip");
    var resultPath = Path.Combine(testRoot, "update-result.json");
    CreateUpdatePackage(package, false);
    var exitCode = await RunUpdaterProcessAsync(package, new string('0', 64), resultPath);
    True(exitCode != 0, "错误哈希没有让真实更新器失败。");
    True(File.Exists(resultPath), "真实更新器失败后没有写入结果文件。");
    using var result = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath));
    Equal("failed", result.RootElement.GetProperty("Status").GetString() ?? string.Empty, "更新失败结果状态无效。");
}

Task TestReleasePackageVerificationContractAsync()
{
    var releaseScript = File.ReadAllText(Path.Combine(repositoryRoot, "build", "Build-Release.ps1"));
    True(releaseScript.Contains("$packagedUpdater --package $zip", StringComparison.Ordinal), "正式构建没有调用包内更新器验证候选 ZIP。");
    True(releaseScript.Contains("--channel $channel --version $version --verify-only true", StringComparison.Ordinal), "候选 ZIP 验证没有核对通道和版本。");
    True(releaseScript.Contains("package updater verification failed", StringComparison.Ordinal), "候选 ZIP 验证失败不会阻断 Release。");
    return Task.CompletedTask;
}

Task TestGameHistoryAndEditingContractAsync()
{
    var windowXaml = File.ReadAllText(Path.Combine(repositoryRoot, "src", "PathEcho", "MainWindow.xaml"));
    var windowCode = File.ReadAllText(Path.Combine(repositoryRoot, "src", "PathEcho", "MainWindow.xaml.cs"));
    var editorCode = File.ReadAllText(Path.Combine(repositoryRoot, "src", "PathEcho", "Dialogs", "GameProfileEditorWindow.xaml.cs"));
    var runtimeCode = File.ReadAllText(Path.Combine(repositoryRoot, "src", "PathEcho", "Services", "PathEchoRuntime.cs"));

    True(windowXaml.Contains("x:Name=\"HistorySearchBox\"", StringComparison.Ordinal), "存档历史缺少搜索框。");
    True(windowXaml.Contains("x:Name=\"HistoryGameFilter\"", StringComparison.Ordinal), "存档历史缺少游戏筛选。");
    True(windowXaml.Contains("MouseDoubleClick=\"OnGameRowDoubleClick\"", StringComparison.Ordinal), "游戏表格没有双击编辑入口。");
    True(windowCode.Contains("row.Profile.Id != selectedProfileId", StringComparison.Ordinal), "历史筛选没有按游戏 ID 区分同名游戏。");
    True(windowCode.Contains("QueueHistoryRefresh", StringComparison.Ordinal), "大量历史记录刷新没有合并 UI 更新。");
    True(editorCode.Contains("updated with { Id = _existingProfile.Id", StringComparison.Ordinal), "编辑游戏时没有保留原配置 ID。");
    True(runtimeCode.Contains("manager.MoveProfileAsync", StringComparison.Ordinal), "编辑单独备份目录时没有迁移现有备份。");
    return Task.CompletedTask;
}

static void True(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void False(bool condition, string message) => True(!condition, message);

static void Equal<T>(T expected, T actual, string message)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} 期望：{expected}；实际：{actual}");
    }
}

static async Task ExpectThrowsAsync<TException>(Func<Task> action, string message)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

static void Throws<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

static void DeleteTree(string path)
{
    if (!Directory.Exists(path))
    {
        return;
    }

    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
    {
        File.SetAttributes(file, FileAttributes.Normal);
    }

    Directory.Delete(path, true);
}

static string FindRepositoryRoot()
{
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null)
    {
        if (File.Exists(Path.Combine(current.FullName, "PathEcho.sln")))
        {
            return current.FullName;
        }

        current = current.Parent;
    }

    throw new DirectoryNotFoundException("无法定位 PathEcho 仓库根目录。");
}

sealed class EmptyOccupancyService : IFileOccupancyService
{
    public Task<IReadOnlyList<OccupiedFile>> FindAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<OccupiedFile>>(Array.Empty<OccupiedFile>());

    public Task TerminateAsync(IReadOnlyList<LockingProcess> processes, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

sealed class AlwaysOccupiedService : IFileOccupancyService
{
    public Task<IReadOnlyList<OccupiedFile>> FindAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<OccupiedFile>>(new[]
        {
            new OccupiedFile(paths[0], new[] { new LockingProcess(123, DateTimeOffset.UtcNow, "test", true) }),
        });

    public Task TerminateAsync(IReadOnlyList<LockingProcess> processes, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(send(request));
}

sealed class FaultInjectingSnapshotStore(int createFailures = 0, int pruneFailures = 0) : IBackupSnapshotStore
{
    private readonly SnapshotStore _inner = new();

    public int CreateAttempts { get; private set; }

    public int PruneAttempts { get; private set; }

    public Task<SnapshotCreationResult> CreateAsync(
        Guid profileId,
        string sourceDirectory,
        string backupDirectory,
        string trigger,
        CancellationToken cancellationToken = default)
    {
        CreateAttempts++;
        if (CreateAttempts <= createFailures)
        {
            throw new IOException($"模拟写入失败 {CreateAttempts}");
        }

        return _inner.CreateAsync(profileId, sourceDirectory, backupDirectory, trigger, cancellationToken);
    }

    public Task<int> PruneAsync(
        Guid profileId,
        string backupDirectory,
        int retainedVersions,
        CancellationToken cancellationToken = default)
    {
        PruneAttempts++;
        if (PruneAttempts <= pruneFailures)
        {
            throw new IOException($"模拟清理失败 {PruneAttempts}");
        }

        return _inner.PruneAsync(profileId, backupDirectory, retainedVersions, cancellationToken);
    }
}

sealed class FaultInjectingStagingStore(int createFailures) : IBackupStagingStore
{
    private readonly BackupStagingStore _inner = new();

    public int CreateAttempts { get; private set; }

    public Task<string> CreateAsync(string sourceDirectory, string transactionDirectory, CancellationToken cancellationToken)
    {
        CreateAttempts++;
        if (CreateAttempts <= createFailures)
        {
            throw new IOException($"模拟读取失败 {CreateAttempts}");
        }

        return _inner.CreateAsync(sourceDirectory, transactionDirectory, cancellationToken);
    }

    public void DeleteIfPresent(string transactionDirectory) => _inner.DeleteIfPresent(transactionDirectory);
}
