using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Runs;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;

namespace Crawldad.Tests.Integration;

/// <summary>One shared background-executor host for the async/cancel/deadline gates (built once, its schema migrated once —
/// the durable tests otherwise contend on parallel schema migration). Built <b>lazily</b> on first use (mirroring the leak
/// host) so its migration lands after the eager collection fixtures' inits, not alongside them. Its <c>fake</c> backend
/// reads a re-armable <see cref="GateHolder"/> so each test drives its own gate + fetch recorder on the one host.</summary>
public sealed class DurableFixture : IAsyncLifetime
{
    private IAlbaHost? _host;

    internal GateHolder Gate { get; } = new();

    internal GatedFakeBackend Backend { get; private set; } = null!;

    public Task InitializeAsync() => Task.CompletedTask; // built lazily on first use

    /// <summary>Builds the shared host on first call, then returns it.</summary>
    internal async Task<IAlbaHost> EnsureAsync()
    {
        if (_host is null)
        {
            Backend = new GatedFakeBackend(Runner.FixturesRoot, Gate);
            _host = await DurableHost.BuildAsync("crawldad_durable", Backend);
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
public sealed class DurableCollection : ICollectionFixture<DurableFixture>
{
    public const string Name = "durable-runs";
}

/// <summary>
/// The Phase 5 WP2 durable-execution gates (§11): the async control surface (<c>202</c> + <c>GET /runs/{id}</c> poll),
/// cooperative cancellation with a clean teardown, checkpoint resume across an honest host kill, and the wall-clock
/// deadline. Every run drives the FULL <c>SearchEnforcementRecords</c> payload (with its P5 checkpoint) through the real
/// <c>POST /runs</c> path and the executor saga against the record/replay fake — no Chromium, no live traffic.
/// </summary>
[Collection(DurableCollection.Name)]
public class DurableRunTests(DurableFixture fixture)
{
    private static string SearchPayload() => File.ReadAllText(Path.Combine(Runner.FixturesRoot, "Payloads", "search-full.json"));

    private static JsonObject SearchBody(string fixture, bool async) => new()
    {
        ["payload"] = JsonNode.Parse(SearchPayload()),
        ["inputs"] = new JsonObject
        {
            ["backend"] = new JsonObject
            {
                ["adapter"] = "fake",
                ["options"] = new JsonObject { ["fixture"] = fixture },
            },
            ["startDate"] = "01/01/2024",
            ["endDate"] = "01/31/2024",
            ["knownUrls"] = new JsonArray(),
            ["priorCrawlComplete"] = false,
        },
        ["async"] = async,
    };

    private static async Task<(Guid RunId, JsonElement Accepted)> StartAsyncAsync(IAlbaHost host, JsonObject body)
    {
        var accepted = await host.Scenario(x =>
        {
            x.Post.Json(body).ToUrl("/runs");
            x.StatusCodeShouldBe(202);
        });
        var root = (await accepted.ReadAsJsonAsync<JsonElement>()).Clone();
        return (root.GetProperty("runId").GetGuid(), root);
    }

    private static void AssertMatchesFullGolden(JsonElement result)
    {
        using var golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(Runner.FixturesRoot, "caphome-multipage", "golden-a-full.json")));
        JsonAssert.DeepEquals(result, golden.RootElement).ShouldBeTrue();
        JsonAssert.Canonical(result).ShouldBe(JsonAssert.Canonical(golden.RootElement));
    }

    private static async Task<IReadOnlyList<Type>> EventTypesAsync(IAlbaHost host, Guid runId)
    {
        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(TestTenants.PrimaryId);
        return [.. (await session.Events.FetchStreamAsync(runId)).Select(e => e.EventType)];
    }

    // ----- async mode + polling ----------------------------------------------

    [Fact]
    public async Task Async_run_accepts_202_then_completes_with_the_same_result_as_sync()
    {
        fixture.Gate.Arm(gate: null);
        var (runId, accepted) = await StartAsyncAsync((await fixture.EnsureAsync()), SearchBody("caphome-multipage", async: true));
        accepted.GetProperty("status").GetString().ShouldBe("running");
        accepted.TryGetProperty("result", out _).ShouldBeFalse(); // just { runId, status } while running

        var terminal = await DurableHost.PollUntilTerminalAsync((await fixture.EnsureAsync()), runId, TimeSpan.FromSeconds(20));
        terminal.GetProperty("status").GetString().ShouldBe("succeeded");
        AssertMatchesFullGolden(terminal.GetProperty("result")); // identical to the caphome-multipage full-crawl golden
    }

    [Fact]
    public async Task Get_for_an_unknown_run_is_404() =>
        await (await fixture.EnsureAsync()).Scenario(x =>
        {
            x.Get.Url($"/runs/{Guid.NewGuid()}");
            x.StatusCodeShouldBe(404);
        });

    // ----- cooperative cancellation (§11) ------------------------------------

    [Fact]
    public async Task Cancel_mid_run_tears_the_session_down_and_reports_a_partial()
    {
        var gate = new RunGate("pg=2");
        fixture.Gate.Arm(gate);
        var (runId, _) = await StartAsyncAsync((await fixture.EnsureAsync()), SearchBody("caphome-resume", async: true));
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(20)); // blocked mid-crawl (page 1 & 2 done)

        // Request cancellation, then release the block so the interpreter reaches its next between-steps check.
        var cancelResponse = await (await fixture.EnsureAsync()).Scenario(x =>
        {
            x.Post.Json(new JsonObject()).ToUrl($"/runs/{runId}/cancel");
            x.StatusCodeShouldBe(202);
        });
        (await cancelResponse.ReadAsJsonAsync<JsonElement>()).GetProperty("status").GetString().ShouldBe("running");
        gate.Release();

        var terminal = await DurableHost.PollUntilTerminalAsync((await fixture.EnsureAsync()), runId, TimeSpan.FromSeconds(20));
        terminal.GetProperty("status").GetString().ShouldBe("cancelled");

        // The partial carries the pages crawled so far (a well-formed result-so-far), not the full crawl.
        var partial = terminal.GetProperty("partial");
        partial.GetProperty("crawledToEnd").GetBoolean().ShouldBeFalse();
        partial.GetProperty("newLinks").GetArrayLength().ShouldBeGreaterThan(0);

        // No orphaned browser session: the run tore its (fake) session down cleanly.
        fixture.Backend.LastSession!.Disposed.ShouldBeTrue();

        var types = await EventTypesAsync((await fixture.EnsureAsync()), runId);
        types.ShouldContain(typeof(RunCancellationRequested));
        types.ShouldContain(typeof(RunCancelled));
    }

