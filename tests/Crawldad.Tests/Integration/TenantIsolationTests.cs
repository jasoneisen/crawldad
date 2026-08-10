using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Tests.Support;
using Crawldad.Web.Infrastructure.Browser;
using Crawldad.Web.Infrastructure.Browser.Fake;
using Crawldad.Web.Infrastructure.Storage;
using Marten;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>A fake backend that records every session it hands out, so a test can assert runs never share a browser session.</summary>
public sealed class SessionRecordingBackend(string fixturesRoot) : IBrowserBackend
{
    private readonly FakeBrowserBackend _inner = new(fixturesRoot);
    private readonly List<IBrowserSession> _sessions = [];

    /// <summary>Every session handed out, in order — distinct instances prove per-run (never shared) sessions.</summary>
    public IReadOnlyList<IBrowserSession> Sessions
    {
        get
        {
            lock (_sessions)
            {
                return [.. _sessions];
            }
        }
    }

    public async Task<IBrowserSession> ConnectAsync(BackendBinding binding, SessionPolicy policy, CancellationToken ct)
    {
        var session = await _inner.ConnectAsync(binding, policy, ct);
        lock (_sessions)
        {
            _sessions.Add(session);
        }

        return session;
    }
}

/// <summary>Builds one isolation host (over the session-recording fake backend) and seeds tenant A's + tenant B's data
/// once, so the cross-tenant assertions share a single host build rather than contending with other hosts under
/// parallelism. Tenant A gets a payload + a succeeded + a failing (screenshot) run; tenant B gets its own run too.</summary>
public sealed class TenantIsolationFixture : IAsyncLifetime
{
    public IAlbaHost Host { get; private set; } = null!;

    public SessionRecordingBackend Backend { get; private set; } = null!;

    public Guid PayloadA { get; private set; }

    public Guid SucceededRunA { get; private set; }

    public Guid FailedRunA { get; private set; }

    public async Task InitializeAsync()
    {
        Backend = new SessionRecordingBackend(Runner.FixturesRoot);
        Host = (await AlbaHost.For<Program>(builder =>
        {
            builder.UseCrawldadTestDefaults("crawldad_isolation");
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(new FakeClock());
                services.AddKeyedSingleton<IBrowserBackend>("fake", (_, _) => Backend);
            });
        })).AuthenticatedAsPrimaryTenant();
        await Host.ResetAllMartenDataAsync();

        PayloadA = await DraftAsync(TestTenants.PrimaryKey, DemoPayload);
        SucceededRunA = await RunToCompletionAsync(TestTenants.PrimaryKey, new JsonObject { ["payloadId"] = PayloadA, ["inputs"] = Inputs(), ["async"] = true }, "succeeded");
        FailedRunA = await RunToCompletionAsync(TestTenants.PrimaryKey, new JsonObject { ["payload"] = JsonNode.Parse(_failingPayload), ["inputs"] = Inputs(), ["async"] = true }, "failed");

        // Tenant B's own run, so B's data is non-empty and isolation is proven against populated state (not just "B sees nothing").
        await RunToCompletionAsync(TestTenants.SecondaryKey, new JsonObject { ["payload"] = JsonNode.Parse(DemoPayload), ["inputs"] = Inputs(), ["async"] = true }, "succeeded");
    }

    public Task DisposeAsync() => Host.DisposeAsync().AsTask();

    internal const string DemoPayload =
        """
        { "crawldad": "1", "name": "iso.demo", "config": { "backend": "input.backend" }, "vars": {},
          "steps": [ { "goto": { "url": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx?module=Enforcement&TabName=Enforcement" } } ],
          "result": "'ok'" }
        """;

    private const string _failingPayload =
        """
        { "crawldad": "1", "name": "iso.fail", "config": { "backend": "input.backend" }, "vars": {},
          "steps": [
            { "goto": { "url": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx?module=Enforcement&TabName=Enforcement" } },
            { "fail": { "class": "terminal", "code": "iso_boom", "message": "stop" } }
          ],
          "result": "'x'" }
        """;

    private static JsonObject Inputs() =>
        new() { ["backend"] = new JsonObject { ["adapter"] = "fake", ["options"] = new JsonObject { ["fixture"] = "caphome-multipage" } } };

    private async Task<Guid> DraftAsync(string apiKey, string payload)
    {
        var result = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(apiKey));
            x.Post.Json(new JsonObject { ["payload"] = JsonNode.Parse(payload) }).ToUrl("/payloads");
            x.StatusCodeShouldBe(200);
        });
        return (await result.ReadAsJsonAsync<JsonElement>()).GetProperty("payloadId").GetGuid();
    }

    private async Task<Guid> RunToCompletionAsync(string apiKey, JsonObject body, string expectedStatus)
    {
        var accepted = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(apiKey));
            x.Post.Json(body).ToUrl("/runs");
            x.StatusCodeShouldBe(202);
        });
        var runId = (await accepted.ReadAsJsonAsync<JsonElement>()).GetProperty("runId").GetGuid();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var state = await Host.Scenario(x =>
            {
                x.WithRequestHeader("Authorization", TestTenants.Bearer(apiKey));
                x.Get.Url($"/runs/{runId}");
                x.StatusCodeShouldBe(200);
            });
            var root = (await state.ReadAsJsonAsync<JsonElement>()).Clone();
            if (!string.Equals(root.GetProperty("status").GetString(), "running", StringComparison.Ordinal))
            {
                root.GetProperty("status").GetString().ShouldBe(expectedStatus);
                return runId;
            }

            await Task.Delay(40);
        }

        throw new TimeoutException($"run {runId} did not terminate");
    }
}

