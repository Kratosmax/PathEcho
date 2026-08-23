using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using PathEcho.Core.Update;

string? resultPath = null;
string? target = null;
var installValidated = false;
var parentExited = false;
try
{
    var options = ParseArguments(args);
    resultPath = options.TryGetValue("result", out var configuredResultPath)
        ? Path.GetFullPath(configuredResultPath)
        : null;
    var package = RequirePath(options, "package");
    var verifyOnly = options.TryGetValue("verify-only", out var verifyOnlyValue) && bool.Parse(verifyOnlyValue);
    if (!verifyOnly)
    {
        if (resultPath is null)
        {
            throw new ArgumentException("缺少更新结果文件参数：--result");
        }

        target = RequirePath(options, "target");
        EnsureLauncherIsExternal(target);
        ValidateInstallRoot(target);
        installValidated = true;
    }

    UpdateManifest? manifest = null;
    string expectedHash;
    string expectedChannel;
    string expectedVersion;
    if (verifyOnly)
    {
        expectedHash = RequireValue(options, "sha256").ToUpperInvariant();
        expectedChannel = RequireValue(options, "channel");
        expectedVersion = RequireValue(options, "version");
    }
    else
    {
        var manifestPath = RequirePath(options, "manifest");
        expectedChannel = RequireValue(options, "channel");
        manifest = await UpdateManifestVerifier.ReadAndVerifyAsync(manifestPath, expectedChannel);
        expectedHash = manifest.Sha256.ToUpperInvariant();
        expectedVersion = manifest.Version;
    }

    await VerifyHashAsync(package, expectedHash);
    UpdatePackageValidator.Validate(package, expectedChannel, expectedVersion);
    if (verifyOnly)
    {
        return 0;
    }

    var processId = int.Parse(RequireValue(options, "pid"));
    var processStartedAt = long.Parse(RequireValue(options, "process-start-filetime"));
    var previewRestart = options.TryGetValue("preview-restart", out var previewRestartValue) && bool.Parse(previewRestartValue);

    await WaitForVerifiedProcessExitAsync(processId, processStartedAt);
    parentExited = true;
    await ApplyPackageAsync(package, target!, expectedVersion, previewRestart, resultPath!);
    return 0;
}
catch (Exception exception)
{
    TryWriteResult(resultPath, "failed", exception.Message);
    if (installValidated &&
        parentExited &&
        target is not null &&
        exception is not UpdateRollbackException)
    {
        TryRestartAfterFailure(target, resultPath);
    }

    Console.Error.WriteLine($"PathEcho 更新失败，现有安装应保持不变或已经回滚。{exception.Message}");
    return 1;
}

static Dictionary<string, string> ParseArguments(string[] arguments)
{
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < arguments.Length; index += 2)
    {
        if (index + 1 >= arguments.Length || !arguments[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException("更新器参数格式无效。");
        }

        values.Add(arguments[index][2..], arguments[index + 1]);
    }

    return values;
}

static string RequirePath(IReadOnlyDictionary<string, string> options, string name) =>
    Path.GetFullPath(RequireValue(options, name));

static string RequireValue(IReadOnlyDictionary<string, string> options, string name) =>
    options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"缺少更新器参数：--{name}");

static void EnsureLauncherIsExternal(string target)
{
    var launcher = Path.GetFullPath(AppContext.BaseDirectory);
    var targetPrefix = Path.TrimEndingDirectorySeparator(target) + Path.DirectorySeparatorChar;
    if (launcher.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("更新器必须从安装目录之外的 launcher 目录运行。");
    }
}

static void ValidateInstallRoot(string target)
{
    var marker = Path.Combine(target, ".pathecho-install-root");
    if (!Directory.Exists(target) || !File.Exists(marker) ||
        !string.Equals(File.ReadAllText(marker).Trim(), "PathEcho", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("目标目录不是可更新的 PathEcho 安装目录。");
    }
}

static async Task VerifyHashAsync(string package, string expectedHash)
{
    await using var stream = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
    var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream));
    if (!string.Equals(actual, expectedHash, StringComparison.Ordinal))
    {
        throw new InvalidDataException("更新包 SHA-256 校验失败。");
    }
}

static async Task WaitForVerifiedProcessExitAsync(int processId, long expectedStartFileTime)
{
    Process process;
    try
    {
        process = Process.GetProcessById(processId);
    }
    catch (ArgumentException)
    {
        return;
    }

    using (process)
    {
        if (process.StartTime.ToUniversalTime().ToFileTimeUtc() != expectedStartFileTime)
        {
            throw new InvalidOperationException("主进程 PID 已被复用，拒绝更新。");
        }

        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }
}

