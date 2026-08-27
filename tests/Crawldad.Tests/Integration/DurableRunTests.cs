using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Api.Features.Runs;
using Crawldad.Api.Features.Runs.Interpreter;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;

namespace Crawldad.Tests.Integration;

/// <summary>One shared background-executor host for the async/cancel/deadline gates, built once (and lazily, after
/// the eager collection fixtures' inits, so schema migrations don't contend). Its <c>fake</c> backend reads a
/// re-armable <see cref="GateHolder"/> so each test drives its own gate + fetch recorder on the one host.</summary>
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

/// <summary>The durable-execution gates: the async control surface (<c>202</c> + <c>GET /runs/{id}</c> poll),
/// cooperative cancellation with a clean teardown, checkpoint resume across an honest host kill, and the wall-clock
/// deadline — all driven through the real <c>POST /runs</c> path against the record/replay fake, no live traffic.</summary>
[Collection(DurableCollection.Name)]
public class DurableRunTests(DurableFixture fixture)
{
    private static string SearchPayload() => File.ReadAllText(Path.Combine(Runner.FixturesRoot, "Payloads", "search-full.json"));

    // A captured detail-page URL carrying a `token=`-shaped param that is NOT a run secret — the customer's own extracted
    // content. The full `Scrub` would param-redact it; the checkpoint's ScrubJson posture must leave it verbatim (issue #82).
    private const string _capturedTokenUrl = "https://aca-prod.accela.com/LJCMG/Cap/CapDetail.aspx?token=abc123SHAPEDtoken&capId=24ENF-1";

    // The search crawl, but carrying _capturedTokenUrl in a var it shapes straight into the result. The var rides the
    // durable checkpoint's snapshot, so a resumed run restores it into the result — the exact path issue #82 is about.
    private static string CapturedVarPayload()
    {
        var payload = JsonNode.Parse(SearchPayload())!;
        payload["vars"]!["captured"] = $"'{_capturedTokenUrl}'"; // a string-literal expression: the extracted URL, set once, never reassigned
        payload["result"] = "{ captured: captured }";            // shape the restored var straight into the result
        return payload.ToJsonString();
    }

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

    // ----- cooperative cancellation ------------------------------------

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

    // ----- #108: the running-branch cancel append survives a concurrent executor append -----

    [Fact]
    public async Task Cancel_retries_its_record_when_the_executor_races_the_same_stream()
    {
        // The executor is a lock-free writer that appends trace events to a run's stream while it executes. This test forces
        // the exact race #108 fixes — a concurrent append landing between the cancel's optimistic version read and its commit
        // — deterministically, via a one-shot Marten listener, rather than relying on timing or test-host parallelism.
        var injector = new StreamVersionRaceInjector();
        await using var host = await DurableHost.BuildAsync(
            "crawldad_cancel_race_retry", new GatedFakeBackend(Runner.FixturesRoot, new GateHolder()),
            configureServices: services => services.ConfigureMarten(options => options.Listeners.Add(injector)));
        var store = host.Services.GetRequiredService<IDocumentStore>();
        var clock = host.Services.GetRequiredService<TimeProvider>();

        var runId = Guid.NewGuid();
        await SeedRunningRunAsync(store, clock, runId);

        // On the cancel's first append attempt, a competitor bumps the SAME stream (as a live trace append would). The run
        // stays running, so the cancel re-reads it and lands the breadcrumb on the next, uncontended attempt.
        injector.Arm(store, runId, (competing, _) =>
        {
            competing.Events.Append(runId, new LogEmitted("info", "concurrent step", clock.GetUtcNow()));
            return Task.CompletedTask;
        });

        await CancelViaHttpAsync(host, runId); // 202, not the 500 the unguarded append raised on the losing side

        injector.Fired.ShouldBeTrue(); // the race really happened — the first append attempt conflicted
        var types = await EventTypesAsync(host, runId);
        types.ShouldContain(typeof(LogEmitted));               // the competitor's version bump
        types.ShouldContain(typeof(RunCancellationRequested)); // ...and the cancel breadcrumb still landed, on the retry
    }