    [Fact]
    public async Task Cancel_for_an_unknown_run_is_404() =>
        await (await fixture.EnsureAsync()).Scenario(x =>
        {
            x.Post.Json(new JsonObject()).ToUrl($"/runs/{Guid.NewGuid()}/cancel");
            x.StatusCodeShouldBe(404);
        });

    [Fact]
    public async Task Cancel_after_completion_is_a_no_op()
    {
        fixture.Gate.Arm(gate: null);
        var (runId, _) = await StartAsyncAsync((await fixture.EnsureAsync()), SearchBody("caphome-multipage", async: true));
        var terminal = await DurableHost.PollUntilTerminalAsync((await fixture.EnsureAsync()), runId, TimeSpan.FromSeconds(20));
        terminal.GetProperty("status").GetString().ShouldBe("succeeded");

        // Cancelling a finished run is accepted but does nothing — its status stays succeeded and no cancel event is added.
        await (await fixture.EnsureAsync()).Scenario(x =>
        {
            x.Post.Json(new JsonObject()).ToUrl($"/runs/{runId}/cancel");
            x.StatusCodeShouldBe(202);
        });
        (await EventTypesAsync((await fixture.EnsureAsync()), runId)).ShouldNotContain(typeof(RunCancellationRequested));
    }

    // ----- wall-clock deadline (§8.4) ----------------------------------------

