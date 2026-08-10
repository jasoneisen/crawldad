using Crawldad.Web.Infrastructure.Security;
using Crawldad.Web.Infrastructure.Storage;
using JasperFx.Events;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Wolverine.Http;

namespace Crawldad.Web.Features.Runs;

/// <summary><c>GET /runs/{id}/screenshots/{reference}</c>: streams a captured PNG (the <c>screenshot</c> node or a
/// screenshot-on-failure) back to the run's tenant. The ref must appear in <b>this</b> run's tenant-scoped trace — the
/// run association is the authorization, so a guessed/foreign ref is a <c>404</c>, exactly like an unknown run.</summary>
public static class RunScreenshotEndpoint
{
    /// <summary>The <c>404</c> body when the ref is authorized but its blob is gone — the retention janitor deletes
    /// screenshots (they can show PII) once past their category's TTL, while the immutable trace keeps the ref forever.</summary>
    internal const string ExpiredMessage =
        "screenshot no longer available; screenshots expire and are deleted once past the storage retention policy window";

    /// <summary>Handles <c>GET /runs/{id}/screenshots/{reference}</c>. The <c>{reference}</c> segment is the timeline's
    /// <c>screenshotRef</c> with its <c>screenshots/</c> prefix dropped (i.e. <c>{sha256}.png</c>).</summary>
    [WolverineGet("/runs/{id}/screenshots/{reference}")]
    public static async Task<IResult> Handle(
        Guid id, string reference, IQuerySession session, IScreenshotStore screenshots, TenantContext tenant, CancellationToken ct)
    {
        var screenshotRef = $"{BlobNaming.ScreenshotsDir}/{reference}";

        // Authorization: the ref must be well-formed AND recorded in this run's (tenant-scoped) trace. A stream in another
        // tenant — or an unknown run — fetches no events, so it fails the membership check exactly like a bad ref.
        var events = await session.Events.FetchStreamAsync(id, token: ct);
        if (!BlobNaming.TryParseScreenshotRef(screenshotRef, out var digest) || !RunScreenshots.AppearsIn(events, screenshotRef))
        {
            return Results.NotFound();
        }

        // Fetching mid-run is fine: the interpreter durably saves the blob BEFORE it appends the ref-bearing event, so a ref
        // visible in the trace always had its blob stored. A null here means retention has since deleted it.
        var png = await screenshots.OpenReadAsync(tenant.TenantId, screenshotRef, ct);
        return png is null ? Results.NotFound(ExpiredMessage) : new CachedPngResult(png, digest);
    }
}

/// <summary>The run-association authorization check, factored out of the endpoint: the ref is authorized iff a
/// <c>Screenshotted</c> (explicit capture) or <c>StepFailed</c> (failure capture) event in the run's trace carries it.</summary>
internal static class RunScreenshots
{
    /// <summary>Whether <paramref name="screenshotRef"/> appears on a screenshot-bearing event in <paramref name="events"/>.</summary>
    public static bool AppearsIn(IReadOnlyList<IEvent> events, string screenshotRef) =>
        events.Any(e => string.Equals(RefOf(e.Data), screenshotRef, StringComparison.Ordinal));

    private static string? RefOf(object data) => data switch
    {
        Screenshotted shot => shot.ScreenshotRef,
        StepFailed failed => failed.ScreenshotRef,
        _ => null,
    };
}

/// <summary>Streams a content-addressed screenshot: because a ref's bytes never change, it is cacheable — privately (the
/// content is tenant-scoped and auth-gated, so never a shared cache) and revalidated by the digest <c>ETag</c>. Delegates
/// the body write (Content-Type, conditional <c>304</c>, stream disposal) to the framework file result.</summary>
internal sealed class CachedPngResult(Stream png, string digest) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.Headers.CacheControl = "private, max-age=3600, immutable";
        return Results.File(png, ContentTypes.Png, lastModified: null, entityTag: new EntityTagHeaderValue($"\"{digest}\"")).ExecuteAsync(httpContext);
    }
}
