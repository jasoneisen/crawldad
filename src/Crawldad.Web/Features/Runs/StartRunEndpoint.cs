using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Crawldad.Contracts.Payloads;
using Crawldad.Contracts.Runs;
using Crawldad.Web.Features.Payloads;
using Crawldad.Web.Features.Runs.Interpreter;
using Crawldad.Web.Infrastructure.Browser;
using Crawldad.Web.Infrastructure.Security;
using Crawldad.Web.Infrastructure.Storage;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.Http;

namespace Crawldad.Web.Features.Runs;

/// <summary>
/// <c>POST /runs</c> (§10/§11, Deliverable 4): executes one payload and returns the shaped result or a typed failure — a
/// failed <em>run</em> is HTTP 200 (a failed run is not a failed request). The payload is supplied one of two
/// mutually-exclusive ways (§14.2): an <b>inline</b> document, or a pinned managed payload (<c>payloadId</c> + optional
/// <c>revision</c>, default head). Pinning a non-existent payload/revision, or an archived payload, is a request rejection
/// (<c>400</c> <see cref="RunRejection"/>) — no run is started.
/// <para>
/// Admission (CD-3/CD-16, docs/PRODUCT.md §Pv.3) runs after pin resolution and identically for both execution modes. Under the
/// tenant's concurrent-run cap the run starts immediately: synchronously by default (the handler owns its Marten session
/// inline, interprets, and returns the terminal <see cref="RunResponse"/> — the exact P1–P4 behaviour, byte-for-byte), or with
/// <c>async: true</c> on the durable executor saga (a <c>202</c> with <c>{ runId, status:"running" }</c> to poll). <b>At the
/// cap the run is queued, not rejected</b> (CD-16): it is accepted, persisted <c>queued</c>, and returns
/// <c>202 { runId, status:"queued", position }</c> — a queued <em>sync</em> run is upgraded to this async surface — and starts
/// automatically when a slot frees. A <c>429</c> occurs only past the tenant's queue depth (<c>queue_depth_exceeded</c>).
/// </para>
/// </summary>
public static class StartRunEndpoint
{
    /// <summary>The default run wall-clock cap (§8.4) when the payload sets no <c>config.deadlineMs</c>: 30 minutes — well
    /// clear of the reference's minutes-long runs and deliberately not in the 40–60 s competitor range.</summary>
    public const int DefaultDeadlineMs = 30 * 60 * 1000;

