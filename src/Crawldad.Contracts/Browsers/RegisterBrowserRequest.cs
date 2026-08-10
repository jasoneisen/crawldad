namespace Crawldad.Contracts.Browsers;

/// <summary>The <c>PUT /browsers/{name}</c> body: register (or replace) a tenant's browser connect credential. The
/// <c>name</c> is the route key (it becomes the <c>credentialRef</c> payloads use); <see cref="Secret"/> is the wss/CDP
/// connect URL or the provider api key — encrypted at rest, never echoed in any response, event, or log.</summary>
/// <param name="Adapter">The backend adapter this credential is for (<c>browserbase</c>/<c>browserless</c>).</param>
/// <param name="Mode">How the secret is used: <c>connectUrl</c> (the secret is the whole connect URL) or <c>apiKey</c>.</param>
/// <param name="Secret">The credential value (a connect URL or an api key). Write-only: never returned by any endpoint.</param>
/// <param name="Options">Optional provider options metadata (e.g. region, projectId), surfaced in listings, never the secret.</param>
public sealed record RegisterBrowserRequest(
    string Adapter,
    string Mode,
    string Secret,
    IReadOnlyDictionary<string, string>? Options = null)
{
    /// <summary>Redacts <see cref="Secret"/> from the record's string form so an accidental log of the request never
    /// carries credential material (the compiler-generated <c>ToString</c> would otherwise print every property).</summary>
    public override string ToString() =>
        $"RegisterBrowserRequest {{ Adapter = {Adapter}, Mode = {Mode}, Secret = [redacted] }}";
}
