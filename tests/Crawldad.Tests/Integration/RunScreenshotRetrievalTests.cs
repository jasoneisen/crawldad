using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Runs;
using Crawldad.Web.Infrastructure.Storage;
using Marten;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary><c>GET /runs/{id}/screenshots/{reference}</c>: the PNG read-back for an authored <c>screenshot</c> capture or a
/// screenshot-on-failure. Proves the round-trip, the run-association authorization (a guessed/foreign ref 404s), the
/// still-running fetch, the caching contract, and the retention-expiry 404 — all against the in-memory blob store.</summary>
[Collection(DurableCollection.Name)]
public class RunScreenshotRetrievalTests(DurableFixture fixture)
{
    // A tiny run that navigates then captures an explicit screenshot — the trace records a Screenshotted ref, the blob store
    // holds the PNG.
    private const string _shotPayload =
        """
        { "crawldad": "1", "name": "shot.retrieve", "config": { "backend": "input.backend" }, "vars": {},
          "steps": [
            { "goto": { "url": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx?module=Enforcement&TabName=Enforcement" } },
            { "screenshot": { "name": "after-load" } }
          ],
          "result": "'ok'" }
        """;

    private static JsonObject ShotBody() => new()
    {
        ["payload"] = JsonNode.Parse(_shotPayload),
        ["inputs"] = new JsonObject { ["backend"] = new JsonObject { ["adapter"] = "fake", ["options"] = new JsonObject { ["fixture"] = "caphome-multipage" } } },
        ["async"] = true,
    };

    [Fact]
    public async Task Round_trips_a_saved_capture_as_image_png_with_content_addressed_caching()
    {
        var host = await fixture.EnsureAsync();
        var storedRef = await RunAndCaptureAsync(host);
        var store = (InMemoryScreenshotStore)host.Services.GetRequiredService<IScreenshotStore>();

        using var response = await GetScreenshotAsync(host, storedRef.RunId, storedRef.Tail, TestTenants.PrimaryKey);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(store.Blobs[storedRef.Ref]); // the exact stored bytes, never re-encoded

        // Content-addressed ⇒ immutable bytes for the ref: a private (tenant-scoped), long-lived, ETag-revalidated cache.
        response.Headers.ETag!.Tag.ShouldBe($"\"{storedRef.Digest}\"");
        response.Headers.CacheControl!.Private.ShouldBeTrue();
        response.Headers.CacheControl.MaxAge.ShouldBe(TimeSpan.FromHours(1));
        response.Headers.CacheControl.ToString().ShouldContain("immutable");
    }

    [Fact]
    public async Task A_matching_if_none_match_revalidates_to_304()
    {
        var host = await fixture.EnsureAsync();
        var storedRef = await RunAndCaptureAsync(host);

        using var response = await GetScreenshotAsync(
            host, storedRef.RunId, storedRef.Tail, TestTenants.PrimaryKey,
            ifNoneMatch: new EntityTagHeaderValue($"\"{storedRef.Digest}\""));

        response.StatusCode.ShouldBe(HttpStatusCode.NotModified); // the client's cached copy is still current
    }

    [Fact]
    public async Task A_still_running_capture_is_fetchable_mid_run()
    {
        var host = await fixture.EnsureAsync();

        // The interpreter saves the blob BEFORE it appends the Screenshotted event, so a ref visible in a non-terminal
        // trace always has its blob stored — reproduced here by seeding a run with no terminal event.
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 };
        var (runId, tail) = await SeedRunAsync(host, png, blobPresent: true);

