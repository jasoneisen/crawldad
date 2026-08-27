using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Crawldad.Api.Features.Payloads;
using Crawldad.Api.Features.Runs.Interpreter;
using Crawldad.Api.Infrastructure.Browser;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Api.Infrastructure.Storage;
using Crawldad.Contracts.Payloads;
using Crawldad.Contracts.Runs;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.Http;

namespace Crawldad.Api.Features.Runs;

/// <summary><c>POST /runs</c>: executes one payload and returns the shaped result or a typed failure — a failed
/// <em>run</em> is HTTP 200, not a failed request. The payload is supplied one of two mutually-exclusive ways: an
/// <b>inline</b> document, or a pinned managed payload. At the concurrent-run cap the run is <b>queued, not rejected</b> — a <c>429</c> occurs only past the tenant's queue depth.</summary>
public static class StartRunEndpoint
{
    /// <summary>The default run wall-clock cap when the payload sets no <c>config.deadlineMs</c>: 30 minutes — well
    /// clear of the reference's minutes-long runs and deliberately not in the 40–60 s competitor range.</summary>
    public const int DefaultDeadlineMs = 30 * 60 * 1000;

    /// <summary>Handles <c>POST /runs</c>.</summary>
    [WolverinePost("/runs")]
    public static async Task<IResult> Handle(
        StartRunRequest request,
        IDocumentSession session,
        IBrowserBackendRegistry registry,
        IDownloadSinkRegistry sinks,
        CredentialScrubber scrubber,
        IRunSecretScope secretScope,
        ISecretStoreRegistry secretStores,
        IMessageBus bus,
        IRunAdmissionGate gate,
        RunQueue queue,
        IRunControlRegistry controls,
        SyncRunSupervisor supervisor,
        IOptions<RunLimitsOptions> limitsOptions,
        TimeProvider clock,
        CancellationToken ct)
    {
        // The server-side mid-run caps the synchronous interpreter enforces; the async executor derives its own from the
        // same options. A payload cannot raise them — they are config, not payload fields.
        var limits = limitsOptions.Value.ToRunLimits();

        // The synchronous wall-clock window: a default POST /runs holding its connection past this is auto-upgraded to
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

            // The stored revision script is already credential-scrubbed; execute it exactly as an inline payload.
            using var pinnedDocument = JsonDocument.Parse(pinned.Script);
            return await DispatchAsync(
                request, pinnedDocument.RootElement, pinned.Script, pinned.ScriptHash, payloadId, revision,
                session, registry, sinks, scrubber, secretScope, secretStores, bus, gate, queue, controls, supervisor, limits, syncThresholdMs, clock, ct);
        }

