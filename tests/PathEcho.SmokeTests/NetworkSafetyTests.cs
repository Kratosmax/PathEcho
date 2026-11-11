using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PathEcho.Core.GameCatalog;
using PathEcho.Core.Update;

/// <summary>
/// 网络与更新包安全回归：响应正文停滞（R6）与更新包预检（R8）。
/// 全部使用内存 mock 流与本地真实产物，不发真实网络请求；真实发布包扫描在缺少本地产物时自动跳过。
/// </summary>
internal static partial class NetworkSafetyTests
{
    internal static string PackageVersion { get; } =
        FormatVersion(typeof(NetworkSafetyTests).Assembly.GetName().Version);

    internal static async Task RunAsync(string testRoot)
    {
        var root = Path.Combine(testRoot, "network-safety");
        Directory.CreateDirectory(root);
        await ManifestStallFallsBackToNextRouteAsync(root).ConfigureAwait(false);
        await ManifestStallOnEveryRouteFailsFastAsync(root).ConfigureAwait(false);
        await ManifestUserCancellationOverridesTimeoutsAsync(root).ConfigureAwait(false);
        await CatalogStallFallsBackToTrustedCacheAsync(root).ConfigureAwait(false);
        await CatalogUserCancellationOverridesTimeoutsAsync(root).ConfigureAwait(false);
        await MalformedMetadataFailsOverAsync(root).ConfigureAwait(false);
        PackageValidationRejectsAmbiguousPaths(root);
        PackageValidationRejectsPlaceholderExecutables(root);
        PackageValidationRejectsMissingRuntimeDependencies(root);
        PackageValidationRejectsAssemblyVersionMismatch(root);
        PackageValidationAcceptsRealisticPackage(root);
        PackageValidationAcceptsRealLocalReleasePackages();
    }