        using var response = await GetScreenshotAsync(host, runId, tail, TestTenants.PrimaryKey);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(png);
    }

    [Fact]
    public async Task An_expired_blob_is_404_with_a_retention_hint()
    {
        var host = await fixture.EnsureAsync();

        // The ref is in the immutable trace, but its blob is gone (the retention janitor deletes screenshots past their TTL).
        var (runId, tail) = await SeedRunAsync(host, png: null, blobPresent: false);

        using var response = await GetScreenshotAsync(host, runId, tail, TestTenants.PrimaryKey);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).ShouldContain("retention");
    }

    [Fact]
    public async Task A_valid_ref_not_recorded_in_the_run_is_404()
    {
        var host = await fixture.EnsureAsync();
        var storedRef = await RunAndCaptureAsync(host); // the run really captured one — the sweep below is not vacuous

        // A well-formed ref the run never recorded: the run association is the authorization, so even a blob that exists
        // under the tenant is unreachable through a run that does not reference it.
        using var response = await GetScreenshotAsync(host, storedRef.RunId, $"{new string('b', 64)}.png", TestTenants.PrimaryKey);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_unknown_run_is_404()
    {
        var host = await fixture.EnsureAsync();
        using var response = await GetScreenshotAsync(host, Guid.NewGuid(), $"{new string('c', 64)}.png", TestTenants.PrimaryKey);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_malformed_ref_is_404()
    {
        var host = await fixture.EnsureAsync();
        var storedRef = await RunAndCaptureAsync(host);
        using var response = await GetScreenshotAsync(host, storedRef.RunId, "not-a-screenshot-ref", TestTenants.PrimaryKey);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ----- helpers -----------------------------------------------------

    // Runs the screenshot payload to completion and returns the captured ref (its wire form + the URL tail + digest + run id).
    private async Task<CapturedRef> RunAndCaptureAsync(IAlbaHost host)
    {
        fixture.Gate.Arm(gate: null);
        var accepted = await host.Scenario(x =>
        {
            x.Post.Json(ShotBody()).ToUrl("/runs");
            x.StatusCodeShouldBe(202);
        });
        var runId = (await accepted.ReadAsJsonAsync<JsonElement>()).GetProperty("runId").GetGuid();
        (await DurableHost.PollUntilTerminalAsync(host, runId, TimeSpan.FromSeconds(20))).GetProperty("status").GetString().ShouldBe("succeeded");

        var timeline = await host.Scenario(x =>
        {
            x.Get.Url($"/runs/{runId}/timeline");
            x.StatusCodeShouldBe(200);
        });
        var shot = (await timeline.ReadAsJsonAsync<JsonElement>()).GetProperty("screenshots").EnumerateArray().Single();
        var storedRef = shot.GetProperty("screenshotRef").GetString()!;
        return new CapturedRef(runId, storedRef);
    }

    // Seeds a non-terminal run stream carrying a Screenshotted ref, optionally storing the blob first (as the interpreter does).
    private static async Task<(Guid RunId, string Tail)> SeedRunAsync(IAlbaHost host, byte[]? png, bool blobPresent)
    {
        var reference = blobPresent
            ? await host.Services.GetRequiredService<IScreenshotStore>().SaveAsync(TestTenants.PrimaryId, png!, CancellationToken.None)
            : $"screenshots/{new string('d', 64)}.png"; // a well-formed ref whose blob was never stored (or was reaped)

        var runId = Guid.NewGuid();
        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession(TestTenants.PrimaryId))
        {
            session.Events.StartStream(
                runId,
                new RunStarted("shot.seed", "hash", DateTimeOffset.UtcNow, ["backend"], null, null),
                new Screenshotted(reference, "live", png?.Length ?? 0, DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(CancellationToken.None);
        }

        return (runId, reference["screenshots/".Length..]);
    }


    private static async Task<HttpResponseMessage> GetScreenshotAsync(
        IAlbaHost host, Guid runId, string tail, string apiKey, EntityTagHeaderValue? ifNoneMatch = null)
    {
        var client = host.GetTestServer().CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", TestTenants.Bearer(apiKey));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/runs/{runId}/screenshots/{tail}", UriKind.Relative));
        if (ifNoneMatch is not null)
        {
            request.Headers.IfNoneMatch.Add(ifNoneMatch);
        }

        var response = await client.SendAsync(request);
        client.Dispose();
        return response;
    }

    private sealed record CapturedRef(Guid RunId, string Ref)
    {
        public string Tail => Ref["screenshots/".Length..];

        public string Digest => Tail[..^".png".Length];
    }
}