    [Fact]
    public async Task A_run_that_outruns_its_deadline_fails_terminally()
    {
        fixture.Gate.Arm(new RunGate("CapHome")); // stall the very first postback so the run is stuck when the deadline fires

        // A minimal payload whose one postback the gate stalls; a short deadline then forcibly caps it (§8.4).
        var payload = JsonNode.Parse(
            """
            { "crawldad": "1", "name": "deadline.demo", "config": { "backend": "input.backend", "deadlineMs": 200 }, "vars": {},
              "steps": [
                { "goto": { "url": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx?module=Enforcement&TabName=Enforcement" } },
                { "waitForRequest": { "urlPrefix": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx", "method": "POST",
                    "trigger": [ { "click": { "selector": "#ctl00_PlaceHolderMain_btnNewSearch" } } ] } }
              ],
              "result": "'unreached'" }
            """);
        var body = new JsonObject
        {
            ["payload"] = payload,
            ["inputs"] = new JsonObject { ["backend"] = new JsonObject { ["adapter"] = "fake", ["options"] = new JsonObject { ["fixture"] = "caphome-resume" } } },
            ["async"] = true,
        };

        var (runId, _) = await StartAsyncAsync((await fixture.EnsureAsync()), body);

        var terminal = await DurableHost.PollUntilTerminalAsync((await fixture.EnsureAsync()), runId, TimeSpan.FromSeconds(30));
        terminal.GetProperty("status").GetString().ShouldBe("failed");
        var failure = terminal.GetProperty("failure");
        failure.GetProperty("class").GetString().ShouldBe("terminal");
        failure.GetProperty("code").GetString().ShouldBe("run_deadline_exceeded");
    }

    // ----- kill-and-restart resume (the load-bearing WP2 gate, §11) ----------

    [Fact]
    public async Task Killed_run_resumes_from_the_last_checkpoint_without_refetching_earlier_pages()
    {
        const string Schema = "crawldad_kill";

        // Host 1 blocks the pagination that DEPARTS page 2 — so it is caught mid-crawl having durably passed the page-1 and
        // page-2 checkpoints, page 3 not yet fetched.
        var firstHolder = new GateHolder();
        var firstFetches = firstHolder.Arm(new RunGate("pg=2"));
        var host1 = await DurableHost.BuildAsync(Schema, new GatedFakeBackend(Runner.FixturesRoot, firstHolder));

        Guid runId;
        try
        {
            (runId, _) = await StartAsyncAsync(host1, SearchBody("caphome-resume", async: true));
            await ((RunGate)firstHolder.Gate!).Reached.WaitAsync(TimeSpan.FromSeconds(20)); // blocked, past >= 2 checkpoints
            firstFetches.Urls.ShouldContain(u => u.Contains("pg=1", StringComparison.Ordinal)); // host 1 fetched page 1
            firstFetches.Urls.ShouldNotContain(u => u.Contains("pg=3", StringComparison.Ordinal)); // but never reached page 3
        }
        finally
        {
            // Honest kill: dispose the host with the executor still mid-run. The run is left un-finalised ("running"),
            // not dead-lettered — the fresh host's recovery scan re-drives it.
            await host1.DisposeAsync();
        }

        // A FRESH host on the SAME schema/durable queues recovers the interrupted run and resumes.
        var resumeHolder = new GateHolder();
        var resumeFetches = resumeHolder.Arm(gate: null);
        await using var host2 = await DurableHost.BuildAsync(Schema, new GatedFakeBackend(Runner.FixturesRoot, resumeHolder), resetData: false);

        var terminal = await DurableHost.PollUntilTerminalAsync(host2, runId, TimeSpan.FromSeconds(40));
        terminal.GetProperty("status").GetString().ShouldBe("succeeded");
        AssertMatchesFullGolden(terminal.GetProperty("result")); // SAME result as an uninterrupted run

        // The proof it resumed from the checkpoint rather than restarting: host 2 re-entered page 2 (the checkpoint page)
        // and page 3, but NEVER re-fetched page 1 (an earlier, already-completed page).
        resumeFetches.Urls.ShouldNotContain(u => u.Contains("pg=1", StringComparison.Ordinal));
        resumeFetches.Urls.ShouldContain(u => u.Contains("pg=2", StringComparison.Ordinal));
        resumeFetches.Urls.ShouldContain(u => u.Contains("pg=3", StringComparison.Ordinal));

        // And the trace carries the durable proof of resume: checkpoints from host 1 and a RunResumed marker from host 2.
        var types = await EventTypesAsync(host2, runId);
        types.ShouldContain(typeof(RunResumed));
        types.Count(t => t == typeof(RunCheckpointReached)).ShouldBeGreaterThanOrEqualTo(2);
    }

