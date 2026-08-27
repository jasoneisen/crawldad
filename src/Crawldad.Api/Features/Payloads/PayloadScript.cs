using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Crawldad.Api.Infrastructure.Security;

namespace Crawldad.Api.Features.Payloads;

/// <summary>The payload persistence chokepoint: a payload's script is the stored artifact, so credentials must never
/// reach the event store. Scrubs first, then hashes the scrubbed bytes (stored script and hash always agree); at save
/// time no run secret scope is open, so only the credential-param rule applies — the exact-secret rule is inert.</summary>
internal static class PayloadScript
{
    /// <summary>Scrubs a submitted payload at the persistence boundary and hashes the scrubbed bytes.</summary>
    /// <param name="submitted">The submitted payload document (a JSON object, guaranteed by the boundary validator).</param>
    /// <returns>The scrubbed script text and its SHA-256 (lowercase hex).</returns>
    public static ScrubbedScript Scrub(JsonElement submitted, CredentialScrubber scrubber)
    {
        // Scrubbing redacts only credential-param values inside string leaves and never inserts JSON-structural
        // characters, so the result is always a valid JSON document with the same shape.
        var script = scrubber.Scrub(submitted.GetRawText());
        var scriptHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(script)));
        return new ScrubbedScript(script, scriptHash);
    }
}

/// <summary>A payload script after scrubbing: the stored text and the hash of exactly those bytes.</summary>
/// <param name="ScriptHash">SHA-256 (lowercase hex) of <see cref="Script"/>.</param>
internal sealed record ScrubbedScript(string Script, string ScriptHash);
