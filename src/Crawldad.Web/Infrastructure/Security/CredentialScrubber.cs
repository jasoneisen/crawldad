using System.Text.Json;
using System.Text.RegularExpressions;

namespace Crawldad.Web.Infrastructure.Security;

/// <summary>The credential-scrubbing primitive every outbound sink (events, HTTP responses, logs) funnels text through:
/// redacts exact registered run secrets, the always-on configured tenant API keys, and known credential query params
/// (<c>apiKey</c>, <c>token</c>, <c>signingKey</c>). Idempotent; a no-op on already-clean text keeps goldens byte-identical.</summary>
public sealed partial class CredentialScrubber(IRunSecretScope secretScope, IReadOnlyCollection<string>? alwaysScrub = null)
{
    private readonly IReadOnlyCollection<string> _alwaysScrub = alwaysScrub ?? [];

    /// <summary>The fixed marker a redacted value is replaced with.</summary>
    public const string Redaction = "[redacted]";

    /// <summary>A registered connect credential (and the always-on tenant keys) is exact-scrubbed only at or above this
    /// length: real credentials are far longer, and the floor stops a short "secret" from mangling common substrings.
    /// The query-param rule still redacts <c>token=x</c> regardless of length.</summary>
    internal const int MinExactScrubLength = 8;

    /// <summary>A registered form-fill secret (a <c>fill.secret</c>) is exact-scrubbed at this much lower floor: a form
    /// credential is user-chosen and may be short (a PIN, a short password), so it is redacted even when a connect
    /// credential of the same length would not be. Over-redacting a short secret is a safer failure than leaking it.</summary>
    internal const int MinFormSecretScrubLength = 4;

    // name=value for apiKey/token/signingKey, case-insensitive; excludes '[' ']' so the redaction marker itself stays
    // inert (double-scrub is stable). Requires one or more value chars, so a bare "token=" or the word "token" alone is
    // left untouched (no false positives). ExplicitCapture bounds the match; the timeout guards pathological input.
    [GeneratedRegex(
        @"\b(?<name>apiKey|token|signingKey)=([^\s&#""'<>()\[\]{},;]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex CredentialParamRegex();

    /// <summary>Scrubs credential material from <paramref name="text"/>. Returns the same string when nothing matched.</summary>
    /// <param name="text">The outbound text (a log message, an event/response field, a serialised result).</param>
    /// <returns>The text with live secrets and known credential-param values redacted.</returns>
    public string Scrub(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var result = text;
        foreach (var secret in secretScope.Current)
        {
            if (secret.Length >= MinExactScrubLength)
            {
                result = result.Replace(secret, Redaction, StringComparison.Ordinal);
            }
        }

        // Form-fill secrets (fill.secret): redacted at the lower form floor so a short user-chosen credential (a PIN,
        // a short password) read back from the page and echoed into free-form text is still caught by exact match.
        foreach (var secret in secretScope.FormSecrets)
        {
            if (secret.Length >= MinFormSecretScrubLength)
            {
                result = result.Replace(secret, Redaction, StringComparison.Ordinal);
            }
        }

        // Always-on secrets (the configured tenant API keys): exact-scrubbed on every call, above the length floor
        // (a configured key is required to be well above it), so a leaked key is redacted wherever it might surface.
        foreach (var secret in _alwaysScrub)
        {
            if (secret.Length >= MinExactScrubLength)
            {
                result = result.Replace(secret, Redaction, StringComparison.Ordinal);
            }
        }

        // Redact known credential query-param values (apiKey/token/signingKey): a connectUrl can embed the apiKey as a
        // query param, so scrubbing the param here is defence-in-depth on top of exact-secret redaction.
        return CredentialParamRegex().Replace(result, static match => match.Groups["name"].Value + "=" + Redaction);
    }

    /// <summary>Scrubs a JSON value (the payload's shaped <c>result</c>, whose caller data could echo a secret). Returns
    /// the same element when scrubbing changes nothing, so a credential-free result is byte-identical to its golden.</summary>
    public JsonElement? ScrubJson(JsonElement? element)
    {
        if (element is null)
        {
            return null;
        }

        var raw = element.Value.GetRawText();
        var scrubbed = Scrub(raw);
        if (string.Equals(scrubbed, raw, StringComparison.Ordinal))
        {
            return element;
        }

        using var document = JsonDocument.Parse(scrubbed);
        return document.RootElement.Clone();
    }
}
