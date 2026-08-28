namespace Crawldad.Api.Infrastructure.Security;

/// <summary>The single, reviewable definition of the console WRITE scope (issue #119 PR5): the exact
/// <c>(method, route)</c> pairs an authenticated console principal may <b>mutate</b> — the writes the portal dashboard
/// performs, and only those. It is the write half of the enumeration <see cref="ConsoleReadEndpoints"/> pins for reads;
/// <see cref="Crawldad.Api.HostConfiguration"/> opts exactly these into the same <see cref="ConsoleAuthModule.ConsoleOrKeyPolicy"/>
/// policy (so a console token is accepted here, and an API key still is too), and marks them for the console-write audit +
/// rate-limit middleware. Every other mutating route keeps the default <c>ApiKey</c>-only gate; a console token presented on
/// one is rejected. The enumeration test derives the live admitting-set from endpoint metadata and asserts it equals an
/// <b>independent</b> hand-listed intent (reads + writes as separate lists), so a route added here without updating that list
/// fails CI as scope creep. Matching is verb-specific: <c>POST /payloads</c> and <c>POST /tenant/keys</c> opt in while their
/// <c>GET</c> read halves are governed by <see cref="ConsoleReadEndpoints"/>, and <c>PUT</c>/<c>DELETE /webhooks/{name}</c>
/// each opt in independently of the <c>GET /webhooks</c> read.</summary>
public static class ConsoleWriteEndpoints
{
    /// <summary>The <c>(METHOD, route RawText)</c> pairs whose write endpoint accepts the console principal. Verb-specific,
    /// because a route's write half opts in while its read half is governed separately (or stays key-only).</summary>
    public static readonly IReadOnlySet<(string Method, string Route)> Routes = new HashSet<(string, string)>()
    {
        ("POST", "/runs/{id}/replay"),           // re-run a pinned historical run
        ("PUT", "/webhooks/{name}"),             // register / replace a webhook endpoint
        ("DELETE", "/webhooks/{name}"),          // unregister a webhook endpoint
        ("POST", "/payloads"),                   // draft a managed payload
        ("POST", "/payloads/{id}/revise"),       // append a payload revision
        ("POST", "/billing/checkout-session"),   // mint a hosted-checkout redirect
        ("POST", "/billing/portal-session"),     // mint a billing-portal redirect
        ("POST", "/tenant/keys"),                // mint a tenant API key
        ("POST", "/tenant/keys/{id}/rotate"),    // rotate a tenant API key
        ("DELETE", "/tenant/keys/{id}"),         // revoke a tenant API key
        ("POST", "/tenant/memberships"),         // record a workspace membership (the attach flow)
    };

    /// <summary>True when an endpoint for HTTP <paramref name="methods"/> on <paramref name="route"/> is a console-write
    /// endpoint — one of the <see cref="Routes"/> <c>(method, route)</c> pairs.</summary>
    /// <param name="methods">The endpoint's HTTP methods (a Wolverine chain carries exactly one verb).</param>
    /// <param name="route">The endpoint's route pattern (RawText), or null.</param>
    public static bool Includes(IEnumerable<string> methods, string? route)
    {
        ArgumentNullException.ThrowIfNull(methods);
        return route is not null
            && methods.Any(method => Routes.Contains((method.ToUpperInvariant(), route)));
    }
}
