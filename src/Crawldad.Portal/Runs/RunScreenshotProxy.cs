using Crawldad.Client;
using Crawldad.Portal.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Crawldad.Portal.Runs;

/// <summary>The authenticated portal proxy for a run's screenshots. The run-detail page renders screenshots with plain
/// <c>&lt;img&gt;</c> tags, but the browser holds no Crawldad API key — so it points at this same-origin, cookie-gated
/// endpoint, which resolves the signed-in user's tenant and streams the PNG through their per-request
/// <see cref="CrawldadClient"/>. The API key never reaches the browser, and the bytes are marked <c>no-store</c> so a
/// screenshot's page content is never retained by a shared cache.</summary>
internal static class RunScreenshotProxy
{
    /// <summary>Maps <c>GET /app/runs/{runId}/screenshots/{reference}</c>, auth-gated by the cookie handler.</summary>
    internal static void MapPortalRunScreenshots(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/app/runs/{runId:guid}/screenshots/{reference}", HandleAsync)
            .RequireAuthorization();

    /// <summary>Streams the screenshot <paramref name="reference"/> for <paramref name="runId"/> to the browser via the
    /// current user's tenant client. An unlinked user (or an unknown/foreign/expired ref) is a <c>404</c> — never a
    /// leak of another tenant's capture and never a <c>500</c>.</summary>
    internal static async Task<IResult> HandleAsync(
        Guid runId, string reference, IPortalTenantContext tenant, HttpContext http, CancellationToken ct)
    {
        var resolved = await tenant.TryResolveAsync(ct);
        if (resolved is null)
        {
            return Results.NotFound(); // authenticated but not linked to a tenant (the route already gates anonymous)
        }

        try
        {
            var screenshot = await resolved.Client.GetRunScreenshotAsync(runId, reference, ct);
            // Screenshots can show sensitive page content; the page is auth-gated, so keep the bytes out of any cache.
            http.Response.Headers.CacheControl = "private, no-store";
            return Results.Bytes(screenshot.Content, screenshot.ContentType);
        }
        catch (CrawldadNotFoundException)
        {
            return Results.NotFound(); // unknown/foreign run or ref, or the screenshot aged out past retention
        }
    }
}
