using System.Security.Cryptography;
using EntityAnalyzer.Core.Codex;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// R1-22 «Приватные кодексы под паролем»: AES-256-CBC + PBKDF2(SHA256,
/// 100k итераций) — коммерческий сеттинг для чувствительных кодексов.
/// Формат: магия EACX1 + соль + IV + шифртекст.
/// </summary>
[Collection("LogSequential")]
public class ProtectedCodexTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ea_prot_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Encrypt_decrypt_roundtrip_preserves_json()
    {
        Directory.CreateDirectory(_dir);
        var src = Path.Combine(_dir, "codex.json");
        var enc = Path.Combine(_dir, "codex.eacx");
        File.WriteAllText(src, """{"version":2,"cards":[{"id":"c_1","name":"Секретный"}]}""");

        ProtectedCodex.EncryptFile(src, enc, "пароль123");
        Assert.True(File.Exists(enc));

        var json = ProtectedCodex.DecryptFile(enc, "пароль123");
        Assert.Contains("Секретный", json);
    }

    [Fact]
    public void Wrong_password_is_rejected()
    {
        Directory.CreateDirectory(_dir);
        var src = Path.Combine(_dir, "codex.json");
        var enc = Path.Combine(_dir, "codex.eacx");
        const string original = "{\"secret\":\"тайна\"}";
        File.WriteAllText(src, original);
        ProtectedCodex.EncryptFile(src, enc, "верный");

        // AES-CBC проверяет только выравнивание: случайный мусор после
        // неверного пароля с редкой вероятностью (~1/256) пройдёт проверку
        // padding и «расшифруется». Поэтому на наборе неверных паролей ждём
        // ошибку хотя бы один раз, а в остальных случаях — что исходный
        // JSON не вернулся.
        var threw = false;
        foreach (var wrong in new[] { "неверный", "Неверный", "невер", "неверный1", "другой", "Неверный2" })
        {
            try
            {
                var text = ProtectedCodex.DecryptFile(enc, wrong);
                Assert.NotEqual(original, text);
            }
            catch (CryptographicException)
            {
                threw = true;
            }
        }
        Assert.True(threw, "неверный пароль должен давать ошибку расшифровки");
    }

    [Fact]
    public void Encrypted_file_has_magic_and_is_not_plaintext()
    {
        Directory.CreateDirectory(_dir);
        var src = Path.Combine(_dir, "codex.json");
        var enc = Path.Combine(_dir, "codex.eacx");
        const string secret = "Совершенно секретная сущность";
        File.WriteAllText(src, secret);
        ProtectedCodex.EncryptFile(src, enc, "pw");

        var bytes = File.ReadAllBytes(enc);
        Assert.True(bytes.Length > 5);
        Assert.Equal("EACX1", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
        // Открытым текстом секрета нет.
        Assert.DoesNotContain(secret, System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Same_password_different_salt_produces_different_ciphertext()
    {
        Directory.CreateDirectory(_dir);
        var src = Path.Combine(_dir, "codex.json");
        File.WriteAllText(src, "{\"x\":1}");
        var e1 = Path.Combine(_dir, "a.eacx");
        var e2 = Path.Combine(_dir, "b.eacx");
        ProtectedCodex.EncryptFile(src, e1, "pw");
        ProtectedCodex.EncryptFile(src, e2, "pw");
        // Свежая соль каждый раз → разные байты (кроме магии).
        Assert.NotEqual(File.ReadAllBytes(e1)[5..], File.ReadAllBytes(e2)[5..]);
    }
}
