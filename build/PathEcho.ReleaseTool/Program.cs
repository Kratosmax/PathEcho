using System.Security.Cryptography;
using System.Text.Json;
using PathEcho.Core.Update;

if (args.Length != 8)
{
    Console.Error.WriteLine("用法：<private-key> <version> <channel> <package> <download-url> <release-notes> <output> <product>");
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
