using System.Security.Cryptography;
using System.Text.Json;
using PathEcho.Core.GameCatalog;
using PathEcho.Core.Update;

if (args.Length == 4 && string.Equals(args[0], "catalog", StringComparison.Ordinal))
{
    var catalogPrivateKeyPath = Path.GetFullPath(args[1]);
    var sourcePath = Path.GetFullPath(args[2]);
    var catalogOutputPath = Path.GetFullPath(args[3]);
    var sourceJson = await File.ReadAllTextAsync(sourcePath);
    var catalog = JsonSerializer.Deserialize<GameCatalogDocument>(sourceJson, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
    }) ?? throw new InvalidDataException("游戏目录源文件为空。");
    GameCatalogVerifier.Validate(catalog);
    if (File.Exists(catalogOutputPath))
    {
        var current = GameCatalogVerifier.ParseAndVerify(await File.ReadAllTextAsync(catalogOutputPath));
        if (catalog.GetCanonicalPayload().SequenceEqual(current.GetCanonicalPayload()))
        {
            return 0;
        }

        if (catalog.Revision <= current.Revision)
        {
            throw new InvalidDataException("游戏目录内容变化时必须递增 revision。");
        }
    }

    using var catalogSigner = ECDsa.Create();
    catalogSigner.ImportFromPem(await File.ReadAllTextAsync(catalogPrivateKeyPath));
    var catalogSignature = catalogSigner.SignData(
        catalog.GetCanonicalPayload(),
        HashAlgorithmName.SHA256,
        DSASignatureFormat.Rfc3279DerSequence);
    catalog = catalog with { Signature = Convert.ToBase64String(catalogSignature) };
    var catalogJson = JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true });
    _ = GameCatalogVerifier.ParseAndVerify(catalogJson);
    Directory.CreateDirectory(Path.GetDirectoryName(catalogOutputPath)!);
    await File.WriteAllTextAsync(catalogOutputPath, catalogJson + Environment.NewLine, new System.Text.UTF8Encoding(false));
    return 0;
}

if (args.Length != 8)
{
    Console.Error.WriteLine("用法：<private-key> <version> <channel> <package> <download-url> <release-notes> <output> <product>，或 catalog <private-key> <source> <output>");
    return 2;
}

var privateKeyPath = Path.GetFullPath(args[0]);
var packagePath = Path.GetFullPath(args[3]);
var notesPath = Path.GetFullPath(args[5]);
var outputPath = Path.GetFullPath(args[6]);
await using var packageStream = File.OpenRead(packagePath);
var manifest = new UpdateManifest
{
    Product = args[7],
    Version = args[1],
    Channel = args[2],
    DownloadUrl = args[4],
    Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(packageStream)),
    PackageSize = packageStream.Length,
    ReleaseNotes = await File.ReadAllTextAsync(notesPath),
};

using var signer = ECDsa.Create();
signer.ImportFromPem(await File.ReadAllTextAsync(privateKeyPath));
var signature = signer.SignData(
    manifest.GetCanonicalPayload(),
    HashAlgorithmName.SHA256,
    DSASignatureFormat.Rfc3279DerSequence);
manifest = manifest with { Signature = Convert.ToBase64String(signature) };
var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
_ = UpdateManifestVerifier.ParseAndVerify(json, manifest.Channel);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
await File.WriteAllTextAsync(outputPath, json + Environment.NewLine, new System.Text.UTF8Encoding(false));
return 0;
