using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Api.Features.Runs;
using Crawldad.Api.Infrastructure.Browser;
using Crawldad.Api.Infrastructure.Browser.Fake;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Tests.Support;
using Marten;
using Marten.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Crawldad.Tests.Integration;

/// <summary>Re-armable per-test behaviour for the ONE shared sync-cap backend: the gate that holds a run past the
/// window, plus two special modes — a run secret registered at connect (leak gate) and a raw fault at connect
/// (unexpected-fault gate). One shared host avoids three schema migrations under Postgres lock contention.</summary>
internal sealed class SyncCapArming
{
    public RunGate? Gate { get; private set; }

    public string? Secret { get; private set; }

    public bool FaultAtConnect { get; private set; }

    /// <summary>The most recently connected gated session — a hook for the cancel gate to assert a clean teardown.</summary>
    public GatedSession? LastSession { get; set; }

    public RunGate? ArmGate(RunGate? gate) => Reset(gate, secret: null, fault: false);

    public void ArmSecret(RunGate gate, string secret) => Reset(gate, secret, fault: false);

    public void ArmFault(RunGate gate) => Reset(gate, secret: null, fault: true);

    private RunGate? Reset(RunGate? gate, string? secret, bool fault)
    {
        Gate = gate;
        Secret = secret;
        FaultAtConnect = fault;
        LastSession = null;
        return gate;
    }
}

/// <summary>The single fake backend behind every sync-cap gate: it gates a run (via <see cref="SyncCapArming"/>) so it
/// crosses the sync window, and — when armed — registers a run secret at connect (mimicking a real adapter) or throws
/// a raw fault at connect (blocking at the gate first, so the fault lands after the 202, not before).</summary>
internal sealed class SyncCapBackend(string fixturesRoot, SyncCapArming arming, IRunSecretScope secretScope) : IBrowserBackend
{
    private readonly FakeBrowserBackend _inner = new(fixturesRoot);

    public async Task<IBrowserSession> ConnectAsync(BackendBinding binding, SessionPolicy policy, CancellationToken ct)
    {
        if (arming.Secret is { } secret)
        {
            secretScope.Register(secret); // mimic a real adapter registering the resolved credential into the run's scope
        }

        if (arming.FaultAtConnect)
        {
            await arming.Gate!.WaitAsync("fault", ct); // block past the window, then fault with a raw (non-modelled) exception
            throw new InvalidOperationException("simulated unexpected backend fault after auto-upgrade (CD-15 test)");
        }

        var session = new GatedSession(await _inner.ConnectAsync(binding, policy, ct), new PageFetchRecorder(), arming.Gate);
        arming.LastSession = session;
        return session;
    }
}

/// <summary>One shared host for the sync-cap gates: a small <c>SyncUpgradeThresholdMs</c> so a run the gate holds open
/// crosses the window and auto-upgrades deterministically (the frozen clock does not affect the real-time window). Built
/// lazily (like the other durable fixtures) and re-armed per test via its <see cref="SyncCapArming"/>.</summary>
public sealed class SyncCapFixture : IAsyncLifetime
{
    // A small real-time window: with a gate holding the run open indefinitely, ANY finite window elapses first — so the
    // upgrade is deterministic, never a wall-clock race. Task.Delay's timer is real, unaffected by the frozen FakeClock.
    internal const int ThresholdMs = 75;

    private IAlbaHost? _host;

    internal SyncCapArming Arming { get; } = new();

    public Task InitializeAsync() => Task.CompletedTask; // built lazily on first use

    internal async Task<IAlbaHost> EnsureAsync() =>
        _host ??= await DurableHost.BuildAsync(
            "crawldad_synccap",
            (sp, _) => new SyncCapBackend(Runner.FixturesRoot, Arming, sp.GetRequiredService<IRunSecretScope>()),
            settings: SyncCapTests.LowThreshold());

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class SyncCapCollection : ICollectionFixture<SyncCapFixture>
{
    public const string Name = "sync-cap";
}

/// <summary>The default synchronous <c>POST /runs</c> is capped at a wall-clock window and, on crossing it, is
/// auto-upgraded, not failed — the caller gets <c>202 { runId, status:"running" }</c> and the run keeps executing on
/// the async surface. Cancel, the deadline, SSE, credential scrubbing, and pinned/replay all hold across the upgrade.</summary>
[Collection(SyncCapCollection.Name)]
public class SyncCapTests(SyncCapFixture fixture)
{
    internal static IEnumerable<KeyValuePair<string, string?>> LowThreshold() =>
        [new("Crawldad:Limits:SyncUpgradeThresholdMs", SyncCapFixture.ThresholdMs.ToString(CultureInfo.InvariantCulture))];

