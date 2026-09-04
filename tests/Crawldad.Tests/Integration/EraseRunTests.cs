using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Api.Features.Runs;
using Crawldad.Api.Infrastructure.Browser.Fake;
using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>On-demand erasure (issue #71): <c>DELETE /runs/{id}</c> hard-erases a finished run's result document, all
/// three derived read models (including the <c>RunSummary</c> row <c>GET /runs</c> lists — issue #160), and its event
/// stream — <c>204</c> on success, idempotent <c>404</c> after, and a <c>409</c> <c>run_still_active</c> refusal for a
/// run that has not finished. Tenant-scoping is proven separately in <c>TenantIsolationTests</c>.</summary>
public class EraseRunTests
{
    [Fact]
    public async Task Erasing_a_finished_run_removes_its_result_timeline_and_events_and_is_idempotent()
    {
        await using var host = await DurableHost.BuildAsync("crawldad_erase_e2e", new FakeBrowserBackend(Runner.FixturesRoot));
        var runId = await RunAsyncToSucceededAsync(host);
        var store = host.Services.GetRequiredService<IDocumentStore>();

        // Precondition: the run left an event stream, its stored result, and all three derived read models — and the
        // RunSummary row is what puts it in the GET /runs listing.
        await using (var session = store.LightweightSession(TestTenants.PrimaryId))
        {
            (await session.Events.FetchStreamAsync(runId)).ShouldNotBeEmpty();
            (await session.LoadAsync<RunProgress>(runId)).ShouldNotBeNull();
            (await session.LoadAsync<Run>(runId)).ShouldNotBeNull();
            (await session.LoadAsync<RunTimeline>(runId)).ShouldNotBeNull();
            (await session.LoadAsync<RunSummary>(runId)).ShouldNotBeNull();
        }

        (await ListedRunIdsAsync(host)).ShouldContain(runId);

        // 204 with no body — the erased content is never echoed back.
        var response = await host.Scenario(x =>
        {
            x.Delete.Url($"/runs/{runId}");
            x.StatusCodeShouldBe(204);
        });
        (await response.ReadAsTextAsync()).ShouldBeEmpty();

        // The result body, all three derived read models, AND the event stream (its incidental PII) are all gone.
        await using (var session = store.LightweightSession(TestTenants.PrimaryId))
        {
            (await session.Events.FetchStreamAsync(runId)).ShouldBeEmpty();
            (await session.LoadAsync<RunProgress>(runId)).ShouldBeNull();
            (await session.LoadAsync<Run>(runId)).ShouldBeNull();
            (await session.LoadAsync<RunTimeline>(runId)).ShouldBeNull();
            (await session.LoadAsync<RunSummary>(runId)).ShouldBeNull();
        }

        // Every read surface 404s coherently, and a repeat DELETE is idempotent (404, no oracle for an erased run).
        await ExpectGetAsync(host, $"/runs/{runId}", 404);
        await ExpectGetAsync(host, $"/runs/{runId}/timeline", 404);
        await ExpectGetAsync(host, $"/runs/{runId}/drift", 404);
        await host.Scenario(x =>
        {
            x.Delete.Url($"/runs/{runId}");
            x.StatusCodeShouldBe(404);
        });

        // ...and the listing agrees: an erased run's headline metadata no longer surfaces in GET /runs.
        (await ListedRunIdsAsync(host)).ShouldNotContain(runId);
    }

    [Fact]
    public async Task Erase_classifies_unknown_synchronous_and_still_active_runs()
    {
        await using var host = await DurableHost.BuildAsync("crawldad_erase_classify", new FakeBrowserBackend(Runner.FixturesRoot));

        // Unknown run → 404 (no oracle).
        await host.Scenario(x =>
        {
            x.Delete.Url($"/runs/{Guid.NewGuid()}");
            x.StatusCodeShouldBe(404);
        });

        // A purely-synchronous run writes no progress row, so it too is a 404 (nothing to erase on the async surface).
        var sync = await RunSyncToSucceededAsync(host);
        await host.Scenario(x =>
        {
            x.Delete.Url($"/runs/{sync}");
            x.StatusCodeShouldBe(404);
        });

        // A still-active run (running or queued) is a 409 run_still_active — the writer must settle first. Seeded directly
        // so the state is deterministic (no gate machinery needed to hold a run non-terminal).
        foreach (var status in new[] { RunStatus.Running, RunStatus.Queued })
        {
            var runId = Guid.NewGuid();
            await SeedAsync(host, new RunProgress { Id = runId, Status = status });

            var conflict = await host.Scenario(x =>
            {
                x.Delete.Url($"/runs/{runId}");
                x.StatusCodeShouldBe(409);
            });
            (await conflict.ReadAsJsonAsync<RunRejection>())!.Code.ShouldBe(EraseRunEndpoint.RunStillActiveCode);

            // The refusal changed nothing: the run is still there.
            (await LoadAsync(host, runId))!.Status.ShouldBe(status);
        }
    }

    private static async Task SeedAsync(IAlbaHost host, RunProgress progress)
    {
        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(TestTenants.PrimaryId);
        session.Store(progress);
        await session.SaveChangesAsync();
    }

    private static async Task<RunProgress?> LoadAsync(IAlbaHost host, Guid id)
    {
        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(TestTenants.PrimaryId);
        return await session.LoadAsync<RunProgress>(id);
    }

    private static JsonObject Body(bool async) => new()
    {
        ["payload"] = JsonNode.Parse("""{ "config": { "backend": "input.backend" }, "vars": {}, "steps": [], "result": "'ok'" }"""),
        ["inputs"] = new JsonObject { ["backend"] = new JsonObject { ["adapter"] = "fake", ["options"] = new JsonObject { ["fixture"] = "caphome-search" } } },
        ["async"] = async,
    };

    private static async Task<Guid> RunAsyncToSucceededAsync(IAlbaHost host)
    {
        var accepted = await host.Scenario(x =>
        {
            x.Post.Json(Body(async: true)).ToUrl("/runs");
            x.StatusCodeShouldBe(202);
        });
        var runId = (await accepted.ReadAsJsonAsync<JsonElement>()).GetProperty("runId").GetGuid();
        (await DurableHost.PollUntilTerminalAsync(host, runId, DurableHost.PollTimeout)).GetProperty("status").GetString().ShouldBe("succeeded");
        return runId;
    }

    private static async Task<Guid> RunSyncToSucceededAsync(IAlbaHost host)
    {
        var result = await host.Scenario(x =>
        {
            x.Post.Json(Body(async: false)).ToUrl("/runs");
            x.StatusCodeShouldBe(200);
        });
        var root = await result.ReadAsJsonAsync<JsonElement>();
        root.GetProperty("status").GetString().ShouldBe("succeeded");
        return root.GetProperty("runId").GetGuid();
    }

    // The run ids GET /runs currently lists for the test tenant — the RunSummary-backed listing, read over HTTP so the
    // assertion is the surface a tenant actually sees rather than the document behind it.
    private static async Task<IReadOnlyList<Guid>> ListedRunIdsAsync(IAlbaHost host)
    {
        var listed = await host.Scenario(x =>
        {
            x.Get.Url("/runs");
            x.StatusCodeShouldBeOk();
        });
        var body = await listed.ReadAsJsonAsync<JsonElement>();
        return [.. body.GetProperty("runs").EnumerateArray().Select(row => row.GetProperty("runId").GetGuid())];
    }

    private static async Task ExpectGetAsync(IAlbaHost host, string url, int status) =>
        await host.Scenario(x =>
        {
            x.Get.Url(url);
            x.StatusCodeShouldBe(status);
        });
}