    // ----- async terminal failure + endpoint edges ---------------------------

    [Fact]
    public async Task A_nameless_async_run_with_no_inputs_fails_terminally_in_the_background()
    {
        var host = await fixture.EnsureAsync();
        fixture.Gate.Arm(gate: null);

        // Nameless + no inputs exercises the async endpoint's unnamed/empty-inputs paths; with no backend the run fails.
        var body = new JsonObject
        {
            ["payload"] = JsonNode.Parse("""{ "crawldad": "1", "config": { "backend": "input.backend" }, "steps": [], "result": "'ok'" }"""),
            ["async"] = true,
        };
        var (runId, _) = await StartAsyncAsync(host, body);

        var terminal = await DurableHost.PollUntilTerminalAsync(host, runId, TimeSpan.FromSeconds(20));
        terminal.GetProperty("status").GetString().ShouldBe("failed");
        terminal.GetProperty("failure").GetProperty("code").GetString().ShouldBe("invalid_backend_binding");
    }

    [Fact]
    public async Task An_async_run_that_logs_then_fails_persists_the_log_and_the_failure()
    {
        var host = await fixture.EnsureAsync();
        fixture.Gate.Arm(gate: null);

        var body = new JsonObject
        {
            ["payload"] = JsonNode.Parse(
                """
                { "crawldad": "1", "name": "async.logfail", "config": { "backend": "input.backend" }, "vars": {},
                  "steps": [
                    { "goto": { "url": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx?module=Enforcement&TabName=Enforcement" } },
                    { "log": { "level": "warning", "message": "about to fail" } },
                    { "fail": { "class": "terminal", "code": "intentional_stop", "message": "stop here" } }
                  ],
                  "result": "'unreached'" }
                """),
            ["inputs"] = new JsonObject { ["backend"] = new JsonObject { ["adapter"] = "fake", ["options"] = new JsonObject { ["fixture"] = "caphome-resume" } } },
            ["async"] = true,
        };
        var (runId, _) = await StartAsyncAsync(host, body);

        var terminal = await DurableHost.PollUntilTerminalAsync(host, runId, TimeSpan.FromSeconds(20));
        terminal.GetProperty("status").GetString().ShouldBe("failed");
        terminal.GetProperty("failure").GetProperty("code").GetString().ShouldBe("intentional_stop");

        // The log the run emitted before failing is persisted (scrubbed) alongside the terminal failure.
        var types = await EventTypesAsync(host, runId);
        types.ShouldContain(typeof(LogEmitted));
        types.ShouldContain(typeof(RunFailed));
    }

