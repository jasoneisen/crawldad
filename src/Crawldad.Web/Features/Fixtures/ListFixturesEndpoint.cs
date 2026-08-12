using Crawldad.Contracts.Fixtures;
using Crawldad.Web.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace Crawldad.Web.Features.Fixtures;

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
