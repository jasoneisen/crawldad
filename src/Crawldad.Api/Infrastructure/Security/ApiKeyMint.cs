using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Crawldad.Api.Infrastructure.Security;

/// <summary>A freshly minted API key: the <see cref="Raw"/> secret (returned to the caller exactly once and never
/// persisted), its stored <see cref="Hash"/> (SHA-256, lowercase hex), and the non-secret <see cref="Prefix"/> shown in
/// listings.</summary>
/// <param name="Raw">The full key — <c>ck_&lt;env&gt;_&lt;random&gt;</c>. Secret; surfaced once at issue, never stored or logged.</param>
/// <param name="Prefix">The non-secret display prefix (scheme + env + a few random chars).</param>
/// <param name="Hash">The SHA-256 of <see cref="Raw"/>, lowercase hex — the only persisted form of the secret.</param>
public readonly record struct MintedApiKey(string Raw, string Prefix, string Hash);

/// <summary>Mints and hashes registry API keys. A key is <c>ck_&lt;env&gt;_&lt;random&gt;</c>: the <c>ck</c> scheme tag, a
/// deployment env label (so a key is recognisable and a staging key can't be confused for a prod one), and 256 bits of
/// CSPRNG entropy, Base64Url-encoded. Only the SHA-256 hash is stored: the entropy makes a plain (unsalted) hash
/// pre-image-safe, which is why hashing — not reversible encryption — is the at-rest scheme (see THREAT_MODEL.md).</summary>
public static class ApiKeyMint
{
    /// <summary>The key scheme tag every minted key opens with.</summary>
    public const string Scheme = "ck";

    /// <summary>The random entropy per key, in bytes (256 bits).</summary>
    public const int EntropyBytes = 32;

    /// <summary>How many characters of the random tail the display <see cref="MintedApiKey.Prefix"/> keeps — enough to
    /// distinguish a tenant's keys in a listing, far too few to narrow the 256-bit secret.</summary>
    public const int PrefixRandomChars = 6;

    /// <summary>Mints a new key for the given deployment env label, returning the raw secret (once), its display prefix,
    /// and the hash to persist.</summary>
    /// <param name="envLabel">The deployment env moniker embedded in the key (e.g. <c>dev</c>, <c>staging</c>, <c>prod</c>).</param>
    public static MintedApiKey Issue(string envLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(envLabel);
        var random = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(EntropyBytes));
        var raw = $"{Scheme}_{envLabel}_{random}";
        var prefix = $"{Scheme}_{envLabel}_{random[..PrefixRandomChars]}";
        return new MintedApiKey(raw, prefix, Hash(raw));
    }

    /// <summary>The stored form of a raw key: SHA-256 of its UTF-8 bytes, lowercase hex. The presented key at auth is
    /// hashed identically and matched against the persisted value.</summary>
    public static string Hash(string rawKey)
    {
        ArgumentNullException.ThrowIfNull(rawKey);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey)));
    }
}