    private static async Task MalformedMetadataFailsOverAsync(string root)
    {
        foreach (var json in new[] { "{", "null", "{\"Product\":\"PathEcho\",\"Channel\":\"Lite\",\"Version\":\"0.8.0\",\"DownloadUrl\":\"https://github.com/Kratosmax/PathEcho/releases/download/v0.8.0/x.zip\",\"Sha256\":null}" })
        {
            var rejected = false;
            try { UpdateManifestVerifier.ParseAndVerify(json, "Lite"); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "Malformed manifest bypassed invalid-input classification.");
        }
        var calls = 0;
        using (var handler = new RouteScriptHandler(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{") }; }))
        using (var client = new HttpClient(handler))
        {
            var options = new UpdateNetworkOptions { UrlRoutes = [new() { BaseUrl = "https://proxy.example", Priority = 10 }, new() { IsDirect = true, Priority = 5 }] };
            var failed = false;
            try { await new UpdateManifestClient(client).FetchAsync(new Uri("https://github.com/Kratosmax/PathEcho/releases/latest/download/update.json"), "Lite", options); }
            catch (InvalidOperationException) { failed = true; }
            Assert(failed && calls == 2, "Invalid JSON escaped fallback before the next update route was attempted.");
        }
        var (valid, key) = CreateSignedCatalogJson(11); var catalogCalls = 0;
        using (var handler = new RouteScriptHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(++catalogCalls == 1 ? "{" : valid) }))
        using (var client = new HttpClient(handler))
        {
            var options = new UpdateNetworkOptions { UrlRoutes = [new() { BaseUrl = "https://proxy.example", Priority = 10 }, new() { IsDirect = true, Priority = 5 }] };
            var result = await new GameCatalogClient(client, Path.Combine(root, "json-fallback", "catalog.json"), key).FetchAsync(options);
            Assert(catalogCalls == 2 && result.Catalog.Revision == 11 && result.RouteFailures.Count == 1, "Malformed catalog did not fall back to a valid signed route.");
        }
        foreach (var json in new[] { "{", "{\"SchemaVersion\":1,\"Revision\":1,\"Games\":null}", "{\"SchemaVersion\":1,\"Revision\":1,\"Games\":[null]}" })
        {
            var rejected = false; try { GameCatalogVerifier.ParseAndVerify(json); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "Null catalog data escaped schema validation.");
        }
    }

    private static void PackageValidationRejectsAmbiguousPaths(string root)
    {
        foreach (var extra in new[] { "PATHecho.exe", "payload.sav:stream", "folder/../escape", "folder./file" })
        {
            var package = Path.Combine(root, "ambiguous-" + Guid.NewGuid().ToString("N") + ".zip");
            CreateUpdatePackage(package, false);
            using (var archive = ZipFile.Open(package, ZipArchiveMode.Update))
            {
                using var writer = new StreamWriter(archive.CreateEntry(extra).Open()); writer.Write("invalid");
            }
            var rejected = false;
            try { UpdatePackageValidator.Validate(package, "Lite", PackageVersion); }
            catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("Package accepted ambiguous, alternate-stream or traversal path: " + extra);
        }
    }

    private static async Task ManifestStallFallsBackToNextRouteAsync(string root)
    {
        // 清单客户端固定使用产品公钥验签，测试签名无法产出可验证清单；直连线路返回完整正文
        // 并到达签名校验环节，即证明停滞线路被释放且确实切换到了下一条线路。
        var json = CreateSignedManifestJson().Json;
        var stallStream = new StallingStream(Encoding.UTF8.GetBytes("{\"partial\""));
        using var handler = new RouteScriptHandler(request => request.RequestUri!.Host == "proxy.example"
            ? CreateStalledResponse(stallStream)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json)) });
        using var client = new HttpClient(handler);
        var options = new UpdateNetworkOptions
        {
            UrlRoutes = new[]
            {
                new UpdateUrlRoute { BaseUrl = "https://proxy.example", Priority = 10 },
                UpdateUrlRoute.Direct with { Priority = 1 },
            },
        };

        var watch = Stopwatch.StartNew();
        var exception = await ThrowsAsync<InvalidOperationException>(
            () => new UpdateManifestClient(client).FetchAsync(
                new Uri("https://github.com/Kratosmax/PathEcho/releases/latest/download/update-lite.json"),
                "Lite",
                options,
                stallTimeout: TimeSpan.FromMilliseconds(200),
                routeTimeout: TimeSpan.FromSeconds(15)),
            "停滞线路之后没有继续尝试直连线路。").ConfigureAwait(false);
        watch.Stop();

        Assert(
            exception.Message.Contains("停滞", StringComparison.Ordinal) &&
            exception.Message.Contains("签名验证失败", StringComparison.Ordinal),
            $"故障转移结果不符合预期：{exception.Message}");
        Assert(
            handler.RequestHosts.SequenceEqual(new[] { "proxy.example", "github.com" }),
            $"清单线路顺序不正确：{string.Join(",", handler.RequestHosts)}");
        Assert(stallStream.DisposeCount >= 1, "正文停滞超时后没有释放上游流。");
        Assert(watch.Elapsed < TimeSpan.FromSeconds(20), $"停滞故障转移耗时异常：{watch.Elapsed}");
    }

    private static async Task ManifestStallOnEveryRouteFailsFastAsync(string root)
    {
        var stallStream = new StallingStream(Encoding.UTF8.GetBytes("{\"partial\""));
        using var handler = new RouteScriptHandler(_ => CreateStalledResponse(stallStream));
        using var client = new HttpClient(handler);

        var watch = Stopwatch.StartNew();
        var exception = await ThrowsAsync<InvalidOperationException>(
            () => new UpdateManifestClient(client).FetchAsync(
                new Uri("https://github.com/Kratosmax/PathEcho/releases/latest/download/update-lite.json"),
                "Lite",
                stallTimeout: TimeSpan.FromMilliseconds(200),
                routeTimeout: TimeSpan.FromSeconds(15)),
            "全部线路正文停滞时没有快速失败。").ConfigureAwait(false);
        watch.Stop();

        Assert(
            exception.Message.Contains("停滞", StringComparison.Ordinal),
            $"失败信息缺少停滞原因：{exception.Message}");
        Assert(stallStream.DisposeCount >= 1, "全部线路停滞超时后没有释放上游流。");
        Assert(watch.Elapsed < TimeSpan.FromSeconds(20), $"全部线路停滞的失败耗时异常：{watch.Elapsed}");
    }

    private static async Task ManifestUserCancellationOverridesTimeoutsAsync(string root)
    {
        var stallStream = new StallingStream(Encoding.UTF8.GetBytes("{\"partial\""));
        using var handler = new RouteScriptHandler(_ => CreateStalledResponse(stallStream));
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var watch = Stopwatch.StartNew();
        await ThrowsAsync<OperationCanceledException>(
            () => new UpdateManifestClient(client).FetchAsync(
                new Uri("https://github.com/Kratosmax/PathEcho/releases/latest/download/update-lite.json"),
                "Lite",
                cancellationToken: cancellation.Token,
                stallTimeout: TimeSpan.FromSeconds(30),
                routeTimeout: TimeSpan.FromSeconds(120)),
            "用户取消没有保持最高优先级（被当成线路失败继续重试）。").ConfigureAwait(false);
        watch.Stop();

        Assert(cancellation.IsCancellationRequested, "取消断言未按计划触发。");
        Assert(stallStream.DisposeCount >= 1, "用户取消后没有释放上游流。");
        Assert(watch.Elapsed < TimeSpan.FromSeconds(20), $"用户取消响应耗时异常：{watch.Elapsed}");
    }

    private static async Task CatalogStallFallsBackToTrustedCacheAsync(string root)
    {
        var (json, publicKey) = CreateSignedCatalogJson(revision: 7);
        var cachePath = Path.Combine(root, "catalog-cache", "game-catalog.json");
        using (var primingHandler = new RouteScriptHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json)),
        }))
        using (var primingClient = new HttpClient(primingHandler))
        {
            var primed = await new GameCatalogClient(primingClient, cachePath, publicKey).FetchAsync()
                .ConfigureAwait(false);
            Assert(!primed.UsedCachedCopy, "预热目录获取意外命中缓存。");
        }

        var stallStream = new StallingStream("{"u8.ToArray());
        using var handler = new RouteScriptHandler(_ => CreateStalledResponse(stallStream));
        using var client = new HttpClient(handler);

        var watch = Stopwatch.StartNew();
        var result = await new GameCatalogClient(client, cachePath, publicKey).FetchAsync(
            stallTimeout: TimeSpan.FromMilliseconds(200),
            routeTimeout: TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        watch.Stop();

        Assert(result.UsedCachedCopy, "目录正文停滞后没有回退可信缓存。");
        Assert(result.Catalog.Revision == 7, "可信缓存回退后的修订号不正确。");
        Assert(
            result.RouteFailures.Any(failure => failure.Contains("停滞", StringComparison.Ordinal)),
            $"目录线路失败没有记录停滞原因：{string.Join(";", result.RouteFailures)}");
        Assert(stallStream.DisposeCount >= 1, "目录停滞超时后没有释放上游流。");
        Assert(watch.Elapsed < TimeSpan.FromSeconds(20), $"目录停滞回退耗时异常：{watch.Elapsed}");
    }

    private static async Task CatalogUserCancellationOverridesTimeoutsAsync(string root)
    {
        var (_, publicKey) = CreateSignedCatalogJson(revision: 7);
        var cachePath = Path.Combine(root, "catalog-cancel-cache", "game-catalog.json");
        var stallStream = new StallingStream("{"u8.ToArray());
        using var handler = new RouteScriptHandler(_ => CreateStalledResponse(stallStream));
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var watch = Stopwatch.StartNew();
        await ThrowsAsync<OperationCanceledException>(
            () => new GameCatalogClient(client, cachePath, publicKey).FetchAsync(
                cancellationToken: cancellation.Token,
                stallTimeout: TimeSpan.FromSeconds(30),
                routeTimeout: TimeSpan.FromSeconds(120)),
            "目录获取的用户取消没有保持最高优先级。").ConfigureAwait(false);
        watch.Stop();

        Assert(cancellation.IsCancellationRequested, "取消断言未按计划触发。");
        Assert(stallStream.DisposeCount >= 1, "用户取消后没有释放上游流。");
        Assert(watch.Elapsed < TimeSpan.FromSeconds(20), $"目录用户取消响应耗时异常：{watch.Elapsed}");
    }

    private static void PackageValidationRejectsPlaceholderExecutables(string root)
    {
        var minimal = Path.Combine(root, "placeholder-minimal.zip");
        using (var archive = ZipFile.Open(minimal, ZipArchiveMode.Create))
        {
            WriteText(archive, "PathEcho.exe", "placeholder");
            WriteText(archive, "PathEcho.Updater.exe", "placeholder");
            WriteText(archive, "channel.txt", "Lite");
            WriteText(archive, "version.txt", PackageVersion);
            WriteText(archive, ".pathecho-install-root", UpdateTrust.Product);
        }

        var missing = Throws<InvalidDataException>(
            () => UpdatePackageValidator.Validate(minimal, "Lite", PackageVersion),
            "只有文件名和文本的占位包没有因缺少运行文件被拒绝。");
        Assert(
            missing.Message.Contains("必要运行文件", StringComparison.Ordinal),
            $"占位包失败原因不正确：{missing.Message}");

        var decorated = Path.Combine(root, "placeholder-decorated.zip");
        CreateRealisticPackage(decorated, versionText: PackageVersion, extraOverrides: new Dictionary<string, byte[]>
        {
            ["PathEcho.exe"] = Encoding.UTF8.GetBytes("placeholder"),
            ["PathEcho.Updater.exe"] = Encoding.UTF8.GetBytes("placeholder"),
        });
        var fakeExecutable = Throws<InvalidDataException>(
            () => UpdatePackageValidator.Validate(decorated, "Lite", PackageVersion),
            "文本冒充的 exe 没有被 PE 结构检查拒绝。");
        Assert(
            fakeExecutable.Message.Contains("PE 映像", StringComparison.Ordinal),
            $"假 exe 失败原因不正确：{fakeExecutable.Message}");
    }

    private static void PackageValidationRejectsMissingRuntimeDependencies(string root)
    {
        var withoutRuntimeConfig = Path.Combine(root, "missing-runtimeconfig.zip");
        CreateRealisticPackage(withoutRuntimeConfig, omittedEntries: new[] { "PathEcho.runtimeconfig.json" });
        var missingConfig = Throws<InvalidDataException>(
            () => UpdatePackageValidator.Validate(withoutRuntimeConfig, "Lite", PackageVersion),
            "缺少 runtimeconfig 的包没有被拒绝。");
        Assert(
            missingConfig.Message.Contains("PathEcho.runtimeconfig.json", StringComparison.Ordinal),
            $"缺少 runtimeconfig 的失败原因不正确：{missingConfig.Message}");

        var withoutCore = Path.Combine(root, "missing-core.zip");
        CreateRealisticPackage(withoutCore, omittedEntries: new[] { "PathEcho.Core.dll" });
        var missingCore = Throws<InvalidDataException>(
            () => UpdatePackageValidator.Validate(withoutCore, "Lite", PackageVersion),
            "缺少核心依赖的包没有被拒绝。");
        Assert(
            missingCore.Message.Contains("PathEcho.Core.dll", StringComparison.Ordinal),
            $"缺少核心依赖的失败原因不正确：{missingCore.Message}");

        var brokenConfig = Path.Combine(root, "broken-runtimeconfig.zip");
        CreateRealisticPackage(brokenConfig, extraOverrides: new Dictionary<string, byte[]>
        {
            ["PathEcho.runtimeconfig.json"] = Encoding.UTF8.GetBytes("not-json"),
        });
        var invalidConfig = Throws<InvalidDataException>(
            () => UpdatePackageValidator.Validate(brokenConfig, "Lite", PackageVersion),
            "runtimeconfig 内容损坏的包没有被拒绝。");
        Assert(
            invalidConfig.Message.Contains("JSON", StringComparison.Ordinal),
            $"runtimeconfig 损坏的失败原因不正确：{invalidConfig.Message}");
    }

    private static void PackageValidationRejectsAssemblyVersionMismatch(string root)
    {
        var mismatched = Path.Combine(root, "version-mismatch.zip");
        CreateRealisticPackage(mismatched, versionText: "9.9.9");
        var mismatch = Throws<InvalidDataException>(
            () => UpdatePackageValidator.Validate(mismatched, "Lite", "9.9.9"),
            "程序集版本与 version.txt 不一致的包没有被拒绝。");
        Assert(
            mismatch.Message.Contains("主程序集版本", StringComparison.Ordinal),
            $"版本不匹配的失败原因不正确：{mismatch.Message}");

        var textAssembly = Path.Combine(root, "text-assembly.zip");
        CreateRealisticPackage(textAssembly, extraOverrides: new Dictionary<string, byte[]>
        {
            ["PathEcho.dll"] = Encoding.UTF8.GetBytes("not an assembly"),
        });
        var invalidAssembly = Throws<InvalidDataException>(
            () => UpdatePackageValidator.Validate(textAssembly, "Lite", PackageVersion),
            "文本冒充的 PathEcho.dll 没有被拒绝。");
        Assert(
            invalidAssembly.Message.Contains("托管", StringComparison.Ordinal),
            $"假程序集失败原因不正确：{invalidAssembly.Message}");
    }

    private static void PackageValidationAcceptsRealisticPackage(string root)
    {
        var realistic = Path.Combine(root, "realistic.zip");
        CreateUpdatePackage(realistic, malicious: false);
        UpdatePackageValidator.Validate(realistic, "Lite", PackageVersion);
    }

    [GeneratedRegex(@"^PathEcho-(\d+\.\d+\.\d+)-(Lite|Full)\.zip$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleasePackagePattern();

    /// <summary>
    /// 回放仓库 temp 内 release/preview 根目录下的真实发布包（0.3.1 以来的 Lite/Full），新预检必须继续接受它们。
    /// 只扫发布产物目录，避免与并行回归在 temp 其他位置产生的测试包互相干扰；
    /// 干净检出没有本地包时跳过，不视为失败；本机完整回放在隔离探针中另行执行。
    /// </summary>
    private static void PackageValidationAcceptsRealLocalReleasePackages()
    {
        var repositoryRoot = FindRepositoryRoot();
        var tempRoot = Path.Combine(repositoryRoot, "temp");
        if (!Directory.Exists(tempRoot))
        {
            return;
        }

        var packageRoots = Directory.EnumerateDirectories(tempRoot)
            .Where(directory =>
                Path.GetFileName(directory).StartsWith("release", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(directory).StartsWith("preview", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var package in packageRoots.SelectMany(
                     root => Directory.EnumerateFiles(root, "PathEcho-*.zip", SearchOption.AllDirectories)))
        {
            var match = ReleasePackagePattern().Match(Path.GetFileName(package));
            if (!match.Success)
            {
                continue;
            }

            UpdatePackageValidator.Validate(package, match.Groups[2].Value, match.Groups[1].Value);
        }
    }

    internal static void CreateUpdatePackage(string path, bool malicious) =>
        CreateRealisticPackage(path, malicious: malicious);

    private static string CreateRealisticPackage(
        string path,
        string? versionText = null,
        bool malicious = false,
        string[]? omittedEntries = null,
        IReadOnlyDictionary<string, byte[]>? extraOverrides = null)
    {
        versionText ??= PackageVersion;
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["PathEcho.exe"] = HostExecutableImage,
            ["PathEcho.Updater.exe"] = HostExecutableImage,
            ["PathEcho.dll"] = TestAssemblyImage,
            ["PathEcho.Core.dll"] = TestAssemblyImage,
            ["PathEcho.Platform.Windows.dll"] = TestAssemblyImage,
            ["PathEcho.runtimeconfig.json"] = Encoding.UTF8.GetBytes("{\"runtimeOptions\":{\"tfm\":\"net8.0\"}}"),
            ["PathEcho.deps.json"] = Encoding.UTF8.GetBytes("{\"targets\":{}}"),
            ["channel.txt"] = Encoding.UTF8.GetBytes("Lite"),
            ["version.txt"] = Encoding.UTF8.GetBytes(versionText),
            [".pathecho-install-root"] = Encoding.UTF8.GetBytes(UpdateTrust.Product),
        };

        foreach (var omitted in omittedEntries ?? Array.Empty<string>())
        {
            entries.Remove(omitted);
        }

        foreach (var (name, content) in extraOverrides ?? NoOverrides)
        {
            entries[name] = content;
        }

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var stream = entry.Open();
            stream.Write(content, 0, content.Length);
        }

        if (malicious)
        {
            WriteText(archive, "../escape.txt", "escape");
        }

        return versionText;
    }

    private static void WriteText(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static (string Json, string PublicKey) CreateSignedManifestJson()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = new UpdateManifest
        {
            Version = "0.2.0",
            Channel = "Lite",
            DownloadUrl = "https://github.com/Kratosmax/PathEcho/releases/download/v0.2.0/PathEcho-0.2.0-Lite.zip",
            Sha256 = new string('A', 64),
            PackageSize = 1024,
            ReleaseNotes = "网络回归清单",
        };
        var signature = signer.SignData(
            manifest.GetCanonicalPayload(),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        manifest = manifest with { Signature = Convert.ToBase64String(signature) };
        return (JsonSerializer.Serialize(manifest), signer.ExportSubjectPublicKeyInfoPem());
    }

    private static (string Json, string PublicKey) CreateSignedCatalogJson(long revision)
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var catalog = new GameCatalogDocument
        {
            Revision = revision,
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
        return (JsonSerializer.Serialize(catalog), signer.ExportSubjectPublicKeyInfoPem());
    }

    private static HttpResponseMessage CreateStalledResponse(StallingStream stream) => new(HttpStatusCode.OK)
    {
        Content = new StreamContent(stream),
    };

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PathEcho.sln")))
            {
                return current.FullName;
            }

            current = current.Parent!;
        }

        throw new DirectoryNotFoundException("无法定位 PathEcho 仓库根目录。");
    }

    private static string FormatVersion(Version? version) =>
        version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static TException Throws<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }
        catch (Exception other)
        {
            throw new InvalidOperationException($"{message} 实际抛出 {other.GetType().Name}：{other.Message}", other);
        }

        throw new InvalidOperationException($"{message} 实际没有抛出异常。");
    }

    private static async Task<TException> ThrowsAsync<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException exception)
        {
            return exception;
        }
        catch (Exception other)
        {
            throw new InvalidOperationException($"{message} 实际抛出 {other.GetType().Name}：{other.Message}", other);
        }

        throw new InvalidOperationException($"{message} 实际没有抛出异常。");
    }

    private static readonly IReadOnlyDictionary<string, byte[]> NoOverrides = new Dictionary<string, byte[]>();

    private static readonly byte[] HostExecutableImage = File.ReadAllBytes(
        Environment.ProcessPath ?? throw new InvalidOperationException("无法定位测试宿主可执行文件。"));

    private static readonly byte[] TestAssemblyImage = File.ReadAllBytes(
        typeof(NetworkSafetyTests).Assembly.Location);

    /// <summary>
    /// 响应头已返回、正文先给出片段后永久停滞的流；记录 Dispose 次数用于断言超时后释放。
    /// </summary>
    private sealed class StallingStream(byte[] prefix) : Stream
    {
        private int _offset;

        public int DisposeCount { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("仅支持异步读取。");

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (_offset < prefix.Length)
            {
                var take = Math.Min(buffer.Length, prefix.Length - _offset);
                prefix.AsMemory(_offset, take).CopyTo(buffer);
                _offset += take;
                return take;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            DisposeCount++;
            base.Dispose(disposing);
        }
    }

    private sealed class RouteScriptHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        public List<string> RequestHosts { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestHosts.Add(request.RequestUri!.Host);
            return Task.FromResult(send(request));
        }
    }
}
