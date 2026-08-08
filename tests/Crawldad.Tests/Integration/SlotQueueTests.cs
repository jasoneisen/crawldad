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

/// <summary>
/// CD-16 (#16) slot admission queue end-to-end: at the per-tenant concurrent-run cap a run is <b>queued, not 429'd</b>
/// (docs/PRODUCT.md §Pv.3). Each test builds its own cap-1 durable host (its own schema + fresh in-process slot gate), so the
/// scenarios are fully isolated. Drives the real <c>POST /runs</c> + executor saga against the record/replay fake — no
/// Chromium, no live traffic. Covers the ticket's done-when matrix: FIFO fill→queue→auto-start ordering, queue-depth 429,
/// cancel-while-queued, deadline-starts-at-execution, max-queue-wait timeout, crash/restart with a non-empty queue (durable,
/// FIFO), queue position via GET + SSE, and p95 queue wait observability — plus the queue service's edge branches.
/// </summary>
public class SlotQueueTests
{
    // A minimal async payload with one gated postback: it blocks at the CapHome page so a run can be caught mid-execution
    // (holding its slot), and once the gate is released it runs straight through to a 'done' result.
    private static JsonObject Body(string? extraConfig = null) => new()
    {
        ["payload"] = JsonNode.Parse($$"""
            { "crawldad": "1", "name": "slotqueue.demo", "config": { "backend": "input.backend"{{(extraConfig is null ? "" : ", " + extraConfig)}} }, "vars": {},
              "steps": [
                { "goto": { "url": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx?module=Enforcement&TabName=Enforcement" } },
                { "waitForRequest": { "urlPrefix": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx", "method": "POST",
                    "trigger": [ { "click": { "selector": "#ctl00_PlaceHolderMain_btnNewSearch" } } ] } },
                { "log": { "level": "info", "message": "done" } }
              ],
              "result": "'done'" }
            """),
        ["inputs"] = new JsonObject { ["backend"] = new JsonObject { ["adapter"] = "fake", ["options"] = new JsonObject { ["fixture"] = "caphome-resume" } } },
        ["async"] = true,
    };

    private static IEnumerable<KeyValuePair<string, string?>> Settings(params (string Key, string Value)[] extra)
    {
        yield return new("Crawldad:Limits:MaxConcurrentRunsPerTenant", "1");
        foreach (var (key, value) in extra)
        {
            yield return new(key, value);
        }
    }

    private static Task<IAlbaHost> HostAsync(string schema, GateHolder holder, params (string, string)[] extra) =>
        DurableHost.BuildAsync(schema, new GatedFakeBackend(Runner.FixturesRoot, holder), settings: Settings(extra));

