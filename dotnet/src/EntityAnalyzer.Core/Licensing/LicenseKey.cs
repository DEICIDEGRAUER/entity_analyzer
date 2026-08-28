using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EntityAnalyzer.Core.Licensing;

/// <summary>Содержимое лицензии Pro.</summary>
public sealed record LicenseInfo(string Licensee, string[] Features, DateTimeOffset? ExpiresAt);

/// <summary>
/// R1-27 «Pro-ключ для тяжёлых корпусов»: офлайн-лицензия — подписанный
/// JSON в формате BASE64(payload).BASE64(подпись). Ключ — ECDSA P-256:
/// Ed25519 отсутствует в BCL, а внешние зависимости ядру запрещены
/// (осознанная замена, криптостойкость сопоставима). Проверка полностью
/// офлайн по встроенному/переданному публичному ключу.
/// </summary>
public static class LicenseKey
{
    public static (string PrivatePem, string PublicPem) GenerateKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var priv = ecdsa.ExportPkcs8PrivateKeyPem();
        var pub = ecdsa.ExportSubjectPublicKeyInfoPem();
        return (priv, pub);
    }

    public static string Issue(LicenseInfo info, string privatePem)
    {
        var payload = JsonSerializer.Serialize(info, JsonOpts);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privatePem);
        var signature = ecdsa.SignData(payloadBytes, HashAlgorithmName.SHA256);

        return Convert.ToBase64String(payloadBytes) + "." + Convert.ToBase64String(signature);
    }

    public static LicenseInfo? Verify(string licenseText, string publicPem)
    {
        try
        {
            var dot = licenseText.IndexOf('.');
            if (dot <= 0 || dot == licenseText.Length - 1) return null;

            var payloadBytes = Convert.FromBase64String(licenseText[..dot]);
            var signature = Convert.FromBase64String(licenseText[(dot + 1)..]);

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(publicPem);
            if (!ecdsa.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256))
                return null;

            var info = JsonSerializer.Deserialize<LicenseInfo>(payloadBytes, JsonOpts);
            if (info is null) return null;
            if (info.ExpiresAt is { } exp && exp < DateTimeOffset.UtcNow) return null;
            return info;
        }
        catch (Exception)
        {
            return null; // мусор на входе — не исключение, а отказ
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
