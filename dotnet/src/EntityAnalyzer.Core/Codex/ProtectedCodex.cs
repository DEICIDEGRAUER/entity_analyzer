using System.Security.Cryptography;
using System.Text;

namespace EntityAnalyzer.Core.Codex;

/// <summary>
/// R1-22 «Приватные кодексы под паролем»: шифрование файла кодекса
/// AES-256-CBC с ключом из PBKDF2 (SHA-256, 100 000 итераций, свежая соль).
/// Формат: ASCII-магия «EACX1» + соль(16) + IV(16) + шифртекст. Только BCL —
/// без внешних зависимостей.
/// </summary>
public static class ProtectedCodex
{
    private const int SaltSize = 16;
    private const int IvSize = 16;
    private const int KeySize = 32;
    private const int Iterations = 100_000;
    private static readonly byte[] Magic = "EACX1"u8.ToArray();

    public static void EncryptFile(string sourceJsonPath, string targetPath, string password)
    {
        var plaintext = File.ReadAllBytes(sourceJsonPath);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var iv = RandomNumberGenerator.GetBytes(IvSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;

        using var output = File.Create(targetPath);
        output.Write(Magic);
        output.Write(salt);
        output.Write(iv);
        using (var crypto = new CryptoStream(output, aes.CreateEncryptor(), CryptoStreamMode.Write))
            crypto.Write(plaintext);
    }

    public static string DecryptFile(string path, string password)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < Magic.Length + SaltSize + IvSize ||
            !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("Файл не является защищённым кодексом (нет подписи EACX1).");

        var salt = bytes[Magic.Length..(Magic.Length + SaltSize)];
        var iv = bytes[(Magic.Length + SaltSize)..(Magic.Length + SaltSize + IvSize)];
        var cipher = bytes[(Magic.Length + SaltSize + IvSize)..];
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;

        using var input = new MemoryStream(cipher);
        using var crypto = new CryptoStream(input, aes.CreateDecryptor(), CryptoStreamMode.Read);
        using var reader = new StreamReader(crypto, Encoding.UTF8);
        return reader.ReadToEnd(); // неверный пароль → CryptographicException (padding)
    }
}