static async Task ApplyPackageAsync(string package, string target, string version, bool previewRestart, string resultPath)
{
    var parent = Path.GetDirectoryName(target) ?? throw new InvalidOperationException("安装目录缺少父目录。");
    var token = Guid.NewGuid().ToString("N");
    var stage = Path.Combine(parent, $".pathecho-stage-{token}");
    var backup = Path.Combine(parent, $".pathecho-backup-{token}");
    var failed = Path.Combine(parent, $".pathecho-failed-{token}");
    var targetMoved = false;
    var newTargetPlaced = false;
    try
    {
        ExtractSafely(package, stage);
        Directory.Move(target, backup);
        targetMoved = true;
        Directory.Move(stage, target);
        newTargetPlaced = true;
        PreserveInstallerFiles(backup, target);

        var executable = Path.Combine(target, "PathEcho.exe");
        var readyPath = resultPath + ".ready";
        TryDeleteFile(readyPath);
        var start = new ProcessStartInfo(executable) { UseShellExecute = true };
        start.ArgumentList.Add("--updated-from");
        start.ArgumentList.Add(version);
        TryWriteResult(resultPath, "succeeded", $"PathEcho 已更新到 {version}。");
        start.ArgumentList.Add("--update-result");
        start.ArgumentList.Add(resultPath);
        start.ArgumentList.Add("--update-ready");
        start.ArgumentList.Add(readyPath);
        if (previewRestart)
        {
            start.ArgumentList.Add("--preview");
            start.ArgumentList.Add("--preview-seed");
        }
        using var updatedProcess = Process.Start(start) ?? throw new InvalidOperationException("新版 PathEcho 启动失败。");
        await WaitForReadyAsync(updatedProcess, readyPath);
        TryDeleteFile(readyPath);
        TryDeleteTree(backup);
    }
    catch (Exception updateFailure)
    {
        Exception? rollbackFailure = null;
        if (newTargetPlaced && Directory.Exists(target))
        {
            try
            {
                Directory.Move(target, failed);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                rollbackFailure = exception;
            }
        }

        if (targetMoved && Directory.Exists(backup) && !Directory.Exists(target))
        {
            try
            {
                Directory.Move(backup, target);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                rollbackFailure = rollbackFailure is null
                    ? exception
                    : new AggregateException(rollbackFailure, exception);
            }
        }

        if (rollbackFailure is not null)
        {
            throw new UpdateRollbackException(updateFailure, rollbackFailure);
        }

        throw;
    }
    finally
    {
        TryDeleteTree(stage);
    }
}

static async Task WaitForReadyAsync(Process process, string readyPath)
{
    var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
    while (DateTimeOffset.UtcNow < deadline)
    {
        if (File.Exists(readyPath))
        {
            return;
        }

        if (process.HasExited)
        {
            throw new InvalidOperationException($"新版 PathEcho 在报告就绪前退出（代码 {process.ExitCode}）。");
        }

        await Task.Delay(100);
    }

    try
    {
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }
    catch (InvalidOperationException)
    {
    }

    throw new TimeoutException("新版 PathEcho 在 15 秒内未报告就绪，已回滚旧版本。");
}

static void TryRestartAfterFailure(string target, string? resultPath)
{
    try
    {
        var executable = Path.Combine(target, "PathEcho.exe");
        if (!File.Exists(executable))
        {
            return;
        }

        var start = new ProcessStartInfo(executable) { UseShellExecute = true };
        if (resultPath is not null)
        {
            start.ArgumentList.Add("--update-result");
            start.ArgumentList.Add(resultPath);
        }

        _ = Process.Start(start);
    }
    catch
    {
    }
}

static void TryWriteResult(string? path, string status, string message)
{
    if (path is null)
    {
        return;
    }

    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new
        {
            Status = status,
            Message = message,
            TimestampUtc = DateTimeOffset.UtcNow,
        }));
        File.Move(temporary, path, true);
    }
    catch
    {
    }
}

static void TryDeleteFile(string path)
{
    try
    {
        File.Delete(path);
    }
    catch (IOException)
    {
    }
    catch (UnauthorizedAccessException)
    {
    }
}

static void PreserveInstallerFiles(string backup, string target)
{
    foreach (var source in Directory.EnumerateFiles(backup, "unins*", SearchOption.TopDirectoryOnly))
    {
        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension is not (".exe" or ".dat" or ".msg"))
        {
            continue;
        }

        File.Copy(source, Path.Combine(target, Path.GetFileName(source)), false);
    }
}

static void ExtractSafely(string package, string stage)
{
    Directory.CreateDirectory(stage);
    var prefix = Path.TrimEndingDirectorySeparator(stage) + Path.DirectorySeparatorChar;
    using var archive = ZipFile.OpenRead(package);
    foreach (var entry in archive.Entries)
    {
        var destination = Path.GetFullPath(Path.Combine(stage, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
        if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"更新包包含越界路径：{entry.FullName}");
        }

        if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
        {
            Directory.CreateDirectory(destination);
            continue;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        entry.ExtractToFile(destination, false);
    }
}

static void TryDeleteTree(string path)
{
    if (!Directory.Exists(path))
    {
        return;
    }

    try
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, true);
    }
    catch (IOException)
    {
    }
    catch (UnauthorizedAccessException)
    {
    }
}

sealed class UpdateRollbackException(Exception updateFailure, Exception rollbackFailure)
    : AggregateException(
        "更新失败，且自动回滚未完整完成。已保留恢复现场并停止自动重启，请手动安装最新版本。",
        updateFailure,
        rollbackFailure);
