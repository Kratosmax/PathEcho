using System.IO.Compression;

namespace PathEcho.Core.Update;

public static class UpdatePackageValidator
{
    private const int MaximumEntries = 20_000;
    private const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;

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

            names.Add(entry.FullName.TrimEnd('/'));
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
    }

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
