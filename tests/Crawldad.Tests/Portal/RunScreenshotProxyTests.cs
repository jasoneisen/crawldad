using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Crawldad.Portal.Runs;
using Crawldad.Tests.Client;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Crawldad.Tests.Portal;

/// <summary>Unit tests for the portal screenshot proxy handler: a linked tenant streams the PNG through their client
/// (with the bytes marked no-store), while an unlinked user and an unknown/expired ref are both a clean 404 — never a
/// leak of another tenant's capture and never a 500.</summary>
public class RunScreenshotProxyTests
{
    private static readonly Guid _runId = new("7b3ad9f2-1c4e-4a08-9f21-2c9e5d1a4f60");

    private static async Task<(int Status, string? ContentType, byte[] Body, string? CacheControl)> ExecuteAsync(IResult result, HttpContext http)
    {
        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        using var buffer = new MemoryStream();
        await http.Response.Body.CopyToAsync(buffer);
        return (http.Response.StatusCode, http.Response.ContentType, buffer.ToArray(), http.Response.Headers.CacheControl);
    }

    private static DefaultHttpContext NewHttpContext() => new()
    {
        Response = { Body = new MemoryStream() },
        RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
    };

    [Fact]
    public async Task Unlinked_user_is_a_not_found()
    {
        var context = new FakePortalTenantContext(tenant: null);
        var http = NewHttpContext();

        var result = await RunScreenshotProxy.HandleAsync(_runId, "abc.png", context, http, CancellationToken.None);
        var (status, _, _, _) = await ExecuteAsync(result, http);

        status.ShouldBe(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task Linked_user_streams_the_png_with_no_store()
    {
        var png = Encoding.UTF8.GetBytes("PNGBYTES");
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return response;
        });
        var context = new FakePortalTenantContext(PortalRunsTestSupport.TenantOver(handler));
        var http = NewHttpContext();

        var result = await RunScreenshotProxy.HandleAsync(_runId, "9f8c21.png", context, http, CancellationToken.None);
        var (status, contentType, body, cacheControl) = await ExecuteAsync(result, http);

        status.ShouldBe(StatusCodes.Status200OK);
        contentType.ShouldBe("image/png");
        body.ShouldBe(png);
        cacheControl.ShouldBe("private, no-store"); // sensitive page content is never cached
        handler.Last.Path.ShouldBe($"/runs/{_runId}/screenshots/9f8c21.png");
        handler.Last.Authorization.ShouldBe("Bearer test-key-abcdef"); // streamed with the tenant's key, never the browser's
    }

    [Fact]
    public async Task Unknown_or_expired_ref_is_a_not_found()
    {
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Text(HttpStatusCode.NotFound, "expired"));
        var context = new FakePortalTenantContext(PortalRunsTestSupport.TenantOver(handler));
        var http = NewHttpContext();

        var result = await RunScreenshotProxy.HandleAsync(_runId, "gone.png", context, http, CancellationToken.None);
        var (status, _, _, _) = await ExecuteAsync(result, http);

        status.ShouldBe(StatusCodes.Status404NotFound);
    }
}