/// <summary>Serializes the cross-tenant isolation class onto its one shared host build.</summary>
[CollectionDefinition(Name)]
public sealed class TenantIsolationCollection : ICollectionFixture<TenantIsolationFixture>
{
    public const string Name = "tenant-isolation";
}

/// <summary>Cross-tenant isolation: tenant B, with its own valid key, gets 404 on every one of tenant A's resources
/// (payloads, runs, timelines, drift, replay, SSE, screenshots), cannot see A's blobs, and its listings show only its
/// own rows. 404 (not 403) is deliberate — a tenant must not confirm another tenant's resource even exists.</summary>
[Collection(TenantIsolationCollection.Name)]
public class TenantIsolationTests(TenantIsolationFixture fixture)
{
    private IAlbaHost Host => fixture.Host;

    [Fact]
    public async Task Tenant_B_cannot_read_tenant_As_payload() =>
        await ExpectStatusAsync("GET", $"/payloads/{fixture.PayloadA}", HttpStatusCode.NotFound);

    [Fact]
    public async Task Tenant_B_cannot_revise_tenant_As_payload() =>
        await ExpectStatusAsync("POST", $"/payloads/{fixture.PayloadA}/revise", HttpStatusCode.NotFound,
            new JsonObject { ["payload"] = JsonNode.Parse(TenantIsolationFixture.DemoPayload) });

    [Fact]
    public async Task Tenant_B_cannot_archive_tenant_As_payload() =>
        await ExpectStatusAsync("POST", $"/payloads/{fixture.PayloadA}/archive", HttpStatusCode.NotFound);

    [Fact]
    public async Task Tenant_B_cannot_rename_tenant_As_payload() => // valid body so the name validator passes — the 404 is the tenant guard, not validation
        await ExpectStatusAsync("POST", $"/payloads/{fixture.PayloadA}/rename", HttpStatusCode.NotFound,
            new JsonObject { ["name"] = "renamed-by-b" });

    [Fact]
    public async Task Tenant_B_cannot_read_tenant_As_run() =>
        await ExpectStatusAsync("GET", $"/runs/{fixture.SucceededRunA}", HttpStatusCode.NotFound);

    [Fact]
    public async Task Tenant_B_cannot_cancel_tenant_As_run() =>
        await ExpectStatusAsync("POST", $"/runs/{fixture.SucceededRunA}/cancel", HttpStatusCode.NotFound);

    [Fact]
    public async Task Tenant_B_cannot_read_tenant_As_timeline() =>
        await ExpectStatusAsync("GET", $"/runs/{fixture.SucceededRunA}/timeline", HttpStatusCode.NotFound);

    [Fact]
    public async Task Tenant_B_cannot_read_tenant_As_drift() =>
        await ExpectStatusAsync("GET", $"/runs/{fixture.SucceededRunA}/drift", HttpStatusCode.NotFound);

    [Fact]
    public async Task Tenant_B_cannot_replay_tenant_As_run() =>
        await ExpectStatusAsync("POST", $"/runs/{fixture.SucceededRunA}/replay", HttpStatusCode.NotFound,
            new JsonObject { ["inputs"] = new JsonObject() });

