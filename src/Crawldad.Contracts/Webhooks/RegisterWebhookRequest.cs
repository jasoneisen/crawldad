namespace Crawldad.Contracts.Webhooks;

/// <summary>The <c>PUT /webhooks/{name}</c> body: register (or replace) a tenant's webhook endpoint. The <c>name</c> is
/// the route key (a slug). <see cref="Secret"/> is the per-endpoint HMAC signing secret the receiver verifies deliveries
/// with — <b>write-only</b>: encrypted at rest, never echoed in any response, event, or log, and rotated by re-registering
/// with a new value. <see cref="Events"/> selects which terminal-run events are delivered; omit (or empty) to receive all.</summary>
/// <param name="Url">The delivery target — an absolute <c>https://</c> URL that must not resolve to a loopback, link-local,
/// or private (RFC 1918 / unique-local) address (SSRF guard). Validated at registration.</param>
/// <param name="Secret">The signing secret (caller-supplied). Used to compute the <c>X-Crawldad-Signature</c> HMAC over each
/// delivery. Write-only: never returned by any endpoint. Minimum length is enforced by the request validator.</param>
/// <param name="Events">The subscribed event types (a subset of <c>run.succeeded</c> / <c>run.failed</c> / <c>run.cancelled</c>).
/// Null or empty subscribes to <b>all</b> terminal-run events.</param>
public sealed record RegisterWebhookRequest(
    string Url,
    string Secret,
    IReadOnlyList<string>? Events = null)
{
    /// <summary>Redacts <see cref="Secret"/> from the record's string form so an accidental log of the request never
    /// carries the signing secret (the compiler-generated <c>ToString</c> would otherwise print every property).</summary>
    public override string ToString() =>
        $"RegisterWebhookRequest {{ Url = {Url}, Events = {(Events is null ? "all" : string.Join(",", Events))}, Secret = [redacted] }}";
}
