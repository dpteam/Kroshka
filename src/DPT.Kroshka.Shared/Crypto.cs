using System.Security.Cryptography;
using System.Text;

namespace DPT.Kroshka.Shared;

/// <summary>
/// Password hashing and token handling. PBKDF2-SHA256 with a per-user salt; tokens
/// are 256 random bits handed to the client once and stored only as a SHA-256 hash.
/// </summary>
public static class Crypto
{
    public const int SaltBytes = 16;
    public const int TokenBytes = 32;
    public const int DefaultIterations = 120_000;

    public static string NewToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    public static string NewId(int bytes = 12)
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();
    }

    public static string NewSalt()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(SaltBytes));
    }

    public static string HashPassword(string password, string salt, int iterations = DefaultIterations)
    {
        if (password == null) password = "";
        byte[] saltBytes = Convert.FromBase64String(salt);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), saltBytes, iterations, HashAlgorithmName.SHA256, 32);
        return iterations.ToString() + "$" + Convert.ToBase64String(hash);
    }

    public static bool VerifyPassword(string password, string salt, string stored)
    {
        if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(salt)) return false;
        int iterations = DefaultIterations;
        string expected = stored;

        int dollar = stored.IndexOf('$');
        if (dollar > 0)
        {
            if (!int.TryParse(stored[..dollar], out iterations)) return false;
            expected = stored[(dollar + 1)..];
        }

        try
        {
            string actual = HashPassword(password, salt, iterations);
            int actualDollar = actual.IndexOf('$');
            if (actualDollar > 0) actual = actual[(actualDollar + 1)..];
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expected));
        }
        catch
        {
            return false;
        }
    }

    public static string Sha256Hex(string text)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? ""))).ToLowerInvariant();
    }

    public static string Sha256Hex(byte[] data)
    {
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }

    /// <summary>Constant time compare that tolerates different lengths.</summary>
    public static bool FixedEquals(string a, string b)
    {
        if (a == null || b == null) return false;
        byte[] ba = Encoding.UTF8.GetBytes(a);
        byte[] bb = Encoding.UTF8.GetBytes(b);
        if (ba.Length != bb.Length)
        {
            // burn the same amount of time so a length difference is not a hint
            CryptographicOperations.FixedTimeEquals(ba, ba);
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}
