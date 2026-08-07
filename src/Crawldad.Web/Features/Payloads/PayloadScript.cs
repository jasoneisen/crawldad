using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Crawldad.Web.Infrastructure.Security;

namespace Crawldad.Web.Features.Payloads;

/// <summary>
/// The payload persistence chokepoint (§12), the analogue of <c>RunEventScrubber</c> for the Payloads slice. A payload's
/// script <em>is</em> the stored artifact, so it must never carry a credential into the immutable event store. The
/// scrubbing decision, applied once here by draft and revise:
/// <list type="number">
///   <item><b>Scrub, then hash the scrubbed bytes.</b> The script is run through the shared
///   <see cref="CredentialScrubber"/> first, and the <c>scriptHash</c> is computed over the <em>scrubbed</em> bytes — so
///   the stored script and its hash always agree, and drift (pinned hash vs head hash) compares stored artifacts.</item>
///   <item><b>Validate exactly what is stored.</b> The scrubbed script is re-parsed and returned so the caller validates,
///   name-extracts from, and persists one and the same artifact (a persisted revision is always executable, §12).</item>
/// </list>
/// For a well-formed payload — credentials are run-time inputs by reference, never embedded (§12) — scrubbing is a no-op,
/// so the stored script is byte-identical to what was submitted and fully executable; scrubbing only ever alters a
/// mis-authored payload that embedded a credential param, and redacting it is the safe outcome. At save time no run
/// secret scope is open, so only the credential-<em>param</em> rule (<c>apiKey=</c>/<c>token=</c>/<c>signingKey=</c>)
/// applies; the exact-secret rule is inert.
/// </summary>
internal static class PayloadScript
{
    /// <summary>Scrubs a submitted payload at the persistence boundary and hashes the scrubbed bytes.</summary>
    /// <param name="submitted">The submitted payload document (a JSON object, guaranteed by the boundary validator).</param>
    /// <param name="scrubber">The shared credential scrubber.</param>
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
/// <param name="Script">The scrubbed payload JSON.</param>
/// <param name="ScriptHash">SHA-256 (lowercase hex) of <see cref="Script"/>.</param>
internal sealed record ScrubbedScript(string Script, string ScriptHash);