    [Fact]
    public async Task Tenant_B_cannot_stream_tenant_As_run_events()
    {
        using var client = Host.GetTestServer().CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", TestTenants.Bearer(TestTenants.SecondaryKey));
        using var response = await client.GetAsync(new Uri($"/runs/{fixture.SucceededRunA}/events", UriKind.Relative));
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound); // an unreadable stream is indistinguishable from an unknown run
    }

    [Fact]
    public async Task Listings_are_tenant_filtered()
    {
        var idsA = await ListPayloadIdsAsync(TestTenants.PrimaryKey);
        idsA.ShouldContain(fixture.PayloadA); // A sees its own payload

        var idsB = await ListPayloadIdsAsync(TestTenants.SecondaryKey);
        idsB.ShouldNotContain(fixture.PayloadA); // B's listing never leaks A's payload
    }

    [Fact]
    public async Task Tenant_A_still_reads_its_own_run_and_screenshot()
    {
        // Not vacuous: A's own reads succeed and its failing run really did capture a screenshot.
        var timeline = await GetJsonAsync(TestTenants.PrimaryKey, $"/runs/{fixture.FailedRunA}/timeline");
        timeline.GetProperty("failure").GetProperty("screenshotRef").GetString().ShouldStartWith("screenshots/");
        await ExpectStatusAsync("GET", $"/runs/{fixture.SucceededRunA}", HttpStatusCode.OK, apiKey: TestTenants.PrimaryKey);
    }

    [Fact]
    public async Task Tenant_B_cannot_retrieve_tenant_As_failure_screenshot()
    {
        // A's failing run captured a screenshot-on-failure; its ref is exposed on A's timeline as screenshots/{sha}.png.
        var timeline = await GetJsonAsync(TestTenants.PrimaryKey, $"/runs/{fixture.FailedRunA}/timeline");
        var tail = timeline.GetProperty("failure").GetProperty("screenshotRef").GetString()!["screenshots/".Length..];

        // B is refused (404, indistinguishable from an unknown run) while A retrieves its own capture — proving the run
        // association, not blob knowledge, is the authorization.
        await ExpectStatusAsync("GET", $"/runs/{fixture.FailedRunA}/screenshots/{tail}", HttpStatusCode.NotFound);
        await ExpectStatusAsync("GET", $"/runs/{fixture.FailedRunA}/screenshots/{tail}", HttpStatusCode.OK, apiKey: TestTenants.PrimaryKey);
    }

    [Fact]
    public void Screenshot_storage_is_partitioned_by_tenant()
    {
        var screenshots = (InMemoryScreenshotStore)Host.Services.GetRequiredService<IScreenshotStore>();
        screenshots.ReferencesFor(TestTenants.PrimaryId).ShouldNotBeEmpty(); // A's failing run captured under A's partition
        screenshots.ReferencesFor(TestTenants.SecondaryId).ShouldBeEmpty();  // B never captured one — its partition is empty
    }

    [Fact]
    public void Every_run_connects_a_fresh_backend_session()
    {
        // Three runs (two for A, one for B) each opened their own session — no instance reused across runs, so no browser
        // session is ever shared between tenants (they are per-run by construction).
        fixture.Backend.Sessions.Count.ShouldBe(3);
        fixture.Backend.Sessions.Distinct().Count().ShouldBe(fixture.Backend.Sessions.Count);
    }

    private async Task<List<Guid>> ListPayloadIdsAsync(string apiKey)
    {
        var listing = await GetJsonAsync(apiKey, "/payloads");
        return [.. listing.GetProperty("payloads").EnumerateArray().Select(p => p.GetProperty("payloadId").GetGuid())];
    }

    private async Task<JsonElement> GetJsonAsync(string apiKey, string url)
    {
        var result = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(apiKey));
            x.Get.Url(url);
            x.StatusCodeShouldBeOk();
        });
        return (await result.ReadAsJsonAsync<JsonElement>()).Clone();
    }

    // Cross-tenant probes default to tenant B's (valid) key — the whole point is that a legitimate other tenant is refused.
    private async Task ExpectStatusAsync(string method, string url, HttpStatusCode expected, JsonObject? body = null, string apiKey = TestTenants.SecondaryKey) =>
        await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(apiKey));
            if (string.Equals(method, "GET", StringComparison.Ordinal))
            {
                x.Get.Url(url);
            }
            else
            {
                x.Post.Json(body ?? new JsonObject()).ToUrl(url);
            }

            x.StatusCodeShouldBe((int)expected);
        });
}
