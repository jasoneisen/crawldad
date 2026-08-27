using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Api;
using Crawldad.Api.Infrastructure.Browser;
using Crawldad.Api.Infrastructure.Browser.Fake;
using Crawldad.Tests.Support;
using JasperFx.Events.Daemon;
using Marten;
using Marten.Events;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>A host running projections on the production lifecycle — <c>Async</c>, built by the projection daemon out of
/// band — rather than the suite's default <c>Inline</c>. Lazily built (after the eager fixtures) so its schema migration
/// does not contend, and single-node so the solo daemon actually runs.</summary>
public sealed class AsyncProjectionFixture : IAsyncLifetime
{
    private IAlbaHost? _host;

    public Task InitializeAsync() => Task.CompletedTask;

    internal async Task<IAlbaHost> EnsureAsync()
    {
        if (_host is null)
        {
            _host = (await AlbaHost.For<Program>(builder =>
            {
                builder.UseCrawldadTestDefaults("crawldad_async_proj");
                builder.UseSetting(HostConfiguration.ProjectionLifecycleKey, "Async"); // override the test default (Inline) — the prod path
                builder.ConfigureServices(services =>
                {
                    // Keep the REAL clock (not the frozen FakeClock the other hosts use): the async projection daemon polls on
                    // wall-clock, so a frozen provider stalls its catch-up. This test asserts the timeline's tenant scope, not
                    // its timestamps, so real time is fine.
                    services.AddKeyedSingleton<IBrowserBackend>("fake", (_, _) => new FakeBrowserBackend(Runner.FixturesRoot));
                });
            })).AuthenticatedAsPrimaryTenant();
            await _host.ResetAllMartenDataAsync();
        }

        return _host;
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class AsyncProjectionCollection : ICollectionFixture<AsyncProjectionFixture>
{
    public const string Name = "async-projection";
}

/// <summary>The tenant boundary holds through the ASYNC projection daemon, not just the inline path the rest of
/// the suite forces. A run's <c>RunTimeline</c> is built off-band under the production <c>Async</c> lifecycle;
/// tenant A reads its own timeline while tenant B gets 404, so async catch-up never crosses tenants.</summary>
[Collection(AsyncProjectionCollection.Name)]
public class AsyncProjectionTenancyTests(AsyncProjectionFixture fixture)
{
    private const string _payload =
        """
        { "crawldad": "1", "name": "async.proj", "config": { "backend": "input.backend" }, "vars": {},
          "steps": [ { "goto": { "url": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx?module=Enforcement&TabName=Enforcement" } } ],
          "result": "'ok'" }
        """;

    private static JsonObject Body() => new()
    {
        ["payload"] = JsonNode.Parse(_payload),
        ["inputs"] = new JsonObject { ["backend"] = new JsonObject { ["adapter"] = "fake", ["options"] = new JsonObject { ["fixture"] = "caphome-multipage" } } },
        ["async"] = true,
    };

    [Fact]
    public async Task The_async_projection_daemon_writes_the_timeline_into_the_runs_tenant_partition()
    {
        var host = await fixture.EnsureAsync();

        // Tenant A starts an async run; its step-trace events accrue in A's partition (the daemon has not run yet).
        var accepted = await host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(TestTenants.PrimaryKey));
            x.Post.Json(Body()).ToUrl("/runs");
            x.StatusCodeShouldBe(202);
        });
        var runId = (await accepted.ReadAsJsonAsync<JsonElement>()).GetProperty("runId").GetGuid();

        // Wait for the run to finish so all its trace events are in the store (the executor writes RunProgress directly, so
        // this poll does not depend on the async daemon).
        var terminal = await DurableHost.PollUntilTerminalAsync(host, runId, TimeSpan.FromSeconds(20));
        terminal.GetProperty("status").GetString().ShouldBe("succeeded");

        // Drive the async projection daemon to catch up — the production path the suite otherwise forces Inline (solo mode
        // registers but does not auto-start it, so start it explicitly, then wait for RunTimeline to be non-stale).
        var store = host.Services.GetRequiredService<IDocumentStore>();
        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();
        await store.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));

        // Confirms the async write landed only in tenant A's partition.
        await host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(TestTenants.PrimaryKey));
            x.Get.Url($"/runs/{runId}/timeline");
            x.StatusCodeShouldBe(200);
        });
        await host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(TestTenants.SecondaryKey));
            x.Get.Url($"/runs/{runId}/timeline");
            x.StatusCodeShouldBe(404);
        });
    }
}
