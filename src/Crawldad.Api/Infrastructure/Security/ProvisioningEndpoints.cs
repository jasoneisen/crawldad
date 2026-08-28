namespace Crawldad.Api.Infrastructure.Security;

/// <summary>The single, reviewable definition of the self-serve PROVISIONING scope (issue #119 PR7): the exact
/// <c>(method, route)</c> that the portal's first-party console identity may call <b>without any membership</b> — the one
/// console surface reachable before a tenant scope exists (a new user has no workspace yet). It is deliberately NOT part of
/// <see cref="ConsoleReadEndpoints"/> / <see cref="ConsoleWriteEndpoints"/>: those require a membership (a tenant claim), and
/// they also admit an API key; provisioning admits the <b>console scheme only</b> (a key caller is a plain 401). So it carries
/// its own <see cref="ConsoleAuthModule.ProvisioningPolicy"/>, and <see cref="Crawldad.Api.HostConfiguration"/> opts exactly
/// this route into it. The enumeration test derives the live provisioning-admitting set from endpoint metadata and asserts it
/// equals an independent hand-list, so a route added here without updating that list fails CI as scope creep.</summary>
public static class ProvisioningEndpoints
{
    /// <summary>The self-serve free-workspace provisioning route.</summary>
    public const string ProvisionRoute = "/provisioning/tenants";

    /// <summary>The <c>(METHOD, route RawText)</c> pairs an unauthenticated-by-membership console principal may call.</summary>
    public static readonly IReadOnlySet<(string Method, string Route)> Routes = new HashSet<(string, string)>()
    {
        ("POST", ProvisionRoute), // create the caller's one free-tier workspace
    };

    /// <summary>True when an endpoint for HTTP <paramref name="methods"/> on <paramref name="route"/> is a provisioning
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
