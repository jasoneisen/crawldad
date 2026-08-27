using System.Security.Cryptography;

namespace Crawldad.Portal.Auth;

/// <summary>Generates the human-typed one-time codes.</summary>
internal interface IOtpCodeGenerator
{
    /// <summary>A fresh cryptographically-random code from the unambiguous alphabet.</summary>
    string Generate();
}

/// <inheritdoc cref="IOtpCodeGenerator"/>
internal sealed class OtpCodeGenerator : IOtpCodeGenerator
{
    /// <summary>Uppercase letters + digits with every visually confusable character removed — no
    /// <c>0/O</c>, no <c>1/I/L</c>. 31 symbols → ~4.95 bits each, ~29.7 bits over a 6-character code, which is
    /// ample for a code that is short-lived (10 min), single-use, rate-limited, and attempt-capped.</summary>
    internal const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    /// <summary>The code length. Also the exact length the verify form requires.</summary>
    internal const int CodeLength = 6;

    public string Generate() =>
        // RandomNumberGenerator.GetInt32 is a cryptographically secure, unbiased index into the alphabet.
        string.Create(CodeLength, 0, static (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            }
        });
}
