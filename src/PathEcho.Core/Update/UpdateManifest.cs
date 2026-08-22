using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PathEcho.Core.Update;

public sealed record UpdateManifest
{
    public string Product { get; init; } = UpdateTrust.Product;

    public string Version { get; init; } = string.Empty;

    public string Channel { get; init; } = string.Empty;

    public string DownloadUrl { get; init; } = string.Empty;

    public string Sha256 { get; init; } = string.Empty;

    public long PackageSize { get; init; }

    public string ReleaseNotes { get; init; } = string.Empty;

    public string Signature { get; init; } = string.Empty;

    public byte[] GetCanonicalPayload()
    {
        var encodedNotes = Convert.ToBase64String(Encoding.UTF8.GetBytes(ReleaseNotes));
        return Encoding.UTF8.GetBytes(string.Join('\n',
            Product,
            Version,
            Channel,
            DownloadUrl,
            Sha256.ToUpperInvariant(),
            PackageSize.ToString(CultureInfo.InvariantCulture),
            encodedNotes));
    }
}

public static class UpdateManifestVerifier
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static UpdateManifest ParseAndVerify(
        string json,
        string expectedChannel,
        string publicKeyPem = UpdateTrust.PublicKeyPem)
    {
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, SerializerOptions)
            ?? throw new InvalidDataException("更新清单内容为空。");
        Validate(manifest, expectedChannel);

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(manifest.Signature);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("更新清单签名格式无效。", exception);
        }

        using var verifier = ECDsa.Create();
        verifier.ImportFromPem(publicKeyPem);
        if (!verifier.VerifyData(
                manifest.GetCanonicalPayload(),
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence))
        {
            throw new InvalidDataException("更新清单签名验证失败。");
        }

        return manifest;
    }

    public static async Task<UpdateManifest> ReadAndVerifyAsync(
        string path,
        string expectedChannel,
        CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return ParseAndVerify(json, expectedChannel);
    }

    private static void Validate(UpdateManifest manifest, string expectedChannel)
    {
        if (!string.Equals(manifest.Product, UpdateTrust.Product, StringComparison.Ordinal) ||
            !string.Equals(manifest.Channel, expectedChannel, StringComparison.Ordinal))
        {
            throw new InvalidDataException("更新清单的产品或通道不匹配。");
        }

        if (!System.Version.TryParse(manifest.Version, out var version) || version.Major < 0)
        {
            throw new InvalidDataException("更新清单版本无效。");
        }

        if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var downloadUri))
        {
            throw new InvalidDataException("更新清单下载地址无效。");
        }

        _ = UpdateRoutePlanner.CreateRoutes(downloadUri, new UpdateNetworkOptions());
        if (manifest.Sha256.Length != 64 || !manifest.Sha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("更新清单 SHA-256 无效。");
        }

        if (manifest.PackageSize is <= 0 or > 2L * 1024 * 1024 * 1024)
        {
            throw new InvalidDataException("更新清单包大小超出限制。");
        }

        if (Encoding.UTF8.GetByteCount(manifest.ReleaseNotes) > 128 * 1024)
        {
            throw new InvalidDataException("更新公告超出大小限制。");
        }
    }
}
