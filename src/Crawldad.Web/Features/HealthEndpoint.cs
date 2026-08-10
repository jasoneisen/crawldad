using Crawldad.Contracts;
using Microsoft.AspNetCore.Authorization;
using Wolverine.Http;

namespace Crawldad.Web.Features;

/// <summary>Liveness probe and the boot smoke test's target: it touches no storage, so a 200 proves the whole host —
/// Marten, Wolverine, and the Wolverine.Http pipeline — composed and started. Deliberately anonymous: a liveness probe
/// must answer an unauthenticated load balancer, so it opts out of the tenant gate via <see cref="AllowAnonymousAttribute"/>.</summary>
public static class HealthEndpoint
{
    [AllowAnonymous]
    [WolverineGet("/health")]
    public static HealthStatus Get() => new("ok");
}