    /// <summary>Handles <c>POST /runs</c>.</summary>
    /// <param name="request">The inline payload (or pinned <c>payloadId</c>) + inputs + async flag.</param>
    /// <param name="session">The request-scoped Marten session (Wolverine tracked-session compatible).</param>
    /// <param name="registry">Resolves the backend adapter named by the payload's <c>config.backend</c>.</param>
    /// <param name="sinks">Resolves the download sink named by a <c>download.to</c> target's <c>kind</c> (§9.3).</param>
    /// <param name="scrubber">Redacts credentials from every persisted event and the response (§12).</param>
    /// <param name="secretScope">The per-run secret registry the backend adapter registers the resolved credential into (§12).</param>
    /// <param name="bus">The message bus that kicks the executor saga (async/promotion) and the queue promotion trigger (§11/CD-16).</param>
    /// <param name="gate">The concurrent-run admission gate (CD-3): the atomic slot reservation.</param>
    /// <param name="queue">The durable FIFO admission queue (CD-16): the at-cap enqueue + depth guard.</param>
    /// <param name="controls">The in-process run-control registry (§11): a sync run auto-upgraded past the sync cap claims its control here so cancel/the saga deadline reach it and the saga's <c>ExecuteRun</c> no-ops (CD-15).</param>
    /// <param name="supervisor">The CD-15 sync auto-upgrade supervisor: drives an upgraded run's already-running interpreter to its terminal state after the 202.</param>
    /// <param name="limitsOptions">The server-side resource-limit options (CD-3/CD-15/§12): the mid-run caps and the sync-upgrade window.</param>
    /// <param name="clock">The time seam for event timestamps and duration.</param>
    /// <param name="ct">Cancels the run.</param>
    /// <returns><c>200</c> (sync terminal), <c>202</c> (async running, sync-cap upgraded, or queued at the cap), <c>400</c> (unrunnable pin), or <c>429</c> (queue full).</returns>
    [WolverinePost("/runs")]
    public static async Task<IResult> Handle(
        StartRunRequest request,
        IDocumentSession session,
        IBrowserBackendRegistry registry,
        IDownloadSinkRegistry sinks,
        CredentialScrubber scrubber,
        IRunSecretScope secretScope,
        IMessageBus bus,
        IRunAdmissionGate gate,
        RunQueue queue,
        IRunControlRegistry controls,
        SyncRunSupervisor supervisor,
        IOptions<RunLimitsOptions> limitsOptions,
        TimeProvider clock,
        CancellationToken ct)
    {
        // The server-side mid-run caps (CD-3/§12) the synchronous interpreter enforces; the async executor derives its own
        // from the same options. A payload cannot raise them — they are config, not payload fields.
        var limits = limitsOptions.Value.ToRunLimits();

        // The synchronous wall-clock window (CD-15): a default POST /runs holding its connection past this is auto-upgraded to
        // async (202 + poll) instead of dying behind an Azure ingress timeout. A config knob (default 120 s), never a payload field.
        var syncThresholdMs = limitsOptions.Value.SyncUpgradeThresholdMs;

        if (request.PayloadId is Guid payloadId)
        {
            var resolved = await PayloadRevisions.LoadAsync(session, payloadId, ct);
            if (resolved is null)
            {
                return Results.BadRequest(new RunRejection("unknown_payload", $"no payload '{payloadId}' exists"));
            }

            if (resolved.Status == PayloadStatus.Archived)
            {
                return Results.BadRequest(new RunRejection("payload_archived", $"payload '{payloadId}' is archived and cannot be run"));
            }

            var revision = request.Revision ?? resolved.HeadRevision;
            var pinned = resolved.At(revision);
            if (pinned is null)
            {
                return Results.BadRequest(new RunRejection("unknown_revision", $"payload '{payloadId}' has no revision {revision}"));
            }

            // The stored revision script is already credential-scrubbed (§12); execute it exactly as an inline payload.
            using var pinnedDocument = JsonDocument.Parse(pinned.Script);
            return await DispatchAsync(
                request, pinnedDocument.RootElement, pinned.Script, pinned.ScriptHash, payloadId, revision,
                session, registry, sinks, scrubber, secretScope, bus, gate, queue, controls, supervisor, limits, syncThresholdMs, clock, ct);
        }

        return await DispatchAsync(
            request, request.Payload, request.Payload.GetRawText(), ComputeScriptHash(request.Payload), null, null,
            session, registry, sinks, scrubber, secretScope, bus, gate, queue, controls, supervisor, limits, syncThresholdMs, clock, ct);
    }

