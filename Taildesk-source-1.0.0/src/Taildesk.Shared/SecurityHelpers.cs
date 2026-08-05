using System.Security.Cryptography;
using System.Text;

namespace Taildesk.Shared;

public static class SecurityHelpers
{
    public static string CreateToken(int byteCount = 32) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool FixedTimeEquals(string first, string second)
    {
        var a = Encoding.UTF8.GetBytes(first ?? string.Empty);
        var b = Encoding.UTF8.GetBytes(second ?? string.Empty);
        try
        {
            return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(a);
            CryptographicOperations.ZeroMemory(b);
        }
    }

    public static string CreateHumanPassword(int length = 20)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        var bytes = RandomNumberGenerator.GetBytes(length);
        var chars = bytes.Select(value => alphabet[value % alphabet.Length]).ToArray();
        CryptographicOperations.ZeroMemory(bytes);
        return new string(chars);
    }

    public static string CreateMediaSignature(
        string secret,
        string method,
        string root,
        string relativePath,
        long expiresUnixSeconds,
        string nonce)
    {
        var payload = $"{method.ToUpperInvariant()}\n{root}\n{relativePath}\n{expiresUnixSeconds}\n{nonce}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }
}
