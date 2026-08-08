using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Runs;

namespace Crawldad.Tests.Integration;

/// <summary>A background-executor host pinned to a per-tenant concurrent-run cap of 1 (CD-3, docs/PRODUCT.md §Pv.3), with a
/// re-armable gate so a test can hold one run mid-execution while it probes admission of the next. Built lazily (its schema
/// migrates on first use) and disposed with the collection.</summary>
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

/// <summary>
/// The CD-3 billing-critical limit end-to-end (limit 5): the per-tenant concurrent-run cap enforced at <c>POST /runs</c>
/// admission. With the cap at 1 and one run held mid-execution, a second start is rejected <c>429</c> with the
/// machine-readable <c>concurrent_runs_exceeded</c> code (the seam CD-16 will turn into a queue); once the first run frees
/// its slot at terminal, a later start is admitted again — proving the async slot is held from admission to finalisation
/// and released by the executor.
/// </summary>
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
    public async Task At_the_tenant_cap_a_second_run_is_rejected_429_then_admitted_once_a_slot_frees()
    {
        var host = await fixture.EnsureAsync();

        // Run 1 is admitted (202) and blocks mid-execution — the tenant's one slot is now occupied.
        var firstRunId = await StartBlockedAsync(host);

        // Run 2 hits the cap at admission — no run is started, and the body is the typed rejection.
        var rejected = await host.Scenario(x =>
        {
            x.Post.Json(Body()).ToUrl("/runs");
            x.StatusCodeShouldBe(429);
        });
        (await rejected.ReadAsJsonAsync<JsonElement>()).GetProperty("code").GetString().ShouldBe(RunAdmissionGate.RejectionCode);

        // Cancel run 1 to a terminal state; the executor frees its slot before the terminal status is observable.
        await CancelToTerminalAsync(host, firstRunId);

        // With the slot freed, a fresh start is admitted again (202) — the cap is a live gauge, not a one-way latch — and it
        // is cancelled too, so the test leaves no run in a non-terminal state.
        var thirdRunId = await StartBlockedAsync(host);
        await CancelToTerminalAsync(host, thirdRunId);
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