        return await DispatchAsync(
            request, request.Payload, request.Payload.GetRawText(), ComputeScriptHash(request.Payload), null, null,
            session, registry, sinks, scrubber, secretScope, secretStores, bus, gate, queue, controls, supervisor, limits, syncThresholdMs, clock, ct);
    }

    // Routes the resolved payload through the single admission decision: admit a slot and run now, or — at the cap —
    // queue the run (or 429 past queue depth). Runs AFTER pin resolution (so a bad pin is still a 400, not a queue).
    // Slot release is exception-safe end to end across the inline, async hand-off, and sync-auto-upgrade paths.
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
        ISecretStoreRegistry secretStores,
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
        // The request's Marten session is already scoped to the authenticated tenant, so its tenant is the run's.
        var tenantId = session.TenantId!;
        var runId = Guid.NewGuid();

        // Resolve the tenant's per-tenant cap from the registry store (not the short-TTL auth cache) before admitting, so a
        // registry tenant's slot allowance is honoured here exactly as it is on the background promotion path.
        await gate.PrimeAsync(tenantId, ct);

        // Admit a slot only when the queue is empty (FIFO fairness): a fresh run must not jump ahead of runs already
        // waiting, so once anything is queued a new arrival queues behind it — even if a slot is momentarily free.
        // Otherwise TryAdmit atomically reserves a free slot.
        var admitted = !await queue.HasQueuedAsync(session, ct) && gate.TryAdmit(tenantId, runId);
        if (!admitted)
        {
            var queuedResult = await EnqueueAsync(request, payload, script, scriptHash, payloadId, payloadRevision, runId, tenantId, session, bus, queue, scrubber, ct);

            // If a slot is actually free at commit time (the stranding race — the cap freed between TryAdmit and the
            // enqueue commit), nudge a promotion so this run is not left behind idle capacity with no pending trigger.
            // Conditional on real free capacity, so the common at-cap enqueue publishes nothing.
            if (gate.HasCapacity(tenantId))
            {
                await bus.PublishAsync(new PromoteQueued(), new DeliveryOptions { TenantId = tenantId });
            }

            return queuedResult;
        }

        if (request.Async)
        {
            // The async slot's lifetime is the executor's: it releases it at finalisation (which then promotes the
            // tenant's oldest queued run). A hand-off that faults before the executor owns the slot leaks it until
            // restart recovery — a rare, documented exceptional-path edge.
            return await StartBackgroundRunAsync(runId, payload, script, scriptHash, payloadId, payloadRevision, request.Inputs, session, scrubber, bus, clock, ct);
        }

        return await RunAdmittedSyncAsync(
            runId, tenantId, payload, script, scriptHash, payloadId, payloadRevision, request.Inputs,
            session, registry, sinks, scrubber, secretScope, secretStores, bus, gate, controls, supervisor, limits, syncThresholdMs, clock, ct);
    }

    // The admitted synchronous path: the interpreter runs inline, raced against the sync-upgrade window. A run finishing
    // within the window is freed here exception-safely, then promotes the next queued run. A run that outruns the window
    // is auto-upgraded — its slot/secret scope/interpreter pass to the supervisor, so THIS path must NOT free it.
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
        ISecretStoreRegistry secretStores,
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
                session, registry, sinks, scrubber, secretScope, secretStores, bus, controls, supervisor, limits, syncThresholdMs, clock, ct);
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

    // At the tenant's concurrent-run cap: queue the run rather than reject it — unless the tenant's queue is already at
    // its per-tier depth, the one remaining 429 (queue_depth_exceeded). A queued run holds no slot; it is persisted
    // durably and starts automatically when a slot frees. A queued sync run is upgraded to the async surface.
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
        var payloadName = PayloadName(payload);

        var queued = new QueuedRunRequest(runId, payloadName, scriptHash, script, InputsJson(request.Inputs), [.. input.Keys], payloadId, payloadRevision, ReadDeadlineMs(payload));
        var position = await queue.EnqueueAsync(session, bus, queued, scrubber, ct);

        return Results.Accepted($"/runs/{runId}", new RunStateResponse(runId, RunStatus.Queued, null, null, null, null, Position: position));
    }

    // The outcome of the synchronous path: the response to return, and whether the run was auto-upgraded to async (so the
    // caller leaves the slot/promotion to the supervisor rather than freeing it inline).
    private readonly record struct SyncCapResult(IResult Response, bool Upgraded);

    // The interpreter runs INLINE, raced against the sync-upgrade window via Task.WhenAny — resolving to EXACTLY ONE of
    // {200 terminal response within the window, 202 auto-upgrade to async if it elapses first}, never both. The interpreter
    // runs on its own cancellation source (never the request's), so a client disconnect no longer cancels an in-flight run.
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
        ISecretStoreRegistry secretStores,
        IMessageBus bus,
        IRunControlRegistry controls,
        SyncRunSupervisor supervisor,
        RunLimits limits,
        int syncThresholdMs,
        TimeProvider clock,
        CancellationToken ct)
    {
        // The interpreter may outlive this request (an auto-upgrade detaches it), and a pinned/replay run's payload is a
        // JsonElement over a `using var JsonDocument` disposed the instant Handle returns the 202. Clone so the detached
        // interpreter owns GC-backed memory, never the disposed request document — else ObjectDisposedException.
        payload = payload.Clone();

        var input = JsonValues.FromJson(inputs) as Dictionary<string, object?> ?? new(StringComparer.Ordinal);
        var payloadName = PayloadName(payload);

        // The per-run secret scope and the interpreter's own cancellation source span the WHOLE run. Both are disposed
        // here on the inline path; on upgrade they are TRANSFERRED to the supervisor (it disposes them once the run
        // finalises), tracked by `transferred` so this finally never tears down a run the supervisor now owns.
        var secretScopeHandle = secretScope.Begin();
        var runCts = new CancellationTokenSource();
        var transferred = false;
        try
        {
            // Pin RunStarted (not yet committed): the inline path commits it with the terminal event; the upgrade path
            // commits it with the seeded RunProgress. The session is already tenant-scoped.
            session.Events.StartStream<Run>(
                runId,
                RunEventScrubber.Scrub(new RunStarted(payloadName, scriptHash, clock.GetUtcNow(), [.. input.Keys], payloadId, payloadRevision), scrubber));

            // The lean synchronous interpreter — no observer, no screenshot store, threaded the run's tenant — so a run
            // finishing within the window is byte-identical to today's. Started (not awaited) so it can be raced.
            var execution = new RunInterpreter(payload, input, registry, sinks, clock, session.TenantId, limits: limits, secretStores: secretStores, secretScope: secretScope).RunAsync(runCts.Token);

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

    // The fast-path terminal finalisation, byte-for-byte: append the interpreter's buffered coarse trace events
    // (LogEmitted/RunAttemptFailed, in occurrence order) and the terminal event to the run's stream in the request's
    // one transaction, then return the RunResponse. Each string is scrubbed at this single append chokepoint.
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

    // Auto-upgrades a synchronous run that outran the sync cap onto the async surface WITHOUT restarting it. Claims the
    // run's in-process control FIRST — before the saga's ExecuteRun can be handled — so the executor finds it already
    // claimed and no-ops (never a duplicate interpreter). Binds forcible-for-every-reason so cancel/deadline can stop the observer-less run.
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

    // The async run path: pin RunStarted + seed the running RunProgress read model + kick the durable executor saga,
    // then return 202 immediately. No interpreter runs in the request (no secret scope here — the executor opens its
    // own), so this returns before the run does any work.
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
        var payloadName = PayloadName(payload);

        session.Events.StartStream<Run>(
            runId,
            RunEventScrubber.Scrub(new RunStarted(payloadName, scriptHash, clock.GetUtcNow(), [.. input.Keys], payloadId, payloadRevision), scrubber));
        session.Store(new RunProgress { Id = runId, Status = RunStatus.Running });
        await session.SaveChangesAsync(ct);

        await bus.PublishAsync(new StartRun(runId, payloadName, scriptHash, script, InputsJson(inputs), payloadId, payloadRevision, ReadDeadlineMs(payload)));

        return Results.Accepted($"/runs/{runId}", new RunStateResponse(runId, RunStatus.Running, null, null, null, null));
    }

    // The run's display name: a string `name`, else "unnamed". Total on an UNVALIDATED inline payload — a wrong-kinded
    // (or absent) name never faults this request-thread read; the interpreter classifies any deeper malformation.
    internal static string PayloadName(JsonElement payload) =>
        payload.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()! : "unnamed";

    // The run wall-clock cap: config.deadlineMs when set, else the generous default. Total on an UNVALIDATED inline
    // payload — a missing/wrong-kinded config or deadlineMs falls back to the default rather than faulting this
    // request-thread read (which runs BEFORE the interpreter, so it cannot itself raise a classified run failure).
    internal static int ReadDeadlineMs(JsonElement payload) =>
        payload.TryGetProperty("config", out var config) && config.ValueKind == JsonValueKind.Object
        && config.TryGetProperty("deadlineMs", out var deadline) && deadline.ValueKind == JsonValueKind.Number
        && deadline.TryGetInt32(out var ms)
            ? ms
            : DefaultDeadlineMs;

    // The run inputs as persistable JSON: an absent (undefined) inputs value becomes the empty object.
    private static string InputsJson(JsonElement inputs) =>
        inputs.ValueKind == JsonValueKind.Undefined ? "{}" : inputs.GetRawText();

    private static string ComputeScriptHash(JsonElement payload) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload.GetRawText())));
}