    // ----- the core: cross the window → 202, then finish with the golden ------

    [Fact]
    public async Task A_sync_run_crossing_the_window_returns_202_running_then_completes_with_the_golden()
    {
        var host = await fixture.EnsureAsync();
        var gate = fixture.Arming.ArmGate(new RunGate("pg=2"))!;

        // A DEFAULT (async:false) POST the gate holds past the window is auto-upgraded — the exact async 202 body.
        var accepted = await host.Scenario(x =>
        {
            x.Post.Json(SearchBody("caphome-resume")).ToUrl("/runs");
            x.StatusCodeShouldBe(202);
        });
        var root = (await accepted.ReadAsJsonAsync<JsonElement>()).Clone();
        root.GetProperty("status").GetString().ShouldBe("running");
        root.TryGetProperty("result", out _).ShouldBeFalse(); // just { runId, status } at the moment of upgrade
        var runId = root.GetProperty("runId").GetGuid();

        // It is now on the async surface (a fast sync run 404s); GET reports it running while the gate still holds it.
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(20));
        var running = await host.Scenario(x =>
        {
            x.Get.Url($"/runs/{runId}");
            x.StatusCodeShouldBe(200);
        });
        (await running.ReadAsJsonAsync<JsonElement>()).GetProperty("status").GetString().ShouldBe("running");

        gate.Release();
        var terminal = await DurableHost.PollUntilTerminalAsync(host, runId, TimeSpan.FromSeconds(20));
        terminal.GetProperty("status").GetString().ShouldBe("succeeded");
        AssertMatchesFullGolden(terminal.GetProperty("result")); // the SAME terminal result a sync (or native async) run produces
    }

    [Fact]
    public async Task An_upgraded_run_replays_its_buffered_log_into_a_lean_stream_pollable_over_sse()
    {
        var host = await fixture.EnsureAsync();
        var gate = fixture.Arming.ArmGate(new RunGate("pg=1"))!;

        var runId = await UpgradeAsync(host, GatedLogBody("'upgraded-ok'"));
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(20));
        gate.Release();

        var terminal = await DurableHost.PollUntilTerminalAsync(host, runId, TimeSpan.FromSeconds(20));
        terminal.GetProperty("status").GetString().ShouldBe("succeeded");
        terminal.GetProperty("result").GetString().ShouldBe("upgraded-ok");

        // The lean synchronous engine buffered its coarse log (no observer); the supervisor replayed it into the stream
        // between RunStarted and the terminal event — the SAME lean shape a fast sync run persists, plus a pollable RunProgress.
        var types = await EventTypesAsync(host, runId);
        types.ShouldContain(typeof(LogEmitted));
        types[^1].ShouldBe(typeof(RunSucceeded));