    [Fact]
    public async Task A_cancel_whose_result_expression_faults_reports_no_partial()
    {
        var host = await fixture.EnsureAsync();
        var gate = new RunGate("pg=1");
        fixture.Gate.Arm(gate);

        var body = new JsonObject
        {
            ["payload"] = JsonNode.Parse(
                """
                { "crawldad": "1", "name": "cancel.badresult", "config": { "backend": "input.backend" }, "vars": { "more": true },
                  "steps": [
                    { "goto": { "url": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx?module=Enforcement&TabName=Enforcement&pg=1" } },
                    { "loop": { "maxIterations": 100000, "while": "more", "do": [
                        { "waitForRequest": { "urlPrefix": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx", "method": "POST",
                            "trigger": [ { "click": { "selector": "table.aca_pagination td:last-child a" } } ] } }
                    ] } }
                  ],
                  "result": "[1,2][9]" }
                """),
            ["inputs"] = new JsonObject { ["backend"] = new JsonObject { ["adapter"] = "fake", ["options"] = new JsonObject { ["fixture"] = "caphome-resume" } } },
            ["async"] = true,
        };
        var (runId, _) = await StartAsyncAsync(host, body);
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(20));

        await host.Scenario(x =>
        {
            x.Post.Json(new JsonObject()).ToUrl($"/runs/{runId}/cancel");
            x.StatusCodeShouldBe(202);
        });
        gate.Release();