    // Routes the resolved payload through the single admission decision (CD-3/CD-16, §Pv.3): admit a slot and run now
    // (sync inline or async executor), or — at the cap — queue the run (or 429 past the tenant's queue depth). Admission runs
    // AFTER pin resolution (so a bad pin is still a 400, not a queue) and identically for both modes. Slot release is
    // exception-safe end to end: the inline path always releases in a finally; the async hand-off releases only if it throws
    // before the executor owns the slot; a sync run auto-upgraded past the sync cap (CD-15) hands its slot to the supervisor.
    private static async Task<IResult> DispatchAsync(
        StartRunRequest request,
        JsonElement payload,
        string script,
        string scriptHash,
        Guid? payloadId,
        int? payloadRevision,
        IDocumentSession session,
        IBrowserBackendRegistry registry,
        IDownloadSinkRegistry sinks,
        CredentialScrubber scrubber,
        IRunSecretScope secretScope,
        IMessageBus bus,
        IRunAdmissionGate gate,
        RunQueue queue,
        IRunControlRegistry controls,
        SyncRunSupervisor supervisor,
        RunLimits limits,
        int syncThresholdMs,
        TimeProvider clock,
        CancellationToken ct)
    {
        // The request's Marten session is already scoped to the authenticated tenant (CD-1), so its tenant is the run's.
        var tenantId = session.TenantId!;
        var runId = Guid.NewGuid();

        // Admit a slot only when the queue is empty (FIFO fairness, CD-16): a fresh run must not jump ahead of runs already
        // waiting, so once anything is queued a new arrival queues behind it — even if a slot is momentarily free. Otherwise
        // TryAdmit atomically reserves a free slot.
        var admitted = !await queue.HasQueuedAsync(session, ct) && gate.TryAdmit(tenantId, runId);
        if (!admitted)
        {
            var queuedResult = await EnqueueAsync(request, payload, script, scriptHash, payloadId, payloadRevision, runId, tenantId, session, bus, queue, scrubber, ct);

            // SHOULD-FIX-2: if a slot is actually free at commit time (the stranding race — the cap freed between our TryAdmit
            // and the enqueue commit), nudge a promotion so this run is not left behind idle capacity with no pending trigger.
            // Conditional on real free capacity, so the common at-cap enqueue publishes nothing (no churn, no restart-redelivery storm).
            if (gate.HasCapacity(tenantId))
            {
                await bus.PublishAsync(new PromoteQueued(), new DeliveryOptions { TenantId = tenantId });
            }

            return queuedResult;
        }

        if (request.Async)
        {
            // The async slot's lifetime is the executor's: it releases it at finalisation (which then promotes the tenant's
            // oldest queued run). A hand-off that faults before the executor owns the slot leaks it until restart recovery —
            // the rare, documented CD-3 exceptional-path edge, unchanged by CD-16.
            return await StartBackgroundRunAsync(runId, payload, script, scriptHash, payloadId, payloadRevision, request.Inputs, session, scrubber, bus, clock, ct);
        }

        return await RunAdmittedSyncAsync(
            runId, tenantId, payload, script, scriptHash, payloadId, payloadRevision, request.Inputs,
            session, registry, sinks, scrubber, secretScope, bus, gate, controls, supervisor, limits, syncThresholdMs, clock, ct);
    }

    // The admitted synchronous path (CD-15): the interpreter runs inline and is raced against the sync-upgrade window. A run
    // that finishes within the window holds its slot for its own duration and is freed here exception-safely (the exact pre-CD-15
    // lifetime), then promotes the tenant's next queued run. A run that outruns the window is auto-upgraded — its slot, secret
    // scope, and running interpreter pass to the supervisor, which frees the slot (and promotes) when the run finalises — so
    // this path must NOT free it.
    private static async Task<IResult> RunAdmittedSyncAsync(
        Guid runId,
        string tenantId,
        JsonElement payload,
        string script,
        string scriptHash,
        Guid? payloadId,
        int? payloadRevision,
        JsonElement inputs,
        IDocumentSession session,
        IBrowserBackendRegistry registry,
        IDownloadSinkRegistry sinks,
        CredentialScrubber scrubber,
        IRunSecretScope secretScope,
        IMessageBus bus,
        IRunAdmissionGate gate,
        IRunControlRegistry controls,
        SyncRunSupervisor supervisor,
        RunLimits limits,
        int syncThresholdMs,
        TimeProvider clock,
        CancellationToken ct)
    {
        var upgraded = false;
        IResult response;
        try
        {
            var capResult = await ExecuteWithSyncCapAsync(
                runId, payload, script, scriptHash, payloadId, payloadRevision, inputs,
                session, registry, sinks, scrubber, secretScope, bus, controls, supervisor, limits, syncThresholdMs, clock, ct);
            upgraded = capResult.Upgraded;
            response = capResult.Response;
        }
        finally
        {
            // Free the slot for the inline (non-upgraded) path — including an unexpected throw before any 202 was sent (upgraded
            // is still false). An upgraded run's slot is the supervisor's now, so leave it held.
            if (!upgraded)
            {
                gate.Release(tenantId, runId);
            }
        }

        // The freed slot promotes the tenant's oldest queued run (a no-op when none is queued) — published AFTER the release so
        // the promotion finds the slot free. An upgraded run promotes from its supervisor at finalisation instead.
        if (!upgraded)
        {
            await bus.PublishAsync(new PromoteQueued(), new DeliveryOptions { TenantId = tenantId });
        }

        return response;
    }

