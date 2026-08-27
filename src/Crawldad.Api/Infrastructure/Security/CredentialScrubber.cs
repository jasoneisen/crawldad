using System.Text.Json;
using System.Text.RegularExpressions;

namespace Crawldad.Api.Infrastructure.Security;

/// <summary>The credential-scrubbing primitive every outbound sink (events, HTTP responses, logs) funnels text through:
/// redacts exact registered run secrets, the always-on configured tenant API keys, and — on leak-prone channels (logs,
/// trace/timeline events, errors) — known credential query params (<c>apiKey</c>, <c>token</c>, <c>signingKey</c>). The
/// JSON channel (<see cref="ScrubJson"/>) — a run's shaped <c>result</c>/<c>partial</c> and the durable resume checkpoint
/// it restores through — exact-scrubs secrets but skips the param rule, so a customer's own extracted page content is
/// never corrupted (issues #70, #82). Idempotent; a no-op on already-clean text keeps goldens byte-identical.</summary>
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

    /// <summary>Scrubs credential material from <paramref name="text"/> for a leak-prone channel (a log message, a
    /// trace/timeline event field, an error message): exact-scrubs the run's live secrets AND applies the known
    /// credential-param rule. Returns the same string when nothing matched.</summary>
    /// <param name="text">The outbound text (a log message, an event/response field).</param>
    /// <returns>The text with live secrets and known credential-param values redacted.</returns>
    public string Scrub(string text) => Scrub(text, scrubCredentialParams: true);

    // The shared scrub core. The exact-secret rules (registered run secrets, form-fill secrets, the always-on tenant
    // keys) ALWAYS run, so a registered credential is redacted on every channel. The credential-param regex runs only
    // when scrubCredentialParams is set: every leak-prone channel (logs, trace/timeline events, errors) passes true, but
    // the JSON channels (ScrubJson — the run's RESULT and the durable resume checkpoint) pass false — the param rule would
    // rewrite a `token=`/`apiKey=`-shaped substring in the customer's OWN extracted page content to `[redacted]`, silently
    // corrupting the data they asked for (issue #70) and, on a checkpoint, the state a resumed run restores from (issue #82).
    // Their content is theirs to receive verbatim; only their run's own registered secrets stay redacted (the exact rules
    // above). This mirrors the capture channel, which bypasses the param rule for the same reason (PR #78).
    private string Scrub(string text, bool scrubCredentialParams)
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
        // query param, so scrubbing the param here is defence-in-depth on top of exact-secret redaction — but ONLY on a
        // leak-prone channel. The result channel opts out (see ScrubJson) so it never corrupts caller-extracted content.
        return scrubCredentialParams
            ? CredentialParamRegex().Replace(result, static match => match.Groups["name"].Value + "=" + Redaction)
            : result;
    }

    /// <summary>Scrubs a JSON value — the payload's shaped <c>result</c>/<c>partial</c>, and the durable resume checkpoint's
    /// cursor + var snapshot (issue #82), both the customer's OWN extracted content that could echo a registered secret.
    /// Exact-scrubs the run's live secrets so a registered credential is never returned, but deliberately does NOT apply the
    /// credential-param rule: a <c>token=</c>/<c>apiKey=</c>-shaped substring in the scraped page (a WebForms href, a hidden
    /// field) — or in a checkpointed var/cursor a resumed run restores and re-navigates — is the caller's data and survives
    /// verbatim, never rewritten to <c>[redacted]</c> (issue #70; the capture channel bypasses the param rule the same way,
    /// PR #78). Returns the same element when scrubbing changes nothing, so a credential-free result is byte-identical to its golden.</summary>
    public JsonElement? ScrubJson(JsonElement? element)
    {
        if (element is null)
        {
            return null;
        }

        var raw = element.Value.GetRawText();
        var scrubbed = Scrub(raw, scrubCredentialParams: false);
        if (string.Equals(scrubbed, raw, StringComparison.Ordinal))
        {
            return element;
        }

        using var document = JsonDocument.Parse(scrubbed);
        return document.RootElement.Clone();
    }
}