    [Fact]
    public async Task Cancel_records_nothing_when_the_run_reaches_terminal_between_attempts()
    {
        // Same forced race, but the competitor drives the run to terminal through the REAL RunFinalization path — the exact
        // plain-Append finaliser that races the cancel record on the sync-upgrade side of #108 (PR-review finding #4), not a
        // hand-rolled stand-in. The cancel's retry must then observe the terminal run and record nothing, so an
        // already-finished run is never re-annotated or double-terminated.
        var injector = new StreamVersionRaceInjector();
        await using var host = await DurableHost.BuildAsync(
            "crawldad_cancel_race_terminal", new GatedFakeBackend(Runner.FixturesRoot, new GateHolder()),
            configureServices: services => services.ConfigureMarten(options => options.Listeners.Add(injector)));
        var store = host.Services.GetRequiredService<IDocumentStore>();
        var clock = host.Services.GetRequiredService<TimeProvider>();

        var runId = Guid.NewGuid();
        await SeedRunningRunAsync(store, clock, runId);

        // Materialise the saga's table before arming the injector (issue #114). The competitor runs the REAL finaliser,
        // whose terminal transaction Deletes the run's RunExecutorSaga — but that table is NOT part of the host's dev-time
        // startup schema apply (Wolverine registers the saga's Marten storage lazily, off the configured-schema set, so
        // neither ApplyAllDatabaseChangesOnStartup nor ApplyAllConfiguredChangesToDatabaseAsync builds it). It is otherwise
        // first bootstrapped only when a session touches the saga type — and here that first touch is the injector's
        // out-of-band competitor session, firing from inside the endpoint's commit, which can race Marten's on-demand
        // schema bootstrap and surface Postgres 42P01 (relation does not exist) as a spurious 500 where the test expects
        // 202. Forcing the table up-front makes the competitor always find it present. It is orthogonal to the stream-
        // version conflict this test relies on: it creates only the saga's empty table — no saga row, no stream event, no
        // version bump — so the #108 optimistic-append race still genuinely fires (asserted by injector.Fired below).
        await store.Storage.Database.EnsureStorageExistsAsync(typeof(RunExecutorSaga));

        // The real finaliser's collaborators; Release/Delete on this un-occupied, saga-less seed are safe no-ops.
        var scrubber = host.Services.GetRequiredService<CredentialScrubber>();
        var gate = host.Services.GetRequiredService<IRunAdmissionGate>();
        injector.Arm(store, runId, async (competing, token) =>
        {
            var progress = (await competing.LoadAsync<RunProgress>(runId, token))!;
            var cancelled = new RunOutcome(RunStatus.Cancelled, null, null, null, new RunStats(0, 0, 0, 0, 0, 0), []);
            RunFinalization.Apply(competing, runId, TestTenants.PrimaryId, cancelled, RunStopReason.Cancelled, progress, scrubber, gate, clock);
        });

        await CancelViaHttpAsync(host, runId); // still 202 — an already-terminal run is a no-op, not a 500

        injector.Fired.ShouldBeTrue();
        var types = await EventTypesAsync(host, runId);
        types.ShouldContain(typeof(RunCancelled));                // the terminal event the real finaliser wrote
        types.ShouldNotContain(typeof(RunCancellationRequested)); // the retry saw terminal and re-annotated nothing
        (await StateAsync(host, runId)).GetProperty("status").GetString().ShouldBe("cancelled");
    }

    // Seeds a running run directly (its stream + RunProgress) with no executor driving it, so the cancel endpoint's append
    // is the only writer — save for the conflict the test injects — mirroring SlotQueueTests' white-box run seeding.
    private static async Task SeedRunningRunAsync(IDocumentStore store, TimeProvider clock, Guid runId)
    {
        await using var session = store.LightweightSession(TestTenants.PrimaryId);
        session.Events.StartStream<Run>(runId, new RunStarted("cancel.race", "hash", clock.GetUtcNow(), [], null, null));
        session.Store(new RunProgress { Id = runId, Status = RunStatus.Running });
        await session.SaveChangesAsync();
    }

    private static async Task CancelViaHttpAsync(IAlbaHost host, Guid runId) =>
        await host.Scenario(x =>
        {
            x.Post.Json(new JsonObject()).ToUrl($"/runs/{runId}/cancel");
            x.StatusCodeShouldBe(202);
        });

    private static async Task<JsonElement> StateAsync(IAlbaHost host, Guid runId)
    {
        var result = await host.Scenario(x =>
        {
            x.Get.Url($"/runs/{runId}");
            x.StatusCodeShouldBe(200);
        });
        return (await result.ReadAsJsonAsync<JsonElement>()).Clone();
    }

    // A one-shot Marten session listener that forces the #108 stream-version race deterministically: when a session is about
    // to commit a RunCancellationRequested for the armed run, it first advances that SAME stream from another session. The
    // endpoint's append uses AppendOptimistic, whose expected version is captured BEFORE this pre-commit hook runs, so the
    // injected advance is always seen as a conflict at commit — no timing or parallelism involved. Fires exactly once, so the
    // cancel's retry then proceeds uncontended.
    private sealed class StreamVersionRaceInjector : DocumentSessionListenerBase
    {
        private IDocumentStore _store = null!;
        private Func<IDocumentSession, CancellationToken, Task> _compete = null!;
        private int _fired;

        public Guid RunId { get; private set; }

        public bool Fired => Volatile.Read(ref _fired) == 1;

        public void Arm(IDocumentStore store, Guid runId, Func<IDocumentSession, CancellationToken, Task> compete)
        {
            _store = store;
            RunId = runId;
            _compete = compete;
        }

