using System.Text.Json;
using PathEcho.Core.Models;

namespace PathEcho.Core.Storage;

public static class GameProfileTransfer
{
    private const int MaximumBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static async Task ExportAsync(
        GameBackupProfile profile,
        string defaultBackupDirectory,
        string destination,
        CancellationToken cancellationToken = default)
    {
        profile.Validate();
        var document = new TransferDocument
        {
            Format = "PathEcho.GameProfile",
            SchemaVersion = 1,
            Profile = profile with { BackupDirectory = profile.ResolveBackupDirectory(defaultBackupDirectory) },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Options);
        if (bytes.Length > MaximumBytes)
        {
            throw new InvalidDataException("游戏配置超过 1 MiB，无法导出。");
        }

        var path = Path.GetFullPath(destination);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static async Task<GameBackupProfile> ReadImportDraftAsync(
        string source,
        IReadOnlyList<GameBackupProfile> existingProfiles,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        if (stream.Length > MaximumBytes)
        {
            throw new InvalidDataException("游戏配置超过 1 MiB，无法导入。");
        }

        var bytes = new byte[MaximumBytes + 1];
        var length = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (length > MaximumBytes)
        {
            throw new InvalidDataException("游戏配置超过 1 MiB，无法导入。");
        }

        TransferDocument document;
        try
        {
            document = JsonSerializer.Deserialize<TransferDocument>(bytes.AsSpan(0, length), Options)
                ?? throw new InvalidDataException("游戏配置为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("这不是有效的 PathEcho 游戏配置文件。", exception);
        }

        if (document.Format != "PathEcho.GameProfile" || document.SchemaVersion != 1 || document.Profile is null)
        {
            throw new InvalidDataException("不支持的游戏配置格式或版本。");
        }

        var profile = document.Profile;
        if (profile.Id == Guid.Empty || profile.ImportantFilePatterns is null)
        {
            throw new InvalidDataException("游戏配置缺少有效档案 ID 或备份规则。");
        }
        profile.Validate();
        var sourceIds = profile.LinkedBackupProfileIds.Append(profile.Id).ToHashSet();
        if (existingProfiles.Any(existing => sourceIds.Contains(existing.Id) || existing.LinkedBackupProfileIds.Any(sourceIds.Contains)))
        {
            throw new InvalidOperationException("该游戏配置已存在或已导入，请在游戏存档页编辑已有档案。");
        }

        var draft = profile with
        {
            Id = Guid.NewGuid(),
            IsEnabled = true,
            LinkedBackupProfileIds = profile.LinkedBackupProfileIds.Append(profile.Id).Distinct().ToArray(),
        };
        draft.Validate();
        return draft;
    }

    private sealed record TransferDocument
    {
        public required string Format { get; init; }
        public required int SchemaVersion { get; init; }
        public DateTimeOffset ExportedAtUtc { get; init; } = DateTimeOffset.UtcNow;
        public required GameBackupProfile Profile { get; init; }
    }
}
