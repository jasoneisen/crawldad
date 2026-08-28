namespace Crawldad.Api.Infrastructure.Security;

/// <summary>The single, reviewable definition of the console <b>Owner-only</b> scope (issue #119 PR6): the
/// <c>(method, route)</c> pairs that, on the console channel, require the <see cref="Crawldad.Contracts.Tenancy.MembershipRole.Owner"/>
/// role — key management (mint/rotate/revoke) and membership management (add/remove/change-role). These are a strict
/// <b>subset</b> of <see cref="ConsoleWriteEndpoints"/> (they are still console writes — audited and rate-limited); the
/// difference is only the authorization policy <see cref="Crawldad.Api.HostConfiguration"/> applies:
/// <see cref="ConsoleAuthModule.ConsoleOwnerOrKeyPolicy"/> here, <see cref="ConsoleAuthModule.ConsoleOrKeyPolicy"/> for the
/// Member-reachable rest. Every operational write (replay, payload save/revise, webhook register/delete, billing sessions)
/// stays Member-reachable and is deliberately absent. An API-key caller is unaffected either way (key possession is full
/// authority); the enumeration test pins this set from live policy metadata so a mis-scoped route fails CI.</summary>
public static class ConsoleOwnerEndpoints
{
    /// <summary>The <c>(METHOD, route RawText)</c> pairs whose console write requires the Owner role. Verb-specific, and a
    /// strict subset of <see cref="ConsoleWriteEndpoints.Routes"/>.</summary>
    public static readonly IReadOnlySet<(string Method, string Route)> Routes = new HashSet<(string, string)>()
    {
        ("POST", "/tenant/keys"),                        // mint a tenant API key
        ("POST", "/tenant/keys/{id}/rotate"),            // rotate a tenant API key
        ("DELETE", "/tenant/keys/{id}"),                 // revoke a tenant API key
        ("POST", "/tenant/memberships"),                 // add a member (or record the attach self-owner)
        ("DELETE", "/tenant/memberships/{id}"),          // remove a member
        ("POST", "/tenant/memberships/{id}/role"),       // change a member's role
    };

    /// <summary>True when an endpoint for HTTP <paramref name="methods"/> on <paramref name="route"/> is an Owner-only
    /// console endpoint — one of the <see cref="Routes"/> <c>(method, route)</c> pairs.</summary>
    /// <param name="methods">The endpoint's HTTP methods (a Wolverine chain carries exactly one verb).</param>
    /// <param name="route">The endpoint's route pattern (RawText), or null.</param>
    public static bool Includes(IEnumerable<string> methods, string? route)
    {
        ArgumentNullException.ThrowIfNull(methods);
        return route is not null
            && methods.Any(method => Routes.Contains((method.ToUpperInvariant(), route)));
    }
}