    // Arms the gate, starts an async run, and waits until it is provably blocked mid-execution (its one slot held).
    private static async Task<Guid> StartBlockedAsync(IAlbaHost host, GateHolder holder, RunGate gate)
    {
        holder.Arm(gate);
        var accepted = await host.Scenario(x =>
        {
            x.Post.Json(Body()).ToUrl("/runs");
            x.StatusCodeShouldBe(202);
        });
        var root = (await accepted.ReadAsJsonAsync<JsonElement>()).Clone();
        root.GetProperty("status").GetString().ShouldBe("running");
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(20));
        return root.GetProperty("runId").GetGuid();
    }

    // Starts an async run expected to be QUEUED at the cap (202 status:"queued"); returns its id and reported position.
    private static async Task<(Guid Id, int Position)> StartQueuedAsync(IAlbaHost host)
    {
        var accepted = await host.Scenario(x =>
        {
            x.Post.Json(Body()).ToUrl("/runs");
            x.StatusCodeShouldBe(202);
        });
        var root = (await accepted.ReadAsJsonAsync<JsonElement>()).Clone();
        root.GetProperty("status").GetString().ShouldBe("queued");
        return (root.GetProperty("runId").GetGuid(), root.GetProperty("position").GetInt32());
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

    private static async Task CancelAsync(IAlbaHost host, Guid runId) =>
        await host.Scenario(x =>
        {
            x.Post.Json(new JsonObject()).ToUrl($"/runs/{runId}/cancel");
            x.StatusCodeShouldBe(202);
        });

    // Drives every run to a terminal state so a test leaves its schema clean (no saga-backed run the next run's recovery scan
    // would re-drive) — the durable tests' discipline that keeps a reused schema from carrying state between runs.
    private static async Task DrainAsync(IAlbaHost host, params Guid[] runIds)
    {
        foreach (var id in runIds)
        {
            await DurableHost.PollUntilTerminalAsync(host, id, TimeSpan.FromSeconds(30));
        }
    }

    // The global mt_events sequence of a run's RunDequeued (promotion) event — a durable, monotonic proof of promotion ORDER
    // (independent of the frozen test clock): if run X promoted before run Y, X's RunDequeued sequence is smaller.
    private static async Task<long> PromotionOrderAsync(IAlbaHost host, Guid runId)
    {
        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(TestTenants.PrimaryId);
        var events = await session.Events.FetchStreamAsync(runId);
        return events.First(e => e.EventType == typeof(RunDequeued)).Sequence;
    }

    private static async Task<IReadOnlyList<Type>> EventTypesAsync(IAlbaHost host, Guid runId)
    {
        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(TestTenants.PrimaryId);
        return [.. (await session.Events.FetchStreamAsync(runId)).Select(e => e.EventType)];
    }

    // ----- FIFO fill -> queue -> auto-start in order -------------------------------

    [Fact]
    public async Task Fills_the_slot_then_queues_and_auto_starts_in_fifo_order()
    {
        var holder = new GateHolder();
        var gate = new RunGate("CapHome");
        await using var host = await HostAsync("crawldad_slotq_fifo", holder);

        // The one slot is filled by a blocked run; the next three are QUEUED with ascending positions.
        var blocked = await StartBlockedAsync(host, holder, gate);
        var (b, bPos) = await StartQueuedAsync(host);
        var (c, cPos) = await StartQueuedAsync(host);
        var (d, dPos) = await StartQueuedAsync(host);
        (bPos, cPos, dPos).ShouldBe((1, 2, 3));

        // Position is computed on read and visible via GET while queued.
        (await StateAsync(host, c)).GetProperty("position").GetInt32().ShouldBe(2);

        // Free the slot: cancel the blocker (releasing the gate so every promoted run runs straight through).
        await CancelAsync(host, blocked);
        gate.Release();
        (await DurableHost.PollUntilTerminalAsync(host, blocked, TimeSpan.FromSeconds(20))).GetProperty("status").GetString().ShouldBe("cancelled");

        // All three queued runs auto-start and complete — never more than the one slot at a time.
        foreach (var id in new[] { b, c, d })
        {
            (await DurableHost.PollUntilTerminalAsync(host, id, TimeSpan.FromSeconds(30))).GetProperty("status").GetString().ShouldBe("succeeded");
        }

        // FIFO: they were promoted oldest-first (B before C before D), proven by their RunDequeued sequences.
        var (bSeq, cSeq, dSeq) = (await PromotionOrderAsync(host, b), await PromotionOrderAsync(host, c), await PromotionOrderAsync(host, d));
        bSeq.ShouldBeLessThan(cSeq);
        cSeq.ShouldBeLessThan(dSeq);
    }

    // ----- queue position + the queued->running transition over SSE ----------------

    [Fact]
    public async Task Surfaces_the_queued_state_and_the_promotion_transition_over_sse()
    {
        var holder = new GateHolder();
        var gate = new RunGate("CapHome");
        await using var host = await HostAsync("crawldad_slotq_sse", holder);

        var blocked = await StartBlockedAsync(host, holder, gate);
        var (queued, _) = await StartQueuedAsync(host);

        // While still queued the run's SSE stream already exists (opened by RunQueued) — a client can tail it.
        (await StateAsync(host, queued)).GetProperty("status").GetString().ShouldBe("queued");

        // Free the slot so the queued run promotes and completes.
        await CancelAsync(host, blocked);
        gate.Release();
        await DurableHost.PollUntilTerminalAsync(host, blocked, TimeSpan.FromSeconds(20));
        (await DurableHost.PollUntilTerminalAsync(host, queued, TimeSpan.FromSeconds(30))).GetProperty("status").GetString().ShouldBe("succeeded");

        // The SSE stream (backfilled read-your-writes from the durable trace) carries the queued state and the
        // queued->running transition a live tail would have seen as they were appended.
        var frames = await SseReader.ReadToCloseAsync(host, queued, lastEventId: null, TimeSpan.FromSeconds(30));
        frames.ShouldContain(f => f.Event == nameof(RunQueued));   // the queued state is visible in the timeline
        frames.ShouldContain(f => f.Event == nameof(RunDequeued)); // and SSE emits the queued->running transition
    }

    // ----- 429 only past the per-tier queue depth ----------------------------------

    [Fact]
    public async Task Rejects_429_only_past_the_queue_depth()
    {
        var holder = new GateHolder();
        var gate = new RunGate("CapHome");
        // A per-tenant queue-depth override (the same seam as MaxConcurrentRuns) — resolved ahead of the global default.
        await using var host = await HostAsync("crawldad_slotq_depth", holder, ("Crawldad:Tenants:0:MaxQueueDepth", "2"));

        var blocked = await StartBlockedAsync(host, holder, gate);
        var (b, _) = await StartQueuedAsync(host); // depth 1
        var (c, _) = await StartQueuedAsync(host); // depth 2 == cap

        // The queue is full: the next admission is the ONLY 429 the endpoint returns, with the typed code.
        var rejected = await host.Scenario(x =>
        {
            x.Post.Json(Body()).ToUrl("/runs");
            x.StatusCodeShouldBe(429);
        });
        (await rejected.ReadAsJsonAsync<JsonElement>()).GetProperty("code").GetString().ShouldBe(RunQueue.QueueDepthExceededCode);

        await CancelAsync(host, blocked); // free the slot; the two queued runs then promote and complete
        gate.Release();
        await DrainAsync(host, blocked, b, c);
    }

    // ----- cancel-while-queued dequeues without a slot -----------------------------

    [Fact]
    public async Task Cancel_while_queued_dequeues_without_consuming_a_slot()
    {
        var holder = new GateHolder();
        var gate = new RunGate("CapHome");
        await using var host = await HostAsync("crawldad_slotq_cancel", holder);

        var blocked = await StartBlockedAsync(host, holder, gate);

        // A nameless, input-less payload queued at the cap (it is cancelled before it can run, so it needs no backend) —
        // exercising the enqueue's unnamed-payload / empty-inputs fallbacks.
        var accepted = await host.Scenario(x =>
        {
            x.Post.Json(new JsonObject
            {
                ["payload"] = JsonNode.Parse("""{ "crawldad": "1", "config": { "backend": "input.backend" }, "steps": [], "result": "'x'" }"""),
                ["async"] = true,
            }).ToUrl("/runs");
            x.StatusCodeShouldBe(202);
        });
        var queuedState = (await accepted.ReadAsJsonAsync<JsonElement>()).Clone();
        queuedState.GetProperty("status").GetString().ShouldBe("queued");
        var queued = queuedState.GetProperty("runId").GetGuid();

        // Cancelling the queued run drives it straight to cancelled — it never ran, so no RunDequeued, and the blocker keeps
        // its slot (nothing was promoted).
        await CancelAsync(host, queued);
        (await DurableHost.PollUntilTerminalAsync(host, queued, TimeSpan.FromSeconds(20))).GetProperty("status").GetString().ShouldBe("cancelled");

        var types = await EventTypesAsync(host, queued);
        types.ShouldContain(typeof(RunQueued));
        types.ShouldContain(typeof(RunCancelled));
        types.ShouldNotContain(typeof(RunDequeued)); // never promoted → never consumed a slot
        (await StateAsync(host, blocked)).GetProperty("status").GetString().ShouldBe("running"); // the blocker still holds the only slot

        await CancelAsync(host, blocked); // cleanup (nothing left queued to promote)
        gate.Release();
        await DrainAsync(host, blocked);
    }

    // ----- the run deadline starts at EXECUTION, not enqueue -----------------------

    [Fact]
    public async Task The_run_deadline_starts_at_execution_not_enqueue()
    {
        var holder = new GateHolder();
        var gate = new RunGate("CapHome");
        await using var host = await HostAsync("crawldad_slotq_deadline", holder);

        var blocked = await StartBlockedAsync(host, holder, gate);

        // A queued run whose wall-clock deadline (1 s) is shorter than the time it will spend queued (1.5 s). If the deadline
        // counted queue time it would fail; it must not — the deadline is only scheduled at promotion.
        var accepted = await host.Scenario(x =>
        {
            x.Post.Json(Body(extraConfig: "\"deadlineMs\": 1000")).ToUrl("/runs");
            x.StatusCodeShouldBe(202);
        });
        var queued = (await accepted.ReadAsJsonAsync<JsonElement>()).GetProperty("runId").GetGuid();

        // Wait past the deadline while it is still queued: no RunDeadline is scheduled until promotion, so it stays queued.
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        (await StateAsync(host, queued)).GetProperty("status").GetString().ShouldBe("queued");

        // Promote it — its deadline now starts, and it completes well within 500 ms of execution start.
        await CancelAsync(host, blocked);
        gate.Release();
        (await DurableHost.PollUntilTerminalAsync(host, queued, TimeSpan.FromSeconds(30))).GetProperty("status").GetString().ShouldBe("succeeded");
        await DrainAsync(host, blocked);
    }

    // ----- a run that waits too long in the queue times out cleanly ----------------

    [Fact]
    public async Task A_run_that_waits_past_the_max_queue_wait_times_out()
    {
        var holder = new GateHolder();
        var gate = new RunGate("CapHome");
        await using var host = await HostAsync("crawldad_slotq_wait", holder, ("Crawldad:Limits:MaxQueueWaitMs", "400"));

        var blocked = await StartBlockedAsync(host, holder, gate);
        var (queued, _) = await StartQueuedAsync(host);

        // The blocker holds the slot past the max queue wait; the queued run terminates on its own with the typed code.
        var terminal = await DurableHost.PollUntilTerminalAsync(host, queued, TimeSpan.FromSeconds(20));
        terminal.GetProperty("status").GetString().ShouldBe("failed");
        terminal.GetProperty("failure").GetProperty("code").GetString().ShouldBe(RunQueue.QueueWaitExceededCode);

        // It promoted nothing extra: the blocker still holds the only slot.
        (await StateAsync(host, blocked)).GetProperty("status").GetString().ShouldBe("running");

        await CancelAsync(host, blocked); // cleanup (the queued run already failed, so nothing promotes)
        gate.Release();
        await DrainAsync(host, blocked);
    }

    // ----- crash/restart with a non-empty queue (durable, FIFO) --------------------

    [Fact]
    public async Task Queued_runs_survive_a_restart_and_start_in_order()
    {
        const string Schema = "crawldad_slotq_restart";

        // Host 1: one blocked run holds the slot; two more are queued. Then the host is killed mid-flight.
        var holder1 = new GateHolder();
        var host1 = await HostAsync(Schema, holder1);
        Guid b, c;
        try
        {
            await StartBlockedAsync(host1, holder1, new RunGate("CapHome"));
            (b, _) = await StartQueuedAsync(host1);
            (c, _) = await StartQueuedAsync(host1);
        }
        finally
        {
            await host1.DisposeAsync(); // honest kill: the blocker is left running, B and C left queued (durably)
        }

        // Host 2 on the SAME schema/durable queues, gate unarmed: recovery resumes the blocker (which now runs through),
        // seeds the FIFO counter, and re-triggers promotion — so the surviving queued runs start, in order.
        var holder2 = new GateHolder();
        holder2.Arm(gate: null);
        await using var host2 = await DurableHost.BuildAsync(Schema, new GatedFakeBackend(Runner.FixturesRoot, holder2), resetData: false, settings: Settings());

        (await DurableHost.PollUntilTerminalAsync(host2, b, TimeSpan.FromSeconds(40))).GetProperty("status").GetString().ShouldBe("succeeded");
        (await DurableHost.PollUntilTerminalAsync(host2, c, TimeSpan.FromSeconds(40))).GetProperty("status").GetString().ShouldBe("succeeded");

        // FIFO preserved across the restart: B (enqueued first) promoted before C.
        (await PromotionOrderAsync(host2, b)).ShouldBeLessThan(await PromotionOrderAsync(host2, c));
    }

    // ----- p95 queue wait observability --------------------------------------------

    [Fact]
    public async Task Queue_stats_exposes_depth_and_p95_wait()
    {
        var holder = new GateHolder();
        var gate = new RunGate("CapHome");
        await using var host = await HostAsync("crawldad_slotq_p95", holder);

        var blocked = await StartBlockedAsync(host, holder, gate);
        var (queued, _) = await StartQueuedAsync(host);

        // While a run is queued and none has been promoted: depth 1, no p95 sample yet.
        var before = await QueueStatsAsync(host);
        before.GetProperty("queued").GetInt32().ShouldBe(1);
        before.GetProperty("sampled").GetInt32().ShouldBe(0);
        before.GetProperty("p95QueueWaitMs").GetInt64().ShouldBe(0);

        // Promote the queued run; its queue wait is recorded (0 under the frozen clock — the plumbing, not the value).
        await CancelAsync(host, blocked);
        gate.Release();
        await DurableHost.PollUntilTerminalAsync(host, blocked, TimeSpan.FromSeconds(20));
        await DurableHost.PollUntilTerminalAsync(host, queued, TimeSpan.FromSeconds(30));

        var after = await QueueStatsAsync(host);
        after.GetProperty("queued").GetInt32().ShouldBe(0);
        after.GetProperty("sampled").GetInt32().ShouldBeGreaterThanOrEqualTo(1); // a promoted run contributed a wait sample
        after.GetProperty("p95QueueWaitMs").GetInt64().ShouldBe(0);
    }

    private static async Task<JsonElement> QueueStatsAsync(IAlbaHost host)
    {
        var result = await host.Scenario(x =>
        {
            x.Get.Url("/runs/queue-stats");
            x.StatusCodeShouldBe(200);
        });
        return (await result.ReadAsJsonAsync<JsonElement>()).Clone();
    }

    // ----- queue-service edge branches (white-box) ---------------------------------

    [Fact]
    public async Task Queue_service_edge_cases_behave()
    {
        var holder = new GateHolder();
        await using var host = await HostAsync("crawldad_slotq_service", holder);
        var queue = host.Services.GetRequiredService<RunQueue>();
        var gate = (RunAdmissionGate)host.Services.GetRequiredService<IRunAdmissionGate>();
        var store = host.Services.GetRequiredService<IDocumentStore>();
        var signals = host.Services.GetRequiredService<RunEventSignals>();
        var clock = host.Services.GetRequiredService<TimeProvider>();
        using var scope = host.Services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        // Promotion with no matching progress row: the reserved slot is released (not leaked) and the failure propagates.
        var orphan = Guid.NewGuid();
        await using (var seed = store.LightweightSession(TestTenants.PrimaryId))
        {
            seed.Store(new QueuedRun { Id = orphan, Sequence = queue.NextSequence(), Script = "{}", Inputs = "{}", QueuedAt = clock.GetUtcNow() });
            await seed.SaveChangesAsync();
        }

        await Should.ThrowAsync<Exception>(async () => await queue.PromoteOldestAsync(bus, TestTenants.PrimaryId, CancellationToken.None));
        gate.ActiveCount(TestTenants.PrimaryId).ShouldBe(0); // the reservation was released in the failure path

        await using (var cleanup = store.LightweightSession(TestTenants.PrimaryId))
        {
            cleanup.Delete<QueuedRun>(orphan);
            await cleanup.SaveChangesAsync();
        }

        // Cancelling a non-queued run through the queue is a no-op.
        await using (var session = store.LightweightSession(TestTenants.PrimaryId))
        {
            (await queue.CancelQueuedAsync(session, new RunProgress { Id = Guid.NewGuid(), Status = RunStatus.Running }, CancellationToken.None)).ShouldBeFalse();
        }

        // Position of a run that is not queued is null (the read-race guard).
        await using (var session = store.QuerySession(TestTenants.PrimaryId))
        {
            (await queue.PositionAsync(session, Guid.NewGuid(), CancellationToken.None)).ShouldBeNull();
        }

        // Promoting an empty queue does nothing.
        (await queue.PromoteOldestAsync(bus, TestTenants.PrimaryId, CancellationToken.None)).ShouldBeFalse();

        // The handlers fail closed without a tenant on the envelope; the wait timeout is a no-op for a run that is gone or not
        // queued (a valid tenant with no progress row, and a run already running).
        await PromoteQueuedHandler.Handle(new PromoteQueued(), queue, bus, new Envelope(), CancellationToken.None);
        await QueueWaitDeadlineHandler.Handle(new QueueWaitDeadline(Guid.NewGuid()), store, new Envelope(), signals, clock, CancellationToken.None);
        await QueueWaitDeadlineHandler.Handle(new QueueWaitDeadline(Guid.NewGuid()), store, new Envelope { TenantId = TestTenants.PrimaryId }, signals, clock, CancellationToken.None);

        var promoted = Guid.NewGuid();
        await using (var seed = store.LightweightSession(TestTenants.PrimaryId))
        {
            seed.Store(new RunProgress { Id = promoted, Status = RunStatus.Running }); // already running, not queued
            await seed.SaveChangesAsync();
        }
        await QueueWaitDeadlineHandler.Handle(new QueueWaitDeadline(promoted), store, new Envelope { TenantId = TestTenants.PrimaryId }, signals, clock, CancellationToken.None);
        (await StateAsync(host, promoted)).GetProperty("status").GetString().ShouldBe("running"); // untouched by the spent timeout
    }
}