        var terminal = await DurableHost.PollUntilTerminalAsync(host, runId, TimeSpan.FromSeconds(20));
        terminal.GetProperty("status").GetString().ShouldBe("cancelled");
        terminal.TryGetProperty("partial", out _).ShouldBeFalse(); // the out-of-range result expression salvaged no partial
    }

    // ----- executor idempotency guards ---------------------------------------

    [Fact]
    public async Task Executing_an_unknown_run_is_a_no_op()
    {
        var host = await fixture.EnsureAsync();
        var executor = host.Services.GetRequiredService<RunExecutor>();

        await executor.ExecuteAsync(Guid.NewGuid(), TestTenants.PrimaryId, CancellationToken.None); // no saga/progress — returns without effect
    }

    [Fact]
    public async Task Executing_a_run_with_no_tenant_is_a_no_op()
    {
        var host = await fixture.EnsureAsync();
        var executor = host.Services.GetRequiredService<RunExecutor>();

        // A message with no tenant cannot resolve a run (CD-1): the executor fails closed — it never touches the default
        // partition — and returns without effect (no crash).
        await executor.ExecuteAsync(Guid.NewGuid(), tenantId: null, CancellationToken.None);
    }

    [Fact]
    public async Task Executing_an_already_terminal_run_is_a_no_op()
    {
        var host = await fixture.EnsureAsync();
        var runId = Guid.NewGuid();
        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession(TestTenants.PrimaryId))
        {
            session.Store(new RunExecutorSaga { Id = runId, Script = "{}", Inputs = "{}" });
            session.Store(new RunProgress { Id = runId, Status = Crawldad.Contracts.Runs.RunStatus.Failed });
            await session.SaveChangesAsync();
        }

        // A redelivered ExecuteRun for a run already finished must not re-run it (idempotent recovery).
        await host.Services.GetRequiredService<RunExecutor>().ExecuteAsync(runId, TestTenants.PrimaryId, CancellationToken.None);
    }

    [Fact]
    public async Task A_run_already_claimed_in_this_process_is_not_driven_twice()
    {
        var host = await fixture.EnsureAsync();
        var runId = Guid.NewGuid();
        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession(TestTenants.PrimaryId))
        {
            session.Store(new RunExecutorSaga { Id = runId, Script = "{}", Inputs = "{}" });
            session.Store(new RunProgress { Id = runId, Status = Crawldad.Contracts.Runs.RunStatus.Running });
            await session.SaveChangesAsync();
        }

        var controls = host.Services.GetRequiredService<IRunControlRegistry>();
        controls.GetOrAdd(runId).TryClaim().ShouldBeTrue(); // pre-claim as if another executor is driving it

        // The executor finds the run claimed and returns without driving it (no crash from the empty "{}" script).
        await host.Services.GetRequiredService<RunExecutor>().ExecuteAsync(runId, TestTenants.PrimaryId, CancellationToken.None);
        controls.Remove(runId);
    }

    [Fact]
    public async Task A_run_interrupted_by_a_cancelled_handler_token_is_left_running_for_recovery()
    {
        var host = await fixture.EnsureAsync();
        var gate = new RunGate("pg=2");
        fixture.Gate.Arm(gate);

        // Seed a runnable run and drive it directly so the handler token can be cancelled mid-run (the honest interruption
        // the executor leaves un-finalised for the recovery scan — distinct from a deadline, which finalises).
        var runId = Guid.NewGuid();
        var inputs = new JsonObject
        {
            ["backend"] = new JsonObject { ["adapter"] = "fake", ["options"] = new JsonObject { ["fixture"] = "caphome-resume" } },
            ["startDate"] = "01/01/2024",
            ["endDate"] = "01/31/2024",
            ["knownUrls"] = new JsonArray(),
            ["priorCrawlComplete"] = false,
        };
        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession(TestTenants.PrimaryId))
        {
            session.Store(new RunExecutorSaga { Id = runId, Script = SearchPayload(), Inputs = inputs.ToJsonString() });
            session.Store(new RunProgress { Id = runId, Status = Crawldad.Contracts.Runs.RunStatus.Running });
            await session.SaveChangesAsync();
        }

        using var handler = new CancellationTokenSource();
        var drive = host.Services.GetRequiredService<RunExecutor>().ExecuteAsync(runId, TestTenants.PrimaryId, handler.Token);
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(20)); // blocked mid-crawl, past >= 1 checkpoint

        await handler.CancelAsync(); // the handler token fires (as on host shutdown) — NOT a deadline
        await drive; // returns without finalising

        await using var read = store.LightweightSession(TestTenants.PrimaryId);
        var progress = await read.LoadAsync<RunProgress>(runId);
        progress!.Status.ShouldBe(Crawldad.Contracts.Runs.RunStatus.Running); // left running so the recovery scan re-drives it
        progress.Checkpoint.ShouldNotBeNull(); // its durable checkpoint survived

        // CD-5 resume invariant: a non-finalised run is never deleted, so its saga (the script+inputs resume source) survives.
        (await read.LoadAsync<RunExecutorSaga>(runId)).ShouldNotBeNull();
    }

    [Fact]
    public async Task An_executor_run_with_non_object_inputs_falls_back_to_empty_inputs()
    {
        var host = await fixture.EnsureAsync();
        var runId = Guid.NewGuid();
        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession(TestTenants.PrimaryId))
        {
            // Inputs stored as JSON null (a shape the endpoint never emits) exercises the executor's empty-inputs fallback.
            session.Store(new RunExecutorSaga { Id = runId, Script = """{ "crawldad": "1", "name": "n", "config": { "backend": "input.backend" }, "steps": [], "result": "'ok'" }""", Inputs = "null" });
            session.Store(new RunProgress { Id = runId, Status = Crawldad.Contracts.Runs.RunStatus.Running });
            await session.SaveChangesAsync();
        }

        await host.Services.GetRequiredService<RunExecutor>().ExecuteAsync(runId, TestTenants.PrimaryId, CancellationToken.None);

        await using var read = store.LightweightSession(TestTenants.PrimaryId);
        var progress = await read.LoadAsync<RunProgress>(runId);
        progress!.Status.ShouldBe(Crawldad.Contracts.Runs.RunStatus.Failed); // empty inputs → no backend → terminal
        progress.Failure!.Code.ShouldBe("invalid_backend_binding");
    }

    // ----- saga completion at terminal (CD-5, §14.2) -------------------------

    private static async Task<RunExecutorSaga?> LoadSagaAsync(IAlbaHost host, Guid runId)
    {
        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(TestTenants.PrimaryId);
        return await session.LoadAsync<RunExecutorSaga>(runId);
    }

    // Drives one durable message through its real generated handler inline, under the run's tenant — so a redelivered StartRun
    // is loaded/handled/stored exactly as Wolverine would, with no wall-clock wait.
    private static async Task InvokeAsync(IAlbaHost host, object message)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeForTenantAsync(TestTenants.PrimaryId, message);
    }

    [Fact]
    public async Task A_finished_async_runs_saga_is_deleted_atomically_at_terminal()
    {
        fixture.Gate.Arm(gate: null);
        var host = await fixture.EnsureAsync();
        var (runId, _) = await StartAsyncAsync(host, SearchBody("caphome-multipage", async: true));

        (await DurableHost.PollUntilTerminalAsync(host, runId, TimeSpan.FromSeconds(20)))
            .GetProperty("status").GetString().ShouldBe("succeeded");

        // CD-5: the saga is deleted in the SAME transaction as the terminal disposition — no RunFinished, no deadline janitor,
        // just gone the instant the run reaches terminal (its deadline is 30 min away and never fires here).
        await DurableHost.WaitUntilSagaGoneAsync(host, runId, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_deadline_killed_runs_saga_is_gone_with_no_finish_signal_and_no_second_deadline()
    {
        // The reviewer's spent-deadline case (CD-5): a run its OWN deadline stopped — the deadline is already fired/acked and
        // cannot fire again, and there is no separate RunFinished — so the saga is reclaimed ONLY by the atomic delete in the
        // terminal transaction. The gate stalls the first postback so a short deadline forcibly caps the run (§8.4).
        var host = await fixture.EnsureAsync();
        fixture.Gate.Arm(new RunGate("CapHome"));
        var payload = JsonNode.Parse(
            """
            { "crawldad": "1", "name": "deadline.saga", "config": { "backend": "input.backend", "deadlineMs": 200 }, "vars": {},
              "steps": [
                { "goto": { "url": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx?module=Enforcement&TabName=Enforcement" } },
                { "waitForRequest": { "urlPrefix": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx", "method": "POST",
                    "trigger": [ { "click": { "selector": "#ctl00_PlaceHolderMain_btnNewSearch" } } ] } }
              ],
              "result": "'unreached'" }
            """);
        var body = new JsonObject
        {
            ["payload"] = payload,
            ["inputs"] = new JsonObject { ["backend"] = new JsonObject { ["adapter"] = "fake", ["options"] = new JsonObject { ["fixture"] = "caphome-resume" } } },
            ["async"] = true,
        };
        var (runId, _) = await StartAsyncAsync(host, body);

        (await DurableHost.PollUntilTerminalAsync(host, runId, TimeSpan.FromSeconds(30)))
            .GetProperty("failure").GetProperty("code").GetString().ShouldBe(RunExecutor.DeadlineExceededCode);

        await DurableHost.WaitUntilSagaGoneAsync(host, runId, TimeSpan.FromSeconds(10)); // reclaimed by the atomic delete alone
    }

    [Fact]
    public async Task A_redelivered_start_run_creates_no_second_saga_and_does_not_error()
    {
        var host = await fixture.EnsureAsync();
        var runId = Guid.NewGuid();
        var command = new StartRun(runId, "n", "redeliverhash",
            """{ "crawldad": "1", "name": "n", "config": { "backend": "input.backend" }, "steps": [], "result": "'ok'" }""",
            "{}", null, null, 90_000);

        await InvokeAsync(host, command);                 // first delivery — starts the saga
        (await LoadSagaAsync(host, runId)).ShouldNotBeNull();

        // The redelivery must be a genuine no-op: the load-first starter finds the existing saga and returns — no second saga,
        // no re-kicked executor, and NOT the DocumentAlreadyExistsException a straight Insert would throw (which would surface
        // out of this InvokeForTenantAsync and fail the test).
        await InvokeAsync(host, command);

        var saga = await LoadSagaAsync(host, runId);
        saga.ShouldNotBeNull();
        saga.Id.ShouldBe(runId);
        saga.ScriptHash.ShouldBe("redeliverhash");
    }
}
