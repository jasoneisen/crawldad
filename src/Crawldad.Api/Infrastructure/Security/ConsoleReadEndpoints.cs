namespace Crawldad.Api.Infrastructure.Security;

/// <summary>The single, reviewable definition of the console READ scope (issue #119 PR4): the exact <c>GET</c> routes an
/// authenticated console principal may read — the portal dashboard's reads. Every other endpoint keeps the default
/// <c>ApiKey</c>-only gate; a console token presented anywhere else is rejected. <see cref="Crawldad.Api.HostConfiguration"/>
/// opts exactly these into the <see cref="ConsoleAuthModule.ConsoleOrKeyPolicy"/> policy, and the enumeration test derives
/// the live admitting-set from endpoint metadata and asserts it equals an <b>independent</b> hand-listed intent — so a
/// route added here (or a policy leaking elsewhere) fails CI as scope creep. Matching is by <c>(GET, route)</c> so the
/// read half of a shared route (e.g. <c>GET /tenant/keys</c>) opts in while its write half (<c>POST /tenant/keys</c>)
/// stays key-only.</summary>
public static class ConsoleReadEndpoints
{
    /// <summary>The Wolverine route patterns (RawText, leading slash) whose <c>GET</c> endpoint accepts the console
    /// principal. This is the console scope — reads only; writes come in PR5.</summary>
    public static readonly IReadOnlySet<string> Routes = new HashSet<string>(StringComparer.Ordinal)
    {
        "/runs",                                // GET /runs — the dashboard runs list
        "/runs/{id}",                           // GET a run
        "/runs/{id}/timeline",                  // the run's timeline read model
        "/runs/{id}/drift",                     // the run's drift read
        "/runs/{id}/screenshots/{reference}",   // a run's screenshot
        "/payloads",                            // GET /payloads — the registry list
        "/payloads/{id}",                       // GET a payload
        "/payloads/{id}/revisions/{revision}",  // a pinned payload revision
        "/payloads/{id}/diff/{from}/{to}",      // a payload revision diff
        "/payloads/{id}/drift-status",          // a payload's drift status
        "/webhooks",                            // GET /webhooks — the registered endpoints
        "/webhooks/{name}/deliveries",          // a webhook's delivery history
        "/tenant",                              // GET /tenant — the tenant profile
        "/usage",                               // GET /usage — capacity + consumption
        "/billing/config",                      // GET /billing/config — the tier/pricing config
        "/tenant/keys",                         // GET /tenant/keys — the tenant's key list (its writes stay key-only)
    };

    /// <summary>True when an endpoint for HTTP <paramref name="methods"/> on <paramref name="route"/> is a console-read
    /// endpoint — a <c>GET</c> on one of <see cref="Routes"/>.</summary>
    /// <param name="methods">The endpoint's HTTP methods (a Wolverine chain carries exactly one verb).</param>
    /// <param name="route">The endpoint's route pattern (RawText), or null.</param>
    public static bool Includes(IEnumerable<string> methods, string? route)
    {
        ArgumentNullException.ThrowIfNull(methods);
        return route is not null
            && Routes.Contains(route)
            && methods.Any(method => string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase));
    }
}
