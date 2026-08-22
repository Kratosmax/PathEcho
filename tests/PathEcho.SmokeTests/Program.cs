using PathEcho.Core.Backup;
using PathEcho.Core.Models;
using PathEcho.Core.Restore;
using PathEcho.Core.Storage;
using PathEcho.Core.Sync;
using PathEcho.Core.Update;
using PathEcho.Platform.Windows.Restore;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

var testRoot = Path.Combine(Environment.CurrentDirectory, "temp", "smoke", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);

var tests = new (string Name, Func<Task> Run)[]
{
    ("配置可原子保存并读取", TestConfigurationStoreAsync),
    ("单向同步可复制并在删除前备份", TestOneWaySyncAsync),
    ("双向冲突默认保留两份", TestBidirectionalConflictAsync),
    ("游戏快照可去重、浏览并清理旧版本", TestSnapshotStoreAsync),
    ("目录变化可合并触发自动同步", TestSyncMonitorAsync),
    ("同一任务并发同步会串行更新基线", TestSyncTaskRunnerSerializationAsync),
    ("只读表格不会进入编辑模式", TestReadOnlyDataGridContractAsync),
    ("Lite 安装器正确检测 x64 Desktop Runtime", TestLiteInstallerRuntimeDetectionContractAsync),
    ("游戏文件变化可触发备份并限制重点备份频率", TestGameBackupMonitorAsync),
    ("整目录与正则文件回档可事务恢复", TestSnapshotRestoreAsync),
    ("Restart Manager 可识别占用且保护当前进程", TestRestartManagerAsync),
    ("备份目录可迁移、发现并导入", TestBackupDirectoryManagerAsync),
    ("更新线路可规范化并稳定排序", TestUpdateRoutePlanningAsync),
    ("签名更新清单可验证并拒绝篡改", TestUpdateManifestSignatureAsync),
    ("更新下载可故障转移并清理失败暂存", TestUpdatePackageDownloadAsync),
    ("更新器拒绝路径穿越包", TestUpdaterPackageValidationAsync),
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
    var installerPath = Path.Combine(Environment.CurrentDirectory, "build", "PathEcho.iss");
    if (!File.Exists(installerPath))
    {
        installerPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "build", "PathEcho.iss"));
    }

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
        EnableDebugLogging = true,
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

    await store.SaveAsync(expected);
    var actual = await store.LoadAsync();

    Equal(1, actual.SyncTasks.Count, "配置任务数量不正确。");
    Equal(task.Id, actual.SyncTasks[0].Id, "配置任务 ID 未保持一致。");
    True(actual.EnableDebugLogging, "Debug 日志开关未保持一致。");
    Equal(2, actual.UpdateNetwork.UrlRoutes.Count, "更新线路配置未保持一致。");
    Equal("http://127.0.0.1:7890", actual.UpdateNetwork.HttpProxy!, "HTTP 代理配置未保持一致。");

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
    var app = XDocument.Load(Path.Combine(Environment.CurrentDirectory, "src", "PathEcho", "App.xaml"));
    var dataGridStyle = app.Descendants(presentation + "Style")
        .Single(element => (string?)element.Attribute("TargetType") == "DataGrid");
    var readOnlySetter = dataGridStyle.Elements(presentation + "Setter")
        .SingleOrDefault(element => (string?)element.Attribute("Property") == "IsReadOnly");
    Equal("True", (string?)readOnlySetter?.Attribute("Value") ?? string.Empty, "全局 DataGrid 没有保持只读。");

    var mainWindow = XDocument.Load(Path.Combine(Environment.CurrentDirectory, "src", "PathEcho", "MainWindow.xaml"));
    var routesGrid = mainWindow.Descendants(presentation + "DataGrid")
        .Single(element => (string?)element.Attribute(x + "Name") == "UpdateRoutesGrid");
    Equal("False", (string?)routesGrid.Attribute("IsReadOnly") ?? string.Empty, "更新线路表没有保留编辑能力。");
    return Task.CompletedTask;
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

static async Task<int> RunUpdaterValidationAsync(string package)
{
    var updater = Path.Combine(
        Environment.CurrentDirectory,
        "temp",
        "build",
        "PathEcho.Updater",
        "Release",
        "net8.0",
        "PathEcho.Updater.exe");
    await using var packageStream = File.OpenRead(package);
    var hash = Convert.ToHexString(await SHA256.HashDataAsync(packageStream));
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

    using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动更新器预检。");
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    return process.ExitCode;
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
