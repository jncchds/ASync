using System.Security.Cryptography;
using System.Text;

namespace ASync.Core.Security;

/// <summary>
/// Short human-typeable codes (enrollment and device-code user codes) and long opaque secrets. Codes use an
/// alphabet without look-alike characters (no 0/O, 1/I/L, U) and are matched case-insensitively, ignoring
/// spaces and dashes. Only hashes are stored.
/// </summary>
public static class Codes
{
    private const string Alphabet = "ABCDEFGHJKMNPQRSTVWXYZ23456789"; // 30 symbols ≈ 4.9 bits each

    /// <summary>A code like <c>K7QD-M2XP-9RWA</c> (<paramref name="groups"/> groups of 4).</summary>
    public static string NewCode(int groups = 3)
    {
        var sb = new StringBuilder(groups * 5);
        for (var g = 0; g < groups; g++)
        {
            if (g > 0) sb.Append('-');
            for (var i = 0; i < 4; i++)
                sb.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        }
        return sb.ToString();
    }

    /// <summary>Uppercases and drops separators, so <c>k7qd m2xp</c> matches <c>K7QD-M2XP</c>.</summary>
    public static string Normalize(string code)
    {
        var sb = new StringBuilder(code.Length);
        foreach (var c in code)
        {
            if (c is '-' or ' ' or '\t') continue;
            sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>A 256-bit URL-safe random secret.</summary>
    public static string NewSecret() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>The stored form of a human-typed code (hex SHA-256 of its normalized text).</summary>
    public static string HashCode(string code) => HashSecret(Normalize(code));

    /// <summary>The stored form of a secret, compared exactly (hex SHA-256).</summary>
    public static string HashSecret(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
