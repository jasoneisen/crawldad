using System.Text.Json;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Fixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace Crawldad.Api.Features.Fixtures;

/// <summary><c>GET /fixtures/{name}</c>: the set summary plus the recorded manifest — the initial state, each state's
/// URL and content-hash, and the transition graph — so a tenant can inspect exactly what coverage a replay has. Page
/// HTML is referenced only by hash, never surfaced. <c>404</c> when the tenant has no such set (a cross-tenant name is
/// simply absent, no existence oracle).</summary>
public static class GetFixtureEndpoint
{
    [WolverineGet("/fixtures/{name}")]
    public static async Task<IResult> Handle(
        string name,
        [FromServices] IFixtureStore store,
        [FromServices] TenantContext tenant,
        CancellationToken ct)
    {
        var set = await store.LoadAsync(tenant.TenantId, name, ct);
        if (set is null)
        {
            return Results.NotFound();
        }

        using var manifest = JsonDocument.Parse(set.ManifestJson);
        var summary = new FixtureSummary(set.Id, set.PageCount, set.TransitionCount, set.TotalBytes, set.RunId, set.CreatedAt);
        return Results.Ok(new FixtureDetailResponse(summary, manifest.RootElement.Clone()));
    }
}
