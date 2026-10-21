using System.Security.Cryptography;
using System.Text;

namespace TDU2Server.Data;

/// <summary>Passwörter (PBKDF2-SHA256) und Merk-Tokens (nur als SHA256-Hash gespeichert).</summary>
public static class Credentials
{
    public static string NewSalt() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

    public static string HashPassword(string password, string salt) =>
        Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Convert.FromBase64String(salt), 100_000, HashAlgorithmName.SHA256, 32));

    public static bool CheckPassword(Account a, string password) =>
        !string.IsNullOrEmpty(password)
        && CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(HashPassword(password, a.salt)), Convert.FromBase64String(a.passwordHash));

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? ""))).ToLowerInvariant();
}
