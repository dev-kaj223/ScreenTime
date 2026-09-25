using System.Security.Cryptography;
using System.Text;

namespace TimeGuard.Helpers;

/// <summary>
/// Password hashing using PBKDF2 + SHA-256. Salt and hash are stored as Base64 strings.
/// </summary>
public static class PasswordHelper
{
    private const int SaltBytes    = 32;
    private const int HashBytes    = 32;
    private const int Iterations   = 100_000;
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    public static (string hash, string salt) Hash(string password)
    {
        var saltBytes = RandomNumberGenerator.GetBytes(SaltBytes);
        var hashBytes = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            saltBytes,
            Iterations,
            Algorithm,
            HashBytes);

        return (Convert.ToBase64String(hashBytes), Convert.ToBase64String(saltBytes));
    }

    public static bool Verify(string password, string storedHash, string storedSalt)
    {
        byte[] saltBytes, expected;
        try
        {
            saltBytes = Convert.FromBase64String(storedSalt);
            expected = Convert.FromBase64String(storedHash);
        }
        catch (FormatException) { return false; }
        // Damaged credentials fail closed without resetting or replacing the password.
        if (saltBytes.Length != SaltBytes || expected.Length != HashBytes) return false;
        var hashBytes = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            saltBytes,
            Iterations,
            Algorithm,
            HashBytes);

        return CryptographicOperations.FixedTimeEquals(
            hashBytes,
            expected);
    }
}
