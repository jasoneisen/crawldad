using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Api.Features.Runs;
using Crawldad.Tests.Support;

namespace Crawldad.Tests.Integration;

/// <summary>A background-executor host pinned to a per-tenant concurrent-run cap of 1, with a re-armable gate so a
/// test can hold one run mid-execution while it probes admission of the next. Built lazily (its schema migrates on
/// first use) and disposed with the collection.</summary>
public sealed class ConcurrentRunsFixture : IAsyncLifetime
{
    private IAlbaHost? _host;

    internal GateHolder Gate { get; } = new();

    public Task InitializeAsync() => Task.CompletedTask;

    internal async Task<IAlbaHost> EnsureAsync() =>
        _host ??= await DurableHost.BuildAsync(
            "crawldad_concurrency",
            new GatedFakeBackend(Runner.FixturesRoot, Gate),
            settings: [new KeyValuePair<string, string?>("Crawldad:Limits:MaxConcurrentRunsPerTenant", "1")]);

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class ConcurrentRunsCollection : ICollectionFixture<ConcurrentRunsFixture>
{
    public const string Name = "concurrent-runs";
}

/// <summary>The per-tenant concurrent-run cap (1) at <c>POST /runs</c> admission queues rather than rejects: with
/// one run held mid-execution, a second start is accepted 202 <c>status:"queued"</c> position 1 (not 429), then
/// auto-promotes once the first run frees its slot. The cap-exceeded 429 is covered separately by <c>SlotQueueTests</c>.</summary>
[Collection(ConcurrentRunsCollection.Name)]
public class ConcurrentRunsCapTests(ConcurrentRunsFixture fixture)
{
    // A minimal async payload with one gated postback so a run can be caught mid-execution (holding its slot) and then
    // cancelled — a deterministic teardown that reaches a terminal state without depending on the postback completing.
    private static JsonObject Body() => new()
    {
        ["payload"] = JsonNode.Parse(
            """
            { "crawldad": "1", "name": "concurrency.demo", "config": { "backend": "input.backend" }, "vars": {},
              "steps": [
                { "goto": { "url": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx?module=Enforcement&TabName=Enforcement" } },
                { "waitForRequest": { "urlPrefix": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx", "method": "POST",
                    "trigger": [ { "click": { "selector": "#ctl00_PlaceHolderMain_btnNewSearch" } } ] } },
                { "log": { "level": "info", "message": "after the postback — a between-steps point the cooperative cancel catches" } }
              ],
              "result": "'done'" }
            """),
        ["inputs"] = new JsonObject
        {
            ["backend"] = new JsonObject
            {
                ["adapter"] = "fake",
                ["options"] = new JsonObject { ["fixture"] = "caphome-resume" },
            },
        },
        ["async"] = true,
    };

    [Fact]
    public async Task At_the_tenant_cap_the_next_run_is_queued_then_auto_starts_when_a_slot_frees()
    {
        var host = await fixture.EnsureAsync();

        // Run 1 is admitted (202) and blocks mid-execution — the tenant's one slot is now occupied.
        var firstRunId = await StartBlockedAsync(host);

        // Run 2 hits the cap at admission — queued (202), not rejected.
        var accepted = await host.Scenario(x =>
        {
            x.Post.Json(Body()).ToUrl("/runs");
            x.StatusCodeShouldBe(202);
        });
        var queued = (await accepted.ReadAsJsonAsync<JsonElement>()).Clone();
        queued.GetProperty("status").GetString().ShouldBe("queued");
        queued.GetProperty("position").GetInt32().ShouldBe(1);
        var queuedRunId = queued.GetProperty("runId").GetGuid();

        // GET shows the queued run's live position while it waits behind the cap.
        var polled = await host.Scenario(x =>
        {
            x.Get.Url($"/runs/{queuedRunId}");
            x.StatusCodeShouldBe(200);
        });
        var state = (await polled.ReadAsJsonAsync<JsonElement>()).Clone();
        state.GetProperty("status").GetString().ShouldBe("queued");
        state.GetProperty("position").GetInt32().ShouldBe(1);

        // Cancel run 1 to a terminal state; the executor frees its slot, which auto-promotes the queued run.
        await CancelToTerminalAsync(host, firstRunId);

        // The queued run leaves the queue on its own and runs to completion — never a slot beyond the cap of 1.
        var terminal = await DurableHost.PollUntilTerminalAsync(host, queuedRunId, TimeSpan.FromSeconds(30));
        terminal.GetProperty("status").GetString().ShouldBe("succeeded");
        terminal.GetProperty("queueWaitMs").GetInt64().ShouldBe(0); // promoted under the frozen test clock — the wait metric is recorded
    }

    // Arms the gate, starts an async run, and waits until it is provably blocked mid-execution (its slot held).
    private async Task<Guid> StartBlockedAsync(IAlbaHost host)
    {
        var gate = new RunGate("CapHome");
        _pending = gate;
        fixture.Gate.Arm(gate);

        var accepted = await host.Scenario(x =>
        {
            x.Post.Json(Body()).ToUrl("/runs");
            x.StatusCodeShouldBe(202);
        });
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(20));
        return (await accepted.ReadAsJsonAsync<JsonElement>()).GetProperty("runId").GetGuid();
    }

    // Cancels the blocked run, releases the gate so it reaches its between-steps cancel check, and polls it to cancelled.
    private async Task CancelToTerminalAsync(IAlbaHost host, Guid runId)
    {
        await host.Scenario(x =>
        {
            x.Post.Json(new JsonObject()).ToUrl($"/runs/{runId}/cancel");
            x.StatusCodeShouldBe(202);
        });
        _pending!.Release();
        var terminal = await DurableHost.PollUntilTerminalAsync(host, runId, TimeSpan.FromSeconds(20));
        terminal.GetProperty("status").GetString().ShouldBe("cancelled");
    }

    private RunGate? _pending;
}
