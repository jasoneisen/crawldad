using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Fixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace Crawldad.Api.Features.Fixtures;

/// <summary><c>GET /fixtures</c>: list the authenticated tenant's recorded fixture sets — name, page/transition counts,
/// byte size, source run, and when. Never the page HTML: the store reads only this tenant's partition, so a listing can
/// never surface another tenant's sets.</summary>
public static class ListFixturesEndpoint
{
    [WolverineGet("/fixtures")]
    public static async Task<IResult> Handle(
        [FromServices] IFixtureStore store,
        [FromServices] TenantContext tenant,
        CancellationToken ct)
    {
        var fixtures = await store.ListAsync(tenant.TenantId, ct);
        return Results.Ok(new FixtureListResponse(fixtures));
    }
}
