using System.Text.Json;
using System.Text.RegularExpressions;

namespace Crawldad.Web.Infrastructure.Security;

/// <summary>
/// The single credential-scrubbing primitive (§12, WP3). Every sink — Marten events, the HTTP response, the ILogger
/// pipeline — funnels its outbound text through <see cref="Scrub"/> so credentials never land in an event, a projection
/// row, a log line, or a response body. Two complementary rules:
/// <list type="number">
///   <item><b>Exact secret</b> — any credential resolved for the <em>current run</em> (registered into
///   <see cref="IRunSecretScope"/> by the connecting adapter, or by a CD-6 <c>fill.secret</c> at action time) is replaced
///   wherever it appears, catching free-form text a query-param rule cannot recognise (a <c>log</c> message echoing an
///   input, an exception, a scraped page, a form value read back after a fill). Connect credentials use
///   <see cref="MinExactScrubLength"/>; a form-fill secret — user-chosen and possibly short — uses the lower
///   <see cref="MinFormSecretScrubLength"/>.</item>
///   <item><b>Known credential params</b> — the values of <c>apiKey</c>, <c>token</c>, and <c>signingKey</c>
///   (case-insensitive) are redacted anywhere they appear as <c>name=value</c>, which covers a <c>ws://</c>/<c>wss://</c>
///   connect URL's query (Browserless <c>?token=…</c>; the live Browserbase <c>?signingKey=…</c> per-session JWT — the
///   returned URL no longer embeds the account apiKey, re-verified live 2026-08-08, §3.5) and a JSON-embedded connect URL
///   alike. The <c>apiKey</c> param is retained for an apiKey-bearing connectUrl (connectUrl mode / pre-drift). Surrounding
///   text (scheme, host, path) is preserved so the redaction stays diagnostic.</item>
/// </list>
/// <para>
/// The transform is <b>idempotent</b> (scrubbing already-scrubbed text is a no-op) and — critically for the acceptance
/// corpus — a <b>no-op on ordinary text</b>: the word "token" without a <c>=value</c> is untouched, and text that
/// contains no credential param and no live secret is returned unchanged (same instance), so goldens are byte-identical.
/// </para>
/// </summary>
/// <para>
/// Alongside the per-run secrets, a fixed set of <b>always-on</b> secrets is exact-scrubbed on every call: the configured
/// tenant API keys (CD-1). An API key is not run data and so never reaches a sink by design, but wiring it in closes the
/// residual vector (a stray log of an <c>Authorization</c> header) the same way run credentials are handled — defence in
/// depth, not a substitute for never emitting it.
/// </para>
/// </summary>
/// <param name="secretScope">The per-run secret registry consulted for the exact-match rule.</param>
/// <param name="alwaysScrub">Process-wide secrets (the configured tenant API keys) exact-scrubbed on every call.</param>
public sealed partial class CredentialScrubber(IRunSecretScope secretScope, IReadOnlyCollection<string>? alwaysScrub = null)
{
    private readonly IReadOnlyCollection<string> _alwaysScrub = alwaysScrub ?? [];

    /// <summary>The fixed marker a redacted value is replaced with.</summary>
    public const string Redaction = "[redacted]";

    /// <summary>
    /// A registered <b>connect</b> credential (and the always-on tenant keys) is exact-scrubbed only when at least this
    /// long. Real connect credentials (tokens, API keys, connect URLs) are far longer; the floor stops a pathologically
    /// short "secret" from mangling every occurrence of a common substring. The query-param rule still redacts
    /// <c>token=x</c> regardless of length.
    /// </summary>
    internal const int MinExactScrubLength = 8;

    /// <summary>
    /// A registered <b>form-fill</b> secret (a CD-6 <c>fill.secret</c>) is exact-scrubbed at this much lower floor: a form
    /// credential is user-chosen and may be short (a 4-digit PIN, a short password), and its whole purpose is protection,
    /// so it is redacted even when a connect credential of the same length would not be. The floor of 4 still avoids the
    /// worst over-redaction (a 1–3 character "secret" — pathological, not a real credential — would redact common short
    /// substrings and is a documented limitation; deliberately over-redacting the user's own short secret in their own
    /// output is a strictly safer failure than leaking it).
    /// </summary>
    internal const int MinFormSecretScrubLength = 4;

    // name=value for the three known credential params, case-insensitive. The value runs until a delimiter that ends a
    // query param / token in prose — whitespace, '&', '#', quotes, or a bracket — and excludes '[' ']' so the redaction
    // marker itself is inert (double-scrub stays stable). One or more value chars are required, so a bare "token=" (no
    // value) and the word "token" alone are left untouched (no false positives). ExplicitCapture: only the param name is
    // captured (the value group is unneeded); the match timeout bounds this against pathological input.
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

        // Form-fill secrets (CD-6 fill.secret): redacted at the lower form floor so a short user-chosen credential (a PIN,
        // a short password) read back from the page and echoed into free-form text is still caught by exact match.
        foreach (var secret in secretScope.FormSecrets)
        {
            if (secret.Length >= MinFormSecretScrubLength)
            {
                result = result.Replace(secret, Redaction, StringComparison.Ordinal);
            }
        }

        // Always-on secrets (the configured tenant API keys, CD-1): exact-scrubbed on every call, above the length floor
        // (a configured key is required to be well above it), so a leaked key is redacted wherever it might surface.
        foreach (var secret in _alwaysScrub)
        {
            if (secret.Length >= MinExactScrubLength)
            {
                result = result.Replace(secret, Redaction, StringComparison.Ordinal);
            }
        }

        return CredentialParamRegex().Replace(result, static match => match.Groups["name"].Value + "=" + Redaction);
    }

    /// <summary>
    /// Scrubs a JSON value (the payload's shaped <c>result</c>, whose caller data could echo a secret, §12). Returns the
    /// <b>same element</b> when scrubbing changes nothing, so a credential-free result is byte-identical to its golden.
    /// </summary>
    /// <param name="element">The result element, or null when the run failed.</param>
    /// <returns>The scrubbed element, the original element when unchanged, or null.</returns>
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
