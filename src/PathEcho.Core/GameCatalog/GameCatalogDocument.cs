using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PathEcho.Core.Update;

namespace PathEcho.Core.GameCatalog;

public sealed record GameCatalogDocument
{
    public int SchemaVersion { get; init; } = 1;

    public long Revision { get; init; }

    public IReadOnlyList<GameCatalogEntry> Games { get; init; } = Array.Empty<GameCatalogEntry>();

    public string Signature { get; init; } = string.Empty;

    public byte[] GetCanonicalPayload()
    {
        var lines = new List<string>
        {
            SchemaVersion.ToString(CultureInfo.InvariantCulture),
            Revision.ToString(CultureInfo.InvariantCulture),
        };
        foreach (var game in Games)
        {
            lines.Add(Encode(game.Id));
            lines.Add(Encode(game.Name));
            lines.Add(game.Executables.Count.ToString(CultureInfo.InvariantCulture));
            lines.AddRange(game.Executables.Select(Encode));
            lines.Add(game.SavePathTemplates.Count.ToString(CultureInfo.InvariantCulture));
            lines.AddRange(game.SavePathTemplates.Select(Encode));
        }

        return Encoding.UTF8.GetBytes(string.Join('\n', lines));
    }

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
}

public sealed record GameCatalogEntry
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<string> Executables { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SavePathTemplates { get; init; } = Array.Empty<string>();
}

public static partial class GameCatalogVerifier
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly string[] AllowedTokens =
    {
        "{UserProfile}", "{Documents}", "{SavedGames}", "{LocalAppData}", "{RoamingAppData}", "{ProgramData}",
    };

    public static GameCatalogDocument ParseAndVerify(string json, string publicKeyPem = UpdateTrust.PublicKeyPem)
    {
        var catalog = JsonSerializer.Deserialize<GameCatalogDocument>(json, SerializerOptions)
            ?? throw new InvalidDataException("游戏目录内容为空。");
        Validate(catalog);

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(catalog.Signature);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("游戏目录签名格式无效。", exception);
        }

        using var verifier = ECDsa.Create();
        verifier.ImportFromPem(publicKeyPem);
        if (!verifier.VerifyData(
                catalog.GetCanonicalPayload(),
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence))
        {
            throw new InvalidDataException("游戏目录签名验证失败。");
        }

        return catalog;
    }

    public static void Validate(GameCatalogDocument catalog)
    {
        if (catalog.SchemaVersion != 1 || catalog.Revision < 1 || catalog.Games.Count > 5000)
        {
            throw new InvalidDataException("游戏目录的版本、修订号或条目数量无效。");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in catalog.Games)
        {
            if (!IdPattern().IsMatch(game.Id) || !ids.Add(game.Id) || string.IsNullOrWhiteSpace(game.Name) || game.Name.Length > 100)
            {
                throw new InvalidDataException("游戏目录包含无效或重复的游戏标识。");
            }

            if (game.Executables.Count is < 1 or > 16 || game.SavePathTemplates.Count is < 1 or > 16)
            {
                throw new InvalidDataException($"游戏 {game.Name} 的程序或存档规则数量无效。");
            }

            foreach (var executable in game.Executables)
            {
                if (executable.Length > 128 || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetFileName(executable), executable, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"游戏 {game.Name} 包含无效的程序文件名。");
                }
            }

            foreach (var template in game.SavePathTemplates)
            {
                ValidateTemplate(game.Name, template);
            }
        }
    }

    private static void ValidateTemplate(string gameName, string template)
    {
        if (string.IsNullOrWhiteSpace(template) || template.Length > 512 || template.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"游戏 {gameName} 包含无效的存档路径模板。");
        }

        var withoutTokens = template;
        foreach (var token in AllowedTokens)
        {
            withoutTokens = withoutTokens.Replace(token, string.Empty, StringComparison.Ordinal);
        }

        if (withoutTokens.Contains('{') || withoutTokens.Contains('}') || !AllowedTokens.Any(template.StartsWith))
        {
            throw new InvalidDataException($"游戏 {gameName} 使用了不允许的存档路径变量。");
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
