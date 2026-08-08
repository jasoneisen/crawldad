using Crawldad.Contracts;
using Microsoft.AspNetCore.Authorization;
using Wolverine.Http;

namespace Crawldad.Web.Features;

/// <summary>
/// Liveness probe and the boot smoke test's target. It touches no storage, so a 200 from it proves the whole
/// host — Marten, Wolverine, and the Wolverine.Http endpoint pipeline — composed and started. Discovered by
/// <c>MapWolverineEndpoints</c>' assembly scan; no explicit registration needed.
/// <para>
/// The one deliberately <b>anonymous</b> route (CD-1): a liveness probe must answer an unauthenticated load balancer, so it
/// opts out of the RequireAuthorizeOnAll tenant gate with <see cref="AllowAnonymousAttribute"/>. It exposes no tenant data
/// (a fixed <c>"ok"</c>), and the endpoint-enumeration test allowlists exactly this route while asserting every other
/// endpoint rejects an unauthenticated request.
/// </para>
/// </summary>
public static class HealthEndpoint
{
    [AllowAnonymous]
    [WolverineGet("/health")]
    public static HealthStatus Get() => new("ok");
}
