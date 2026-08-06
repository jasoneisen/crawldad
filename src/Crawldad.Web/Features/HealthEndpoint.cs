using Crawldad.Contracts;
using Wolverine.Http;

namespace Crawldad.Web.Features;

/// <summary>
/// Liveness probe and the boot smoke test's target. It touches no storage, so a 200 from it proves the whole
/// host — Marten, Wolverine, and the Wolverine.Http endpoint pipeline — composed and started. Discovered by
/// <c>MapWolverineEndpoints</c>' assembly scan; no explicit registration needed.
/// </summary>
public static class HealthEndpoint
{
    [WolverineGet("/health")]
    public static HealthStatus Get() => new("ok");
}
