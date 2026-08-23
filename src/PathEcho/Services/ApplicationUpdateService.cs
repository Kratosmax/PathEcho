using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using PathEcho.Core.Update;

namespace PathEcho.Services;

public enum UpdateAvailability
{
    Latest,
    Available,
    ManualOnly,
}

public sealed record UpdateCheckResult(
    UpdateAvailability Availability,
    string CurrentVersion,
    UpdateManifest? Manifest,
    string? ManualDownloadUrl = null);

public sealed class ApplicationUpdateService(UpdateNetworkOptions networkOptions) : IDisposable
{
    private static readonly Uri LiteManifestUri = new(
        "https://github.com/Kratosmax/PathEcho/releases/latest/download/update-lite.json");
    private static readonly Uri FullManifestUri = new(
        "https://github.com/Kratosmax/PathEcho/releases/latest/download/update-full.json");

    private readonly HttpClient _httpClient = UpdateRoutePlanner.CreateHttpClient(networkOptions);
    private readonly UpdateNetworkOptions _networkOptions = UpdateRoutePlanner.Normalize(networkOptions);

    public static string CurrentVersion
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version ?? new System.Version(0, 0, 0);
            return $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        CleanupStaleUpdateCache();
        var currentVersion = CurrentVersion;
        var channel = ReadChannel();
        if (channel is null || !File.Exists(Path.Combine(AppContext.BaseDirectory, ".pathecho-install-root")))
        {
            return new UpdateCheckResult(
                UpdateAvailability.ManualOnly,
                currentVersion,
                null,
                "https://github.com/Kratosmax/PathEcho/releases/latest");
        }

        var uri = string.Equals(channel, "Full", StringComparison.Ordinal) ? FullManifestUri : LiteManifestUri;
        var manifest = await new UpdateManifestClient(_httpClient)
            .FetchAsync(uri, channel, _networkOptions, cancellationToken)
            .ConfigureAwait(false);
        var availability = ParseVersion(manifest.Version) > ParseVersion(currentVersion)
            ? UpdateAvailability.Available
            : UpdateAvailability.Latest;
        return new UpdateCheckResult(availability, currentVersion, manifest);
    }

    public async Task DownloadAndLaunchAsync(
        UpdateManifest manifest,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Action? handoffStarting = null)
    {
        var channel = ReadChannel() ?? throw new InvalidOperationException("当前目录不是可就地更新的 PathEcho 安装。");
        if (!string.Equals(channel, manifest.Channel, StringComparison.Ordinal))
        {
            throw new InvalidDataException("更新通道与当前安装不匹配。");
        }

        var updateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PathEcho",
            "Update");
        var package = Path.Combine(updateRoot, $"PathEcho-{manifest.Version}-{channel}.zip");
        Directory.CreateDirectory(updateRoot);
        await new UpdatePackageDownloader(_httpClient).DownloadAsync(
            new Uri(manifest.DownloadUrl),
            package,
            manifest.Sha256,
            manifest.PackageSize,
            _networkOptions,
            progress: progress,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        UpdatePackageValidator.Validate(package, channel, manifest.Version);

        var manifestPath = Path.Combine(updateRoot, $"update-{channel.ToLowerInvariant()}-{manifest.Version}.json");
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken).ConfigureAwait(false);

        var launcher = await PrepareLauncherAsync(updateRoot, cancellationToken).ConfigureAwait(false);
        var resultPath = Path.Combine(updateRoot, $"result-{manifest.Version}-{Guid.NewGuid():N}.json");
        var handoffReadyPath = resultPath + ".handoff-ready";
        TryDeleteFile(handoffReadyPath);
        var process = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(Path.Combine(launcher, "PathEcho.Updater.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "--package", package,
            "--manifest", manifestPath,
            "--channel", channel,
            "--target", Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
            "--pid", Environment.ProcessId.ToString(),
            "--process-start-filetime", process.StartTime.ToUniversalTime().ToFileTimeUtc().ToString(),
            "--result", resultPath,
            "--handoff-ready", handoffReadyPath,
        })
        {
            start.ArgumentList.Add(argument);
        }

        cancellationToken.ThrowIfCancellationRequested();
        handoffStarting?.Invoke();
        using var updaterProcess = Process.Start(start) ?? throw new InvalidOperationException("无法启动外部更新器。");
        await WaitForUpdaterHandoffAsync(updaterProcess, handoffReadyPath, resultPath).ConfigureAwait(false);
        TryDeleteFile(handoffReadyPath);
    }

    public void Dispose() => _httpClient.Dispose();

    private static string TryReadUpdateFailure(string resultPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(resultPath));
            return document.RootElement.GetProperty("Message").GetString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task WaitForUpdaterHandoffAsync(Process updaterProcess, string handoffReadyPath, string resultPath)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(handoffReadyPath))
            {
                return;
            }

            if (updaterProcess.HasExited)
            {
                var detail = TryReadUpdateFailure(resultPath);
                throw new InvalidOperationException(
                    $"外部更新器在交接前退出（代码 {updaterProcess.ExitCode}）。{detail}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }

        try
        {
            updaterProcess.Kill(entireProcessTree: true);
            await updaterProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }

        throw new TimeoutException("外部更新器在 10 秒内未完成启动握手，现有安装未修改。");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static async Task<string> PrepareLauncherAsync(string updateRoot, CancellationToken cancellationToken)
    {
        var launcher = Path.Combine(updateRoot, "launcher", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(launcher);
        try
        {
            var updaterFiles = Directory.EnumerateFiles(AppContext.BaseDirectory)
                .Where(file =>
                    Path.GetFileName(file).StartsWith("PathEcho.Updater", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(file).StartsWith("PathEcho.Core", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (!updaterFiles.Any(file => string.Equals(Path.GetFileName(file), "PathEcho.Updater.exe", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("安装目录缺少 PathEcho 更新器。");
            }

            foreach (var file in updaterFiles)
            {
                await UpdateFileOperation.RetryAsync(
                    "准备外部更新器",
                    file,
                    () => File.Copy(file, Path.Combine(launcher, Path.GetFileName(file)), false),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            return launcher;
        }
        catch
        {
            TryDeleteTree(launcher);
            throw;
        }
    }

    private static void TryDeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void CleanupStaleUpdateCache()
    {
        var updateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PathEcho",
            "Update");
        if (!Directory.Exists(updateRoot))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var launcherRoot = Path.Combine(updateRoot, "launcher");
        if (Directory.Exists(launcherRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(launcherRoot))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) < now.AddHours(-1))
                    {
                        TryDeleteTree(directory);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        foreach (var file in Directory.EnumerateFiles(updateRoot, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            var isManagedCache = name.StartsWith("PathEcho-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("update-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("result-", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".download", StringComparison.OrdinalIgnoreCase);
            if (!isManagedCache)
            {
                continue;
            }

            try
            {
                if (File.GetLastWriteTimeUtc(file) < now.AddDays(-30))
                {
                    TryDeleteFile(file);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static string? ReadChannel()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "channel.txt");
        if (!File.Exists(path))
        {
            return null;
        }

        var channel = File.ReadAllText(path).Trim();
        return channel is "Lite" or "Full" ? channel : null;
    }

    private static System.Version ParseVersion(string value) =>
        System.Version.TryParse(value, out var version)
            ? version
            : throw new InvalidDataException($"版本格式无效：{value}");
}
