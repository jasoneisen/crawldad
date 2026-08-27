using System.Security.Cryptography;
using System.Text;

namespace Crawldad.Portal.Auth;

/// <summary>Hashes and verifies OTP codes. The stored value is a per-challenge salted SHA-256 of the code — the
/// plaintext is never persisted — and verification is a constant-time comparison so a timing side-channel cannot
/// distinguish a near-correct guess. A salted single-round hash is sufficient here because the code is
/// high-friction to brute force by policy (single-use, 10-minute lifetime, 5-attempt cap, per-email rate
/// limit), not because the hash is slow.</summary>
internal static class OtpHasher
{
    /// <summary>Hash <paramref name="code"/> under a fresh random salt. Returns both as Base64 to store on the
    /// challenge.</summary>
    internal static (string HashB64, string SaltB64) Hash(string code)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return (Convert.ToBase64String(Compute(code, salt)), Convert.ToBase64String(salt));
    }

    /// <summary>Constant-time check that <paramref name="code"/> hashes (under the stored salt) to the stored
    /// hash. <see cref="CryptographicOperations.FixedTimeEquals"/> also handles a length mismatch without
    /// leaking timing.</summary>
    internal static bool Verify(string code, string saltB64, string hashB64)
    {
        var salt = Convert.FromBase64String(saltB64);
        var expected = Convert.FromBase64String(hashB64);
        return CryptographicOperations.FixedTimeEquals(Compute(code, salt), expected);
    }

    private static byte[] Compute(string code, byte[] salt)
    {
        var codeBytes = Encoding.UTF8.GetBytes(code);
        var buffer = new byte[salt.Length + codeBytes.Length];
        salt.CopyTo(buffer.AsSpan());
        codeBytes.CopyTo(buffer.AsSpan(salt.Length));
        return SHA256.HashData(buffer);
    }
}
