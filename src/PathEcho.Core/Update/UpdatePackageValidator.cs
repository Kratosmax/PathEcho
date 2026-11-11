using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace PathEcho.Core.Update;

public static class UpdatePackageValidator
{
    private const int MaximumEntries = 20_000;
    private const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumImagePrefixBytes = 64 * 1024;
    private const long MaximumManagedAssemblyBytes = 512L * 1024 * 1024;
    private const long MaximumDescriptorBytes = 4L * 1024 * 1024;

    public static void Validate(string package, string expectedChannel, string expectedVersion)
    {
        using var archive = ZipFile.OpenRead(package);
        if (archive.Entries.Count is 0 or > MaximumEntries)
        {
            throw new InvalidDataException("更新包条目数量超出限制。");
        }

        long expandedBytes = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > MaximumExpandedBytes)
            {
                throw new InvalidDataException("更新包展开大小超出限制。");
            }

            var normalized = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normalized) ||
                normalized.Split(Path.DirectorySeparatorChar).Any(segment => segment == ".."))
            {
                throw new InvalidDataException($"更新包包含越界路径：{entry.FullName}");
            }

            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType == 0xA000)
            {
                throw new InvalidDataException($"更新包包含不允许的符号链接：{entry.FullName}");
            }

            var canonical = entry.FullName.Replace('\\', '/').TrimEnd('/');
            if (string.IsNullOrWhiteSpace(canonical) || canonical.Split('/').Any(segment =>
                segment is "" or "." or ".." || segment.Contains(':') || segment.EndsWith(' ') || segment.EndsWith('.') ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            {
                throw new InvalidDataException($"更新包包含不安全的路径：{entry.FullName}");
            }
            if (!names.Add(canonical)) throw new InvalidDataException($"更新包包含重复路径：{entry.FullName}");
        }

        foreach (var required in new[] { "PathEcho.exe", "PathEcho.Updater.exe", "channel.txt", "version.txt", ".pathecho-install-root" })
        {
            if (!names.Contains(required))
            {
                throw new InvalidDataException($"更新包缺少必要文件：{required}");
            }
        }

        var channel = ReadSmallEntry(archive, "channel.txt");
        var version = ReadSmallEntry(archive, "version.txt");
        var marker = ReadSmallEntry(archive, ".pathecho-install-root");
        if (!string.Equals(channel, expectedChannel, StringComparison.Ordinal) ||
            !string.Equals(version, expectedVersion, StringComparison.Ordinal) ||
            !string.Equals(marker, UpdateTrust.Product, StringComparison.Ordinal))
        {
            throw new InvalidDataException("更新包的产品、通道或版本不匹配。");
        }

        // R8：文件名与文本之外，还必须存在能真实启动的主程序、更新器与运行时依赖。
        // 必需集合取自 0.3.1 以来全部 Lite/Full 真实发布包的公共根布局，保持旧包兼容。
        ValidateRuntimePayload(archive, names, expectedVersion);
    }

    private static void ValidateRuntimePayload(ZipArchive archive, HashSet<string> names, string expectedVersion)
    {
        foreach (var required in new[]
        {
            "PathEcho.dll",
            "PathEcho.Core.dll",
            "PathEcho.Platform.Windows.dll",
            "PathEcho.deps.json",
            "PathEcho.runtimeconfig.json",
        })
        {
            if (!names.Contains(required))
            {
                throw new InvalidDataException($"更新包缺少必要运行文件：{required}");
            }
        }

        ValidatePortableExecutable(archive, "PathEcho.exe");
        ValidatePortableExecutable(archive, "PathEcho.Updater.exe");
        ValidateMainAssemblyVersion(archive, expectedVersion);
        ValidateRuntimeConfiguration(archive);
        ValidateDependencyManifest(archive);
    }

    private static void ValidatePortableExecutable(ZipArchive archive, string name)
    {
        var entry = FindEntry(archive, name);
        byte[] image;
        using (var source = entry.Open())
        {
            using var prefix = new MemoryStream();
            var buffer = new byte[8192];
            while (prefix.Length < MaximumImagePrefixBytes)
            {
                var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, MaximumImagePrefixBytes - prefix.Length));
                if (read == 0)
                {
                    break;
                }

                prefix.Write(buffer, 0, read);
            }

            image = prefix.ToArray();
        }

        if (!IsValidExecutableImage(image))
        {
            throw new InvalidDataException($"更新包的可执行文件不是有效的 PE 映像：{name}");
        }
    }

    private static bool IsValidExecutableImage(byte[] image)
    {
        if (image.Length < 0x40 || image[0] != (byte)'M' || image[1] != (byte)'Z')
        {
            return false;
        }

        var peHeaderOffset = BitConverter.ToInt32(image, 0x3C);
        if (peHeaderOffset <= 0 || peHeaderOffset + 26 > image.Length)
        {
            return false;
        }

        if (image[peHeaderOffset] != (byte)'P' || image[peHeaderOffset + 1] != (byte)'E' ||
            image[peHeaderOffset + 2] != 0 || image[peHeaderOffset + 3] != 0)
        {
            return false;
        }

        var machine = BitConverter.ToUInt16(image, peHeaderOffset + 4);
        if (machine is not (0x014c or 0x8664 or 0xAA64))
        {
            return false;
        }

        var sections = BitConverter.ToUInt16(image, peHeaderOffset + 6);
        if (sections is 0 or > 96)
        {
            return false;
        }

        return BitConverter.ToUInt16(image, peHeaderOffset + 24) is 0x10B or 0x20B;
    }

    private static void ValidateMainAssemblyVersion(ZipArchive archive, string expectedVersion)
    {
        if (!Version.TryParse(expectedVersion, out var expected))
        {
            throw new InvalidDataException($"更新包版本号无效：{expectedVersion}");
        }

        var entry = FindEntry(archive, "PathEcho.dll");
        if (entry.Length > MaximumManagedAssemblyBytes)
        {
            throw new InvalidDataException("更新包主程序集超出大小限制。");
        }

        using var buffer = new MemoryStream();
        using (var source = entry.Open())
        {
            source.CopyTo(buffer);
        }

        buffer.Position = 0;
        try
        {
            using var reader = new PEReader(buffer);
            if (!reader.HasMetadata)
            {
                throw new InvalidDataException("更新包主程序集缺少托管元数据：PathEcho.dll。");
            }

            var assemblyVersion = reader.GetMetadataReader().GetAssemblyDefinition().Version;
            if (assemblyVersion is null ||
                assemblyVersion.Major != expected.Major ||
                assemblyVersion.Minor != expected.Minor ||
                assemblyVersion.Build != expected.Build)
            {
                throw new InvalidDataException($"更新包主程序集版本 {assemblyVersion} 与更新版本 {expectedVersion} 不匹配。");
            }
        }
        catch (Exception exception) when (exception is BadImageFormatException or IOException)
        {
            throw new InvalidDataException("更新包主程序集不是有效的托管程序集：PathEcho.dll。", exception);
        }
    }

    private static void ValidateRuntimeConfiguration(ZipArchive archive)
    {
        using var document = ParseDescriptor(ReadDescriptor(archive, "PathEcho.runtimeconfig.json"));
        if (!document.RootElement.TryGetProperty("runtimeOptions", out var runtimeOptions) ||
            runtimeOptions.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("更新包运行时配置缺少 runtimeOptions。");
        }
    }

    private static void ValidateDependencyManifest(ZipArchive archive)
    {
        using var document = ParseDescriptor(ReadDescriptor(archive, "PathEcho.deps.json"));
        if (!document.RootElement.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("更新包依赖清单缺少 targets。");
        }
    }

    private static string ReadDescriptor(ZipArchive archive, string name)
    {
        var entry = FindEntry(archive, name);
        if (entry.Length > MaximumDescriptorBytes)
        {
            throw new InvalidDataException($"更新包描述文件超出大小限制：{name}");
        }

        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static JsonDocument ParseDescriptor(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("更新包描述文件不是有效的 JSON。", exception);
        }
    }

    private static ZipArchiveEntry FindEntry(ZipArchive archive, string name) =>
        archive.Entries.FirstOrDefault(entry => string.Equals(entry.FullName, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"更新包缺少 {name}");

    private static string ReadSmallEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidDataException($"更新包缺少 {name}");
        if (entry.Length > 4096)
        {
            throw new InvalidDataException($"{name} 超出大小限制。");
        }

        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd().Trim();
    }
}
