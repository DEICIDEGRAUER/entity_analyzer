using System.Security.Cryptography;
using EntityAnalyzer.Core.Licensing;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// R1-27 «Pro-ключ»: офлайн-лицензия — подписанный JSON
/// (BASE64(payload).BASE64(подпись)), ECDSA P-256 (замена Ed25519:
/// его нет в BCL, внешние зависимости запрещены). Проверка без сети.
/// </summary>
[Collection("LogSequential")]
public class LicenseKeyTests
{
    private static LicenseInfo Sample() => new(
        "Кафедра классической филологии",
        ["unlimited-corpus", "headless-api"],
        DateTimeOffset.Parse("2030-01-01T00:00:00Z"));

    [Fact]
    public void Issue_verify_roundtrip_preserves_fields()
    {
        var (priv, pub) = LicenseKey.GenerateKeyPair();
        var text = LicenseKey.Issue(Sample(), priv);

        var info = LicenseKey.Verify(text, pub);
        Assert.NotNull(info);
        Assert.Equal("Кафедра классической филологии", info.Licensee);
        Assert.Equal(["unlimited-corpus", "headless-api"], info.Features);
    }

    [Fact]
    public void Tampered_payload_is_rejected()
    {
        var (priv, pub) = LicenseKey.GenerateKeyPair();
        var text = LicenseKey.Issue(Sample(), priv);
        var parts = text.Split('.');
        var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]));
        var forged = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            json.Replace("unlimited-corpus", "unlimited-corpusX"))) + "." + parts[1];

        Assert.Null(LicenseKey.Verify(forged, pub));
    }

    [Fact]
    public void Wrong_public_key_is_rejected()
    {
        var (priv, _) = LicenseKey.GenerateKeyPair();
        var (_, otherPub) = LicenseKey.GenerateKeyPair();
        var text = LicenseKey.Issue(Sample(), priv);

        Assert.Null(LicenseKey.Verify(text, otherPub));
    }

    [Fact]
    public void Expired_license_is_rejected()
    {
        var (priv, pub) = LicenseKey.GenerateKeyPair();
        var expired = new LicenseInfo("Лицензиат", ["f"], DateTimeOffset.UtcNow.AddDays(-1));
        var text = LicenseKey.Issue(expired, priv);

        Assert.Null(LicenseKey.Verify(text, pub));
    }

    [Fact]
    public void Garbage_input_is_rejected_cleanly()
    {
        var (_, pub) = LicenseKey.GenerateKeyPair();
        Assert.Null(LicenseKey.Verify("мусор", pub));
        Assert.Null(LicenseKey.Verify("", pub));
    }
}