    // At the tenant's concurrent-run cap: queue the run (CD-16) rather than reject it — unless the tenant's queue is already at
    // its per-tier depth, the one remaining 429 (queue_depth_exceeded). A queued run holds no slot; it is persisted durably and
    // starts automatically when a slot frees. A queued sync run is upgraded to the async surface (202 + poll/SSE).
    private static async Task<IResult> EnqueueAsync(
        StartRunRequest request,
        JsonElement payload,
        string script,
        string scriptHash,
        Guid? payloadId,
        int? payloadRevision,
        Guid runId,
        string tenantId,
        IDocumentSession session,
        IMessageBus bus,
        RunQueue queue,
        CredentialScrubber scrubber,
        CancellationToken ct)
    {
        var depthCap = queue.QueueDepthFor(tenantId);
        if (await queue.DepthAsync(session, ct) >= depthCap)
        {
            return Results.Json(
                new RunRejection(RunQueue.QueueDepthExceededCode, $"tenant '{tenantId}' admission queue is at its depth of {depthCap}"),
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        var input = JsonValues.FromJson(request.Inputs) as Dictionary<string, object?> ?? new(StringComparer.Ordinal);
        var payloadName = payload.TryGetProperty("name", out var name) ? name.GetString()! : "unnamed";

        var queued = new QueuedRunRequest(runId, payloadName, scriptHash, script, InputsJson(request.Inputs), [.. input.Keys], payloadId, payloadRevision, ReadDeadlineMs(payload));
        var position = await queue.EnqueueAsync(session, bus, queued, scrubber, ct);

        return Results.Accepted($"/runs/{runId}", new RunStateResponse(runId, RunStatus.Queued, null, null, null, null, Position: position));
    }

    // The outcome of the synchronous path: the response to return, and whether the run was auto-upgraded to async (so the
    // caller leaves the slot/promotion to the supervisor rather than freeing it inline).
    private readonly record struct SyncCapResult(IResult Response, bool Upgraded);

    // The synchronous run path with the CD-15 sync cap. The interpreter runs INLINE exactly as P1–P4 (no observer, lean
    // stream, one transaction), then is raced against the sync-upgrade wall-clock window:
    //   • finishes within the window → today's terminal RunResponse, byte-for-byte (200); the slot is freed by DispatchAsync;
    //   • window elapses first       → AUTO-UPGRADE (§8.4/CD-15): pin the run onto the async surface + kick the durable saga,
    //     hand the STILL-RUNNING interpreter to the supervisor, and return 202 { runId, status:"running" }.
    // Task.WhenAny resolves to EXACTLY ONE of the two, so a run finishing at the window boundary never yields both a 200 body
    // and a 202. The interpreter runs on its own cancellation source (never the request's), so returning 202 cannot cancel it —
    // the deliberate trade-off is that a client disconnect no longer cancels an in-flight sync run (pre-CD-15 it did, via the
    // request token): a run is now bounded by the sync window then the async wall-clock deadline (§8.4), not by the connection.
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The run's CancellationTokenSource and secret scope are disposed in the finally on the inline/fault paths, and TRANSFERRED to the SyncRunSupervisor on auto-upgrade (which disposes them once the run finalises) — never leaked.")]
    [SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "On the non-transferred path this finally runs no cancellation callbacks (the forcible binding happens only on the transferred/upgrade path this block skips), so Cancel() does not block; the async CancelAsync would add an async-finally state-machine branch no exceptional test path can reach.")]
    private static async Task<SyncCapResult> ExecuteWithSyncCapAsync(
        Guid runId,
        JsonElement payload,
        string script,
        string scriptHash,
        Guid? payloadId,
        int? payloadRevision,
        JsonElement inputs,
        IDocumentSession session,
        IBrowserBackendRegistry registry,
        IDownloadSinkRegistry sinks,
        CredentialScrubber scrubber,
        IRunSecretScope secretScope,
        IMessageBus bus,
        IRunControlRegistry controls,
        SyncRunSupervisor supervisor,
        RunLimits limits,
        int syncThresholdMs,
        TimeProvider clock,
        CancellationToken ct)
    {
        // The interpreter may outlive this request — an auto-upgrade detaches it (CD-15) — and a pinned/replay run's payload is
        // a JsonElement over a `using var JsonDocument` (Handle / RunReplayEndpoint) disposed the instant Handle returns the 202.
        // Read from a GC-backed Clone the detached interpreter owns, never the request's document, so a pinned/replay run
        // crossing the window cannot fault with ObjectDisposedException.
        payload = payload.Clone();

        var input = JsonValues.FromJson(inputs) as Dictionary<string, object?> ?? new(StringComparer.Ordinal);
        var payloadName = payload.TryGetProperty("name", out var name) ? name.GetString()! : "unnamed";

        // The per-run secret scope (§12) and the interpreter's own cancellation source span the WHOLE run. Both are disposed
        // here on the inline path; on upgrade they are TRANSFERRED to the supervisor (it disposes them once the run finalises),
        // tracked by `transferred` so this finally never tears down a run the supervisor now owns.
        var secretScopeHandle = secretScope.Begin();
        var runCts = new CancellationTokenSource();
        var transferred = false;
        try
        {
            // Pin RunStarted (not yet committed): the inline path commits it with the terminal event; the upgrade path commits
            // it with the seeded RunProgress. Scrubbed exactly as before (§12). The session is already tenant-scoped (CD-1).
            session.Events.StartStream<Run>(
                runId,
                RunEventScrubber.Scrub(new RunStarted(payloadName, scriptHash, clock.GetUtcNow(), [.. input.Keys], payloadId, payloadRevision), scrubber));

            // The lean synchronous interpreter — no observer, no screenshot store, threaded the run's tenant — the exact P1–P4
            // construction, so a run finishing within the window is byte-identical. Started (not awaited) so it can be raced.
            var execution = new RunInterpreter(payload, input, registry, sinks, clock, session.TenantId, limits: limits).RunAsync(runCts.Token);

            using var capCts = new CancellationTokenSource();
            var finishedWithinWindow = await Task.WhenAny(execution, Task.Delay(syncThresholdMs, capCts.Token)) == execution;
            if (finishedWithinWindow)
            {
                await capCts.CancelAsync(); // finished under the cap — stop the window timer
                var response = await FinalizeInlineAsync(runId, await execution, session, scrubber, clock, ct);
                return new SyncCapResult(response, Upgraded: false);
            }

            var upgrade = await UpgradeToAsyncAsync(
                runId, session.TenantId!, payload, script, scriptHash, payloadId, payloadRevision, inputs, payloadName,
                execution, session, bus, controls, supervisor, runCts, secretScopeHandle, ct);
            transferred = true;
            return upgrade;
        }
        finally
        {
            if (!transferred)
            {
                // Inline completion, or a fault before the 202 (e.g. an upgrade commit that threw): the supervisor never took
                // ownership, so tear the run's own lifetimes down here. Cancel first to stop the interpreter if it still runs;
                // scrubbing on the inline path has already happened by now, so clearing the secret scope is safe.
                controls.Remove(runId);
                runCts.Cancel();
                secretScopeHandle.Dispose();
                runCts.Dispose();
            }
        }
    }

    // The fast-path terminal finalisation (P1–P4, byte-for-byte): append the interpreter's buffered coarse trace events
    // (LogEmitted/RunAttemptFailed, in occurrence order) and the terminal event to the run's stream in the request's one
    // transaction, then return the §10 RunResponse. Each string is scrubbed at this single append chokepoint (§12).
    private static async Task<IResult> FinalizeInlineAsync(Guid runId, RunOutcome outcome, IDocumentSession session, CredentialScrubber scrubber, TimeProvider clock, CancellationToken ct)
    {
        foreach (var traceEvent in outcome.Events)
        {
            session.Events.Append(runId, RunEventScrubber.Scrub(traceEvent, scrubber));
        }

        var failure = outcome.Failure is null ? null : RunEventScrubber.ScrubFailure(outcome.Failure, scrubber);
        object finished = outcome.Status == RunStatus.Succeeded
            ? new RunSucceeded(outcome.Stats, clock.GetUtcNow())
            : new RunFailed(failure!, outcome.Stats, clock.GetUtcNow());
        session.Events.Append(runId, finished);

        await session.SaveChangesAsync(ct);

        return Results.Ok(new RunResponse(runId, outcome.Status, scrubber.ScrubJson(outcome.Result), failure, outcome.Stats));
    }

    // Auto-upgrades a synchronous run that outran the sync cap onto the async surface (CD-15) WITHOUT restarting it. Claim the
    // run's in-process control FIRST — before the saga's ExecuteRun can be handled — so the executor finds it already claimed
    // and no-ops (never a duplicate interpreter), and bind the interpreter's source as forcible-for-every-reason so a POST
    // /cancel or the saga's wall-clock deadline (§8.4) forcibly stops the observer-less run. Then seed the pollable RunProgress
    // + commit RunStarted in one transaction and kick the durable saga (its RunDeadline is the deadline backstop; a restart
    // re-runs it from scratch). Finally hand the still-running interpreter to the supervisor and return 202. Mirrors the native
    // async StartBackgroundRunAsync — the only difference is that the interpreter is already running, so ExecuteRun no-ops.
    private static async Task<SyncCapResult> UpgradeToAsyncAsync(
        Guid runId,
        string tenantId,
        JsonElement payload,
        string script,
        string scriptHash,
        Guid? payloadId,
        int? payloadRevision,
        JsonElement inputs,
        string payloadName,
        Task<RunOutcome> execution,
        IDocumentSession session,
        IMessageBus bus,
        IRunControlRegistry controls,
        SyncRunSupervisor supervisor,
        CancellationTokenSource runCts,
        IDisposable secretScopeHandle,
        CancellationToken ct)
    {
        var control = controls.GetOrAdd(runId);
        _ = control.TryClaim(); // a brand-new control — always succeeds; claiming before the saga starts makes ExecuteRun a no-op
        control.UseForcibleCancellation(runCts, forEveryReason: true);

        session.Store(new RunProgress { Id = runId, Status = RunStatus.Running });
        await session.SaveChangesAsync(ct);
        await bus.PublishAsync(new StartRun(runId, payloadName, scriptHash, script, InputsJson(inputs), payloadId, payloadRevision, ReadDeadlineMs(payload)));

        supervisor.Adopt(new SyncRunHandoff(runId, tenantId, execution, control, runCts, secretScopeHandle));
        return new SyncCapResult(
            Results.Accepted($"/runs/{runId}", new RunStateResponse(runId, RunStatus.Running, null, null, null, null)),
            Upgraded: true);
    }

    // The async run path (§11): pin RunStarted + seed the running RunProgress read model + kick the durable executor saga,
    // then return 202 immediately. No interpreter runs in the request (no secret scope here — the executor opens its own,
    // §12), so this returns before the run does any work. RunStarted is scrubbed exactly as the sync path scrubs it.
    private static async Task<IResult> StartBackgroundRunAsync(
        Guid runId,
        JsonElement payload,
        string script,
        string scriptHash,
        Guid? payloadId,
        int? payloadRevision,
        JsonElement inputs,
        IDocumentSession session,
        CredentialScrubber scrubber,
        IMessageBus bus,
        TimeProvider clock,
        CancellationToken ct)
    {
        var input = JsonValues.FromJson(inputs) as Dictionary<string, object?> ?? new(StringComparer.Ordinal);
        var payloadName = payload.TryGetProperty("name", out var name) ? name.GetString()! : "unnamed";

        session.Events.StartStream<Run>(
            runId,
            RunEventScrubber.Scrub(new RunStarted(payloadName, scriptHash, clock.GetUtcNow(), [.. input.Keys], payloadId, payloadRevision), scrubber));
        session.Store(new RunProgress { Id = runId, Status = RunStatus.Running });
        await session.SaveChangesAsync(ct);

        await bus.PublishAsync(new StartRun(runId, payloadName, scriptHash, script, InputsJson(inputs), payloadId, payloadRevision, ReadDeadlineMs(payload)));

        return Results.Accepted($"/runs/{runId}", new RunStateResponse(runId, RunStatus.Running, null, null, null, null));
    }

    // The run wall-clock cap (§8.4): config.deadlineMs when set, else the generous default.
    private static int ReadDeadlineMs(JsonElement payload) =>
        payload.GetProperty("config").TryGetProperty("deadlineMs", out var deadline) ? deadline.GetInt32() : DefaultDeadlineMs;

    // The run inputs as persistable JSON: an absent (undefined) inputs value becomes the empty object.
    private static string InputsJson(JsonElement inputs) =>
        inputs.ValueKind == JsonValueKind.Undefined ? "{}" : inputs.GetRawText();

    private static string ComputeScriptHash(JsonElement payload) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload.GetRawText())));
}