        public override async Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
        {
            if (Volatile.Read(ref _fired) == 1)
            {
                return; // one-shot: the competing session's own commit (and every later save) re-enters here — do nothing
            }

            var racesCancel = session.PendingChanges.Streams().Any(s => s.Id == RunId && s.Events.Any(e => e.Data is RunCancellationRequested));
            if (!racesCancel || Interlocked.Exchange(ref _fired, 1) == 1)
            {
                return;
            }

            await using var competing = _store.LightweightSession(session.TenantId);
            await _compete(competing, token);
            await competing.SaveChangesAsync(token); // commits before the endpoint's guarded append executes -> its optimistic guard trips
        }
    }

    // ----- wall-clock deadline ----------------------------------------

    [Fact]
    public async Task A_run_that_outruns_its_deadline_fails_terminally()
    {
        fixture.Gate.Arm(new RunGate("CapHome")); // stall the very first postback so the run is stuck when the deadline fires

        // A minimal payload whose one postback the gate stalls; a short deadline then forcibly caps it.
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

    // ----- kill-and-restart resume (the load-bearing gate) ----------

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

    // ----- checkpointed vars are not param-scrubbed (issue #82) --------------

    [Fact]
    public async Task A_checkpointed_token_shaped_var_survives_resume_into_the_result_uncorrupted()
    {
        var host = await fixture.EnsureAsync();
        var gate = new RunGate("pg=2");
        fixture.Gate.Arm(gate);

        // Seed a runnable run whose crawl carries a `token=`-shaped captured URL in a var, and drive it directly so the
        // handler token can honestly interrupt it mid-crawl (as host shutdown does) once the durable checkpoint is written.
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
            session.Store(new RunExecutorSaga { Id = runId, Script = CapturedVarPayload(), Inputs = inputs.ToJsonString() });
            session.Store(new RunProgress { Id = runId, Status = Crawldad.Contracts.Runs.RunStatus.Running });
            await session.SaveChangesAsync();
        }

        using var handler = new CancellationTokenSource();
        var executor = host.Services.GetRequiredService<RunExecutor>();
        var drive = executor.ExecuteAsync(runId, TestTenants.PrimaryId, handler.Token);
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(20)); // blocked mid-crawl, past >= 1 checkpoint
        await handler.CancelAsync();                            // honest interruption — leaves the run running for recovery
        await drive;

        // The durable checkpoint stored the TRUE value, not `token=[redacted]`: the var snapshot is scrubbed through the
        // result-channel posture (exact-secret only, no param rule), so the customer's own extracted content is intact (#82).
        await using (var read = store.LightweightSession(TestTenants.PrimaryId))
        {
            var interrupted = await read.LoadAsync<RunProgress>(runId);
            interrupted!.Status.ShouldBe(Crawldad.Contracts.Runs.RunStatus.Running);
            interrupted.Checkpoint.ShouldNotBeNull();
            using var snapshot = JsonDocument.Parse(interrupted.Checkpoint.VarsJson);
            snapshot.RootElement.GetProperty("captured").GetString().ShouldBe(_capturedTokenUrl); // survived verbatim (not token=[redacted])
            interrupted.Checkpoint.VarsJson.ShouldNotContain(CredentialScrubber.Redaction);        // never param-redacted on the checkpoint
        }

        // Resume on the same durable host (a fresh executor drive, as startup recovery would): it restores the var snapshot
        // and shapes it into the result. The resumed run must succeed with the TRUE captured value — not a corrupted one.
        fixture.Gate.Arm(gate: null); // run straight through to completion this time
        (await executor.ExecuteAsync(runId, TestTenants.PrimaryId, CancellationToken.None)).ShouldBeTrue(); // reached terminal

        await using (var read = store.LightweightSession(TestTenants.PrimaryId))
        {
            var finished = await read.LoadAsync<RunProgress>(runId);
            finished!.Status.ShouldBe(Crawldad.Contracts.Runs.RunStatus.Succeeded);
            using var result = JsonDocument.Parse(finished.ResultJson!);
            result.RootElement.GetProperty("captured").GetString().ShouldBe(_capturedTokenUrl); // NOT token=[redacted]
        }
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

        // A message with no tenant cannot resolve a run: the executor fails closed — it never touches the default
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

        // A non-finalised run is never deleted, so its saga (the script+inputs resume source) survives.
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

    // ----- saga completion at terminal -------------------------

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

        // The saga is deleted in the SAME transaction as the terminal disposition — no RunFinished, no deadline janitor,
        // just gone the instant the run reaches terminal (its deadline is 30 min away and never fires here).
        await DurableHost.WaitUntilSagaGoneAsync(host, runId, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_deadline_killed_runs_saga_is_gone_with_no_finish_signal_and_no_second_deadline()
    {
        // The spent-deadline case: a run its OWN deadline stopped has already fired/acked that deadline (it cannot fire
        // again), and there is no separate RunFinished — so the saga is reclaimed ONLY by the atomic delete in the terminal
        // transaction. The gate stalls the first postback so a short deadline forcibly caps the run.
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