        // SSE backfills the whole terminal stream and closes — the async observability surface works for an upgraded run.
        var frames = await SseReader.ReadToCloseAsync(host, runId, lastEventId: null, TimeSpan.FromSeconds(20));
        frames[0].Event.ShouldBe("RunStarted");
        frames.Select(f => f.Event).ShouldContain("LogEmitted");
        frames[^1].Event.ShouldBe("RunSucceeded");
    }

    // ----- pinned + replay crossing the window ------

    [Fact]
    public async Task A_pinned_sync_run_crossing_the_window_upgrades_and_completes_with_the_golden()
    {
        var host = await fixture.EnsureAsync();
        var payloadId = await DraftSearchPayloadAsync(host);

        var gate = fixture.Arming.ArmGate(new RunGate("pg=2"))!;

        // A PINNED (payloadId) sync run whose script is a JsonElement over a `using var JsonDocument` disposed at the 202: the
        // detached interpreter must read an owned clone, not the disposed document — else it faults with internal_error.
        var runId = await UpgradeAsync(host, new JsonObject { ["payloadId"] = payloadId, ["inputs"] = SearchInputs("caphome-resume") });
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(20));
        gate.Release();

        var terminal = await DurableHost.PollUntilTerminalAsync(host, runId, TimeSpan.FromSeconds(20));
        terminal.GetProperty("status").GetString().ShouldBe("succeeded");
        AssertMatchesFullGolden(terminal.GetProperty("result")); // the correct golden, NOT a terminal internal_error
    }

    [Fact]
    public async Task A_sync_replay_crossing_the_window_upgrades_and_completes_with_the_golden()
    {
        var host = await fixture.EnsureAsync();
        var payloadId = await DraftSearchPayloadAsync(host);

        // Establish a completed pinned run to replay — async, so the original is unaffected by the sync window.
        fixture.Arming.ArmGate(gate: null);
        var originalId = await StartAsyncToTerminalAsync(host, new JsonObject { ["payloadId"] = payloadId, ["inputs"] = SearchInputs("caphome-resume"), ["async"] = true });

        // Replay it SYNC: RunReplayEndpoint delegates to POST /runs with the pinned payloadId, so replay also parses the
        // stored script into a `using var JsonDocument` and hits the same detached-lifetime path — it must upgrade cleanly.
        var gate = fixture.Arming.ArmGate(new RunGate("pg=2"))!;
        var accepted = await host.Scenario(x =>
        {
            x.Post.Json(new JsonObject { ["inputs"] = SearchInputs("caphome-resume") }).ToUrl($"/runs/{originalId}/replay");
            x.StatusCodeShouldBe(202);
        });
        var replay = (await accepted.ReadAsJsonAsync<JsonElement>()).Clone();
        replay.GetProperty("status").GetString().ShouldBe("running");
        var replayId = replay.GetProperty("runId").GetGuid();

        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(20));
        gate.Release();

        var terminal = await DurableHost.PollUntilTerminalAsync(host, replayId, TimeSpan.FromSeconds(20));
        terminal.GetProperty("status").GetString().ShouldBe("succeeded");
        AssertMatchesFullGolden(terminal.GetProperty("result"));
    }

    // ----- the async surface holds across the upgrade: cancel + deadline ------

    [Fact]
    public async Task An_upgraded_run_can_be_cancelled()
    {
        var host = await fixture.EnsureAsync();
        var gate = fixture.Arming.ArmGate(new RunGate("pg=2"))!;

        var runId = await UpgradeAsync(host, SearchBody("caphome-resume"));
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(20));

        await host.Scenario(x =>
        {
            x.Post.Json(new JsonObject()).ToUrl($"/runs/{runId}/cancel");
            x.StatusCodeShouldBe(202);
        });
        // No gate.Release(): the cancel forcibly cancels the observer-less interpreter, unblocking it via its own token.

        var terminal = await DurableHost.PollUntilTerminalAsync(host, runId, TimeSpan.FromSeconds(20));
        terminal.GetProperty("status").GetString().ShouldBe("cancelled");
        terminal.TryGetProperty("partial", out _).ShouldBeFalse(); // a forcible cancel salvages no partial (no between-steps observer)
        fixture.Arming.LastSession!.Disposed.ShouldBeTrue();        // the run tore its (fake) session down cleanly
    }

    [Fact]
    public async Task An_upgraded_run_honours_the_wall_clock_deadline()
    {
        var host = await fixture.EnsureAsync();
        fixture.Arming.ArmGate(new RunGate("CapHome")); // stall the first postback so the run is stuck when the deadline fires

        // deadlineMs (300) is comfortably over the sync window (75): the run upgrades first, then the saga's RunDeadline caps it.
        var runId = await UpgradeAsync(host, DeadlineBody(deadlineMs: 300));

        var terminal = await DurableHost.PollUntilTerminalAsync(host, runId, TimeSpan.FromSeconds(30));
        terminal.GetProperty("status").GetString().ShouldBe("failed");
        var failure = terminal.GetProperty("failure");
        failure.GetProperty("class").GetString().ShouldBe("terminal");
        failure.GetProperty("code").GetString().ShouldBe(RunExecutor.DeadlineExceededCode);
    }

    // ----- an unexpected fault after the 202 becomes a terminal failure -------

    [Fact]
    public async Task An_upgraded_run_that_faults_unexpectedly_fails_terminally()
    {
        var host = await fixture.EnsureAsync();
        var gate = new RunGate("fault");
        fixture.Arming.ArmFault(gate);

        // The backend blocks in ConnectAsync until released, so the run crosses the window and upgrades; then it throws a raw
        // (non-modelled) exception, which the supervisor maps to a terminal internal_error rather than a stuck "running" run.
        var runId = await UpgradeAsync(host, MinimalBody("'unreached'"));
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(20));
        gate.Release();

        var terminal = await DurableHost.PollUntilTerminalAsync(host, runId, TimeSpan.FromSeconds(20));
        terminal.GetProperty("status").GetString().ShouldBe("failed");
        terminal.GetProperty("failure").GetProperty("code").GetString().ShouldBe(SyncRunSupervisor.InternalErrorCode);
    }

    // ----- the credential-scrubbing boundary holds across the request→background handoff -------

    [Fact]
    public async Task An_upgraded_run_keeps_a_run_secret_out_of_every_sink()
    {
        const string Secret = "sk-upgrade-canary-0123456789"; // >= the scrubber's exact-match floor
        var host = await fixture.EnsureAsync();
        var gate = new RunGate("pg=1");
        fixture.Arming.ArmSecret(gate, Secret); // the backend registers the secret at connect, then gates the pagination

        // The run is finalised by the background supervisor AFTER the 202, so this proves the run's ambient secret scope is
        // still in effect there — the request→background handoff cannot leak.
        var runId = await UpgradeAsync(host, EchoSecretBody(Secret));
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(20));
        gate.Release();

        var terminal = await DurableHost.PollUntilTerminalAsync(host, runId, TimeSpan.FromSeconds(20));
        terminal.GetProperty("status").GetString().ShouldBe("succeeded");
        terminal.GetProperty("result").GetProperty("echoed").GetString().ShouldBe(CredentialScrubber.Redaction); // scrubbed at finalisation

        // No sink leaks the secret: the response body, the persisted event stream, and the SSE frames are all clean.
        terminal.GetRawText().ShouldNotContain(Secret);
        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(TestTenants.PrimaryId);
        foreach (var e in await session.Events.FetchStreamAsync(runId))
        {
            JsonSerializer.Serialize(e.Data).ShouldNotContain(Secret);
        }

        var frames = await SseReader.ReadToCloseAsync(host, runId, lastEventId: null, TimeSpan.FromSeconds(20));
        string.Join('\n', frames.Select(f => f.Data)).ShouldNotContain(Secret);
    }

    // ----- host shutdown drains the in-flight supervised tail -----------

    [Fact]
    public async Task Host_shutdown_drains_an_in_flight_upgraded_run_without_a_disposed_service_fault()
    {
        // A DEDICATED host on its own schema: this test drives the supervisor's shutdown drain, so it must not share the
        // collection's one long-lived host. A gate holds the run past the sync window so it auto-upgrades and is adopted.
        var holder = new GateHolder();
        var gate = new RunGate("pg=2");
        holder.Arm(gate);
        await using var host = await DurableHost.BuildAsync(
            "crawldad_synccap_drain", new GatedFakeBackend(Runner.FixturesRoot, holder), settings: LowThreshold());

        // A default (async:false) run the gate holds past the window → 202 running, adopted by the supervisor and now
        // provably mid-execution (blocked at the pagination gate) — its finalisation tail is exactly what a fire-and-forget
        // would race against host disposal.
        var runId = await UpgradeAsync(host, SearchBody("caphome-resume"));
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(20));

        // Invoke the supervisor's shutdown drain — the exact call the host makes on StopAsync — WHILE the tail is in flight.
        // It blocks on the gated interpreter, so kick it without awaiting, then release the gate so the run finalises INSIDE
        // the drain window: the tail commits through the still-live singleton store, and StopAsync returns only once it is done.
        var supervisor = host.Services.GetRequiredService<SyncRunSupervisor>();
        var drain = supervisor.StopAsync(CancellationToken.None);
        gate.Release();
        await drain; // returns cleanly — the drain awaited the tail while the store/bus were live, so no ObjectDisposedException

        // The tail finalised during the drain: a single GET (no poll) shows the golden terminal state, durably persisted.
        // The single read is honest because StopAsync now OBSERVES each tail rather than awaiting a WhenAny that returns
        // whether the drain finished, faulted, or timed out (issue #167) — and a tail that ends at all ends TERMINAL, since
        // a finalisation that cannot commit falls back to internal_error. The queue-promotion nudge was skipped (durably
        // recovered), not raced onto a stopping bus. The host itself was not stopped, so its endpoints still answer.
        var state = await StateAsync(host, runId);
        state.GetProperty("status").GetString().ShouldBe("succeeded");
        AssertMatchesFullGolden(state.GetProperty("result"));
    }


    // ----- a finalisation that cannot commit still ends the run -------------

    [Fact]
    public async Task An_upgraded_run_whose_finalisation_cannot_commit_still_ends_terminally()
    {
        // The mechanism behind issue #167: an upgraded run has already returned 202, so a fault in its finalisation TAIL
        // (a null progress row, a Marten concurrency/stream-lock failure, an Npgsql fault at commit, a disposed dependency
        // during teardown) must not leave it stuck `running` on the async surface. Driven WITHOUT threads (reference #14):
        // a one-shot Marten session listener throws out of the supervisor's own finalisation commit, exactly as a real
        // store fault would, so the retry path is exercised deterministically.
        var holder = new GateHolder();
        var gate = new RunGate("pg=2");
        holder.Arm(gate);
        var injector = new FinalizeCommitInjector(oneShot: true);
        var logs = new CapturingLoggerProvider();
        await using var host = await DurableHost.BuildAsync(
            "crawldad_synccap_finalfault", new GatedFakeBackend(Runner.FixturesRoot, holder), settings: LowThreshold(),
            configureServices: services =>
            {
                services.ConfigureMarten(options => options.Listeners.Add(injector));
                services.AddSingleton<ILoggerProvider>(logs);
            });

        var runId = await UpgradeAsync(host, SearchBody("caphome-resume"));
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(20));
        injector.Arm(runId);

        var supervisor = host.Services.GetRequiredService<SyncRunSupervisor>();
        var drain = supervisor.StopAsync(CancellationToken.None);
        gate.Release();
        await drain;

        injector.Fired.ShouldBe(1); // the supervisor's real finalisation commit was the save that threw

        // The run is TERMINAL, not stuck running: the golden result could not be committed, so the tail fell back to the
        // same internal_error disposition an unexpected execution fault produces — asserted with a single GET, since the
        // observed drain is what guarantees the tail is already done.
        var state = await StateAsync(host, runId);
        state.GetProperty("status").GetString().ShouldBe("failed");
        state.GetProperty("failure").GetProperty("code").GetString().ShouldBe(SyncRunSupervisor.InternalErrorCode);

        // And the operator can see WHY that run lost its result — the fault is logged at Error against its id, through the
        // host's scrubbing logger like every other line.
        logs.Lines.ShouldContain(line =>
            line.Contains("Finalising upgraded run", StringComparison.Ordinal) && line.Contains(runId.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_drain_whose_tail_faults_reports_the_run_it_left_for_recovery()
    {
        // The last-resort branch: the store is unusable, so even the minimal internal_error commit throws (the injector
        // never disarms). The tail then faults — and the drain must SAY so rather than return "clean" while the run is
        // still running, which is exactly how #167 hid.
        var holder = new GateHolder();
        var gate = new RunGate("pg=2");
        holder.Arm(gate);
        var injector = new FinalizeCommitInjector(oneShot: false);
        var logs = new CapturingLoggerProvider();
        await using var host = await DurableHost.BuildAsync(
            "crawldad_synccap_drainfault", new GatedFakeBackend(Runner.FixturesRoot, holder), settings: LowThreshold(),
            configureServices: services =>
            {
                services.ConfigureMarten(options => options.Listeners.Add(injector));
                services.AddSingleton<ILoggerProvider>(logs);
            });

        var runId = await UpgradeAsync(host, SearchBody("caphome-resume"));
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(20));
        injector.Arm(runId);

        var supervisor = host.Services.GetRequiredService<SyncRunSupervisor>();
        var drain = supervisor.StopAsync(CancellationToken.None);
        gate.Release();
        await drain; // still does not throw: a hosted service that throws from stop aborts the rest of shutdown

        injector.Fired.ShouldBe(2); // both the real finalisation and its internal_error retry reached their commit

        // The drain named the run it could not finish, at Error, with the recovery it depends on.
        logs.Lines.ShouldContain(line =>
            line.Contains("faulted while draining", StringComparison.Ordinal) && line.Contains(runId.ToString(), StringComparison.Ordinal));

        // That end state is one the next host REPAIRS rather than strands: the failed commits rolled back, so the run is
        // still `running` WITH its executor saga intact — precisely what RunRecoveryService re-publishes ExecuteRun for.
        (await StateAsync(host, runId)).GetProperty("status").GetString().ShouldBe("running");
        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(TestTenants.PrimaryId);
        (await session.LoadAsync<RunExecutorSaga>(runId)).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_drain_that_times_out_names_the_run_it_leaves_running()
    {
        // The other silent path: the drain window elapses with a tail still in flight. Deterministic without a sleep —
        // the gate is NEVER released while the drain runs, so the tail provably cannot finish, and a short configured
        // ShutdownDrainMs bounds the wait instead of the 15 s production default.
        var holder = new GateHolder();
        var gate = new RunGate("pg=2");
        holder.Arm(gate);
        var logs = new CapturingLoggerProvider();
        await using var host = await DurableHost.BuildAsync(
            "crawldad_synccap_drainslow", new GatedFakeBackend(Runner.FixturesRoot, holder),
            settings: [.. LowThreshold(), new KeyValuePair<string, string?>("Crawldad:Limits:ShutdownDrainMs", "150")],
            configureServices: services => services.AddSingleton<ILoggerProvider>(logs));

        var runId = await UpgradeAsync(host, SearchBody("caphome-resume"));
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(20));

        var supervisor = host.Services.GetRequiredService<SyncRunSupervisor>();
        await supervisor.StopAsync(CancellationToken.None); // the gate still holds the tail: the window must elapse

        // The stranded run is named at Warning with the budget that elapsed, so an operator can see which run the host
        // walked away from — and the run is still `running`, for the next host's recovery scan to re-drive.
        logs.Lines.ShouldContain(line =>
            line.Contains("still in flight when the", StringComparison.Ordinal) && line.Contains(runId.ToString(), StringComparison.Ordinal));
        (await StateAsync(host, runId)).GetProperty("status").GetString().ShouldBe("running");

        gate.Release(); // let the tail unwind so the host tears down without an orphaned interpreter
        await DurableHost.PollUntilTerminalAsync(host, runId, DurableHost.PollTimeout);
    }

    // Throws out of the supervisor's own finalisation commit — identified by the terminal event staged on the target
    // run's stream — either once (the store recovers, so the internal_error retry commits) or every time (the store is
    // gone). Every other save in the host passes straight through, so the injection needs no threads and no real fault.
    private sealed class FinalizeCommitInjector(bool oneShot) : DocumentSessionListenerBase
    {
        private Guid _runId;
        private int _fired;

        public int Fired => Volatile.Read(ref _fired);

        public void Arm(Guid runId) => _runId = runId;

        public override Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(session);
            var finalises = session.PendingChanges.Streams()
                .Any(s => s.Id == _runId && s.Events.Any(e => e.Data is RunSucceeded or RunFailed or RunCancelled));
            if (!finalises || (oneShot && Fired == 1))
            {
                return Task.CompletedTask;
            }

            Interlocked.Increment(ref _fired);
            throw new InvalidOperationException("the finalisation commit could not reach the store");
        }
    }

    // ----- helpers -----------------------------------------------------------

    private static async Task<Guid> UpgradeAsync(IAlbaHost host, JsonObject body)
    {
        var accepted = await host.Scenario(x =>
        {
            x.Post.Json(body).ToUrl("/runs");
            x.StatusCodeShouldBe(202);
        });
        var root = (await accepted.ReadAsJsonAsync<JsonElement>()).Clone();
        root.GetProperty("status").GetString().ShouldBe("running"); // the auto-upgrade 202, not a queued 202
        return root.GetProperty("runId").GetGuid();
    }

    private static async Task<JsonElement> StateAsync(IAlbaHost host, Guid runId)
    {
        var result = await host.Scenario(x =>
        {
            x.Get.Url($"/runs/{runId}");
            x.StatusCodeShouldBe(200);
        });
        return (await result.ReadAsJsonAsync<JsonElement>()).Clone();
    }

    private static async Task<Guid> DraftSearchPayloadAsync(IAlbaHost host)
    {
        var drafted = await host.Scenario(x =>
        {
            x.Post.Json(new JsonObject { ["payload"] = JsonNode.Parse(File.ReadAllText(Path.Combine(Runner.FixturesRoot, "Payloads", "search-full.json"))) }).ToUrl("/payloads");
            x.StatusCodeShouldBe(200);
        });
        return (await drafted.ReadAsJsonAsync<JsonElement>()).GetProperty("payloadId").GetGuid();
    }

    private static async Task<Guid> StartAsyncToTerminalAsync(IAlbaHost host, JsonObject body)
    {
        var accepted = await host.Scenario(x =>
        {
            x.Post.Json(body).ToUrl("/runs");
            x.StatusCodeShouldBe(202);
        });
        var runId = (await accepted.ReadAsJsonAsync<JsonElement>()).GetProperty("runId").GetGuid();
        await DurableHost.PollUntilTerminalAsync(host, runId, TimeSpan.FromSeconds(20));
        return runId;
    }

    private static JsonObject SearchInputs(string fixtureDir) => new()
    {
        ["backend"] = FakeBackend(fixtureDir),
        ["startDate"] = "01/01/2024",
        ["endDate"] = "01/31/2024",
        ["knownUrls"] = new JsonArray(),
        ["priorCrawlComplete"] = false,
    };

    private static JsonObject SearchBody(string fixtureDir) => new()
    {
        ["payload"] = JsonNode.Parse(File.ReadAllText(Path.Combine(Runner.FixturesRoot, "Payloads", "search-full.json"))),
        ["inputs"] = SearchInputs(fixtureDir),
    };

    private static JsonObject GatedLogBody(string result) => new()
    {
        ["payload"] = JsonNode.Parse(
            $$"""
            { "crawldad": "1", "name": "synccap.upgrade.log", "config": { "backend": "input.backend" }, "vars": {},
              "steps": [
                { "goto": { "url": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx?module=Enforcement&TabName=Enforcement&pg=1" } },
                { "log": { "level": "info", "message": "crossing the sync cap" } },
                { "waitForRequest": { "urlPrefix": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx", "method": "POST",
                    "trigger": [ { "click": { "selector": "table.aca_pagination td:last-child a" } } ] } }
              ],
              "result": {{JsonSerializer.Serialize(result)}} }
            """),
        ["inputs"] = new JsonObject { ["backend"] = FakeBackend("caphome-resume") },
    };

    private static JsonObject EchoSecretBody(string secret) => new()
    {
        ["payload"] = JsonNode.Parse(
            """
            { "crawldad": "1", "name": "synccap.leak", "config": { "backend": "input.backend" }, "vars": {},
              "steps": [
                { "goto": { "url": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx?module=Enforcement&TabName=Enforcement&pg=1" } },
                { "log": { "level": "warning", "message": "echoing ${input.secret}" } },
                { "waitForRequest": { "urlPrefix": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx", "method": "POST",
                    "trigger": [ { "click": { "selector": "table.aca_pagination td:last-child a" } } ] } }
              ],
              "result": "{ echoed: input.secret }" }
            """),
        ["inputs"] = new JsonObject { ["backend"] = FakeBackend("caphome-resume"), ["secret"] = secret },
    };

    private static JsonObject DeadlineBody(int deadlineMs) => new()
    {
        ["payload"] = JsonNode.Parse(
            $$"""
            { "crawldad": "1", "name": "synccap.deadline", "config": { "backend": "input.backend", "deadlineMs": {{deadlineMs}} }, "vars": {},
              "steps": [
                { "goto": { "url": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx?module=Enforcement&TabName=Enforcement" } },
                { "waitForRequest": { "urlPrefix": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx", "method": "POST",
                    "trigger": [ { "click": { "selector": "#ctl00_PlaceHolderMain_btnNewSearch" } } ] } }
              ],
              "result": "'unreached'" }
            """),
        ["inputs"] = new JsonObject { ["backend"] = FakeBackend("caphome-resume") },
    };

    private static JsonObject MinimalBody(string result) => new()
    {
        ["payload"] = JsonNode.Parse(
            $$"""
            { "crawldad": "1", "name": "synccap.minimal", "config": { "backend": "input.backend" }, "vars": [], "steps": [], "result": {{JsonSerializer.Serialize(result)}} }
            """),
        ["inputs"] = new JsonObject { ["backend"] = FakeBackend("caphome-resume") },
    };

    private static JsonObject FakeBackend(string fixtureDir) =>
        new() { ["adapter"] = "fake", ["options"] = new JsonObject { ["fixture"] = fixtureDir } };

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
}
