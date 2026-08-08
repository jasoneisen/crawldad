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
    /// <param name="limitsOptions">The server-side resource-limit options (CD-3/§12).</param>
    /// <param name="clock">The time seam for event timestamps and duration.</param>
    /// <param name="ct">Cancels the run.</param>
    /// <returns><c>200</c> (sync terminal), <c>202</c> (async running, or queued at the cap), <c>400</c> (unrunnable pin), or <c>429</c> (queue full).</returns>
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
        IOptions<RunLimitsOptions> limitsOptions,
        TimeProvider clock,
        CancellationToken ct)
    {
        // The server-side mid-run caps (CD-3/§12) the synchronous interpreter enforces; the async executor derives its own
        // from the same options. A payload cannot raise them — they are config, not payload fields.
        var limits = limitsOptions.Value.ToRunLimits();

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
                session, registry, sinks, scrubber, secretScope, bus, gate, queue, limits, clock, ct);
        }

        return await DispatchAsync(
            request, request.Payload, request.Payload.GetRawText(), ComputeScriptHash(request.Payload), null, null,
            session, registry, sinks, scrubber, secretScope, bus, gate, queue, limits, clock, ct);
    }

    // Routes the resolved payload through the single admission decision (CD-3/CD-16, §Pv.3): admit a slot and run now
    // (sync inline or async executor), or — at the cap — queue the run (or 429 past the tenant's queue depth). Admission runs
    // AFTER pin resolution (so a bad pin is still a 400, not a queue) and identically for both modes. Slot release is
    // exception-safe end to end: the inline path always releases in a finally; the async hand-off releases only if it throws
    // before the executor owns the slot.
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
        RunLimits limits,
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
            return await EnqueueAsync(request, payload, script, scriptHash, payloadId, payloadRevision, runId, tenantId, session, bus, queue, scrubber, ct);
        }

        if (request.Async)
        {
            // The async slot's lifetime is the executor's: it releases it at finalisation (which then promotes the tenant's
            // oldest queued run). A hand-off that faults before the executor owns the slot leaks it until restart recovery —
            // the rare, documented CD-3 exceptional-path edge, unchanged by CD-16.
            return await StartBackgroundRunAsync(runId, payload, script, scriptHash, payloadId, payloadRevision, request.Inputs, session, scrubber, bus, clock, ct);
        }

        // The synchronous slot is held for the whole inline run and freed exception-safely the instant it finishes — including
        // a throw from the secret scope / input parsing inside ExecuteInlineAsync (NIT-1: the release wraps the entire body).
        IResult response;
        try
        {
            response = await ExecuteInlineAsync(runId, payload, scriptHash, payloadId, payloadRevision, request.Inputs, session, registry, sinks, scrubber, secretScope, limits, clock, ct);
        }
        finally
        {
            gate.Release(tenantId, runId);
        }

        // The freed slot promotes the tenant's oldest queued run (a no-op when none is queued) — published AFTER the release so
        // the promotion finds the slot free.
        await bus.PublishAsync(new PromoteQueued(), new DeliveryOptions { TenantId = tenantId });
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
        var inputsJson = request.Inputs.ValueKind == JsonValueKind.Undefined ? "{}" : request.Inputs.GetRawText();

        var queued = new QueuedRunRequest(runId, payloadName, scriptHash, script, inputsJson, [.. input.Keys], payloadId, payloadRevision, ReadDeadlineMs(payload));
        var position = await queue.EnqueueAsync(session, bus, queued, scrubber, ct);

        return Results.Accepted($"/runs/{runId}", new RunStateResponse(runId, RunStatus.Queued, null, null, null, null, Position: position));
    }

    // The synchronous run path (P1–P4, unchanged): open the per-run secret scope, pin RunStarted, interpret, and persist the
    // scrubbed trace + terminal event — all in the request's one transaction. Byte-for-byte the prior behaviour, so every
    // acceptance and parity golden stands. The admitted slot is held for the whole inline run and freed by DispatchAsync's
    // finally (CD-3/NIT-1) — a sync run occupies a slot only for its own duration, unlike an async run the executor releases.
    private static async Task<IResult> ExecuteInlineAsync(
        Guid runId,
        JsonElement payload,
        string scriptHash,
        Guid? payloadId,
        int? payloadRevision,
        JsonElement inputs,
        IDocumentSession session,
        IBrowserBackendRegistry registry,
        IDownloadSinkRegistry sinks,
        CredentialScrubber scrubber,
        IRunSecretScope secretScope,
        RunLimits limits,
        TimeProvider clock,
        CancellationToken ct)
    {
        var input = JsonValues.FromJson(inputs) as Dictionary<string, object?> ?? new(StringComparer.Ordinal);
        var payloadName = payload.TryGetProperty("name", out var name) ? name.GetString()! : "unnamed";

        // Open the per-run secret scope for the whole run: the backend adapter registers the resolved credential into it
        // at connect (§12), so the sinks below scrub even free-form text that echoes the raw secret. Disposal clears it.
        using var runSecrets = secretScope.Begin();

        session.Events.StartStream<Run>(
            runId,
            RunEventScrubber.Scrub(new RunStarted(payloadName, scriptHash, clock.GetUtcNow(), [.. input.Keys], payloadId, payloadRevision), scrubber));

        // The session is already tenant-scoped by Wolverine's HTTP tenant detection, so its tenant is the run's tenant —
        // thread it to the interpreter so a synchronous run's downloads/screenshots land in this tenant's storage (CD-1).
        var outcome = await new RunInterpreter(payload, input, registry, sinks, clock, session.TenantId, limits: limits).RunAsync(ct);

        // The interpreter's trace events (LogEmitted/RunAttemptFailed) land between RunStarted and the terminal event,
        // in occurrence order — each scrubbed at this single append chokepoint, so nothing credential-bearing is persisted.
        foreach (var traceEvent in outcome.Events)
        {
            session.Events.Append(runId, RunEventScrubber.Scrub(traceEvent, scrubber));
        }

        // Scrub the failure once, then use the same value for the persisted event and the §10 response.
        var failure = outcome.Failure is null ? null : RunEventScrubber.ScrubFailure(outcome.Failure, scrubber);
        object finished = outcome.Status == RunStatus.Succeeded
            ? new RunSucceeded(outcome.Stats, clock.GetUtcNow())
            : new RunFailed(failure!, outcome.Stats, clock.GetUtcNow());
        session.Events.Append(runId, finished);

        await session.SaveChangesAsync(ct);

        return Results.Ok(new RunResponse(runId, outcome.Status, scrubber.ScrubJson(outcome.Result), failure, outcome.Stats));
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
        var inputsJson = inputs.ValueKind == JsonValueKind.Undefined ? "{}" : inputs.GetRawText();

        session.Events.StartStream<Run>(
            runId,
            RunEventScrubber.Scrub(new RunStarted(payloadName, scriptHash, clock.GetUtcNow(), [.. input.Keys], payloadId, payloadRevision), scrubber));
        session.Store(new RunProgress { Id = runId, Status = RunStatus.Running });
        await session.SaveChangesAsync(ct);

        await bus.PublishAsync(new StartRun(runId, payloadName, scriptHash, script, inputsJson, payloadId, payloadRevision, ReadDeadlineMs(payload)));

        return Results.Accepted($"/runs/{runId}", new RunStateResponse(runId, RunStatus.Running, null, null, null, null));
    }

    // The run wall-clock cap (§8.4): config.deadlineMs when set, else the generous default.
    private static int ReadDeadlineMs(JsonElement payload) =>
        payload.GetProperty("config").TryGetProperty("deadlineMs", out var deadline) ? deadline.GetInt32() : DefaultDeadlineMs;

    private static string ComputeScriptHash(JsonElement payload) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload.GetRawText())));
}
