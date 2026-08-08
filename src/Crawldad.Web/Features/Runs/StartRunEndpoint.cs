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
/// By default the run executes <b>synchronously</b>: the handler owns its Marten session inline, interprets the payload, and
/// returns the terminal <see cref="RunResponse"/> — the exact P1–P4 behaviour, byte-for-byte. With <c>async: true</c> it
/// hands the run to the durable executor saga (§11): it pins <c>RunStarted</c>, seeds the running <see cref="RunProgress"/>,
/// kicks <see cref="StartRun"/>, and returns <c>202</c> with <c>{ runId, status:"running" }</c> for the caller to poll via
/// <c>GET /runs/{id}</c>.
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
    /// <param name="bus">The message bus that kicks the executor saga for an async run (§11).</param>
    /// <param name="clock">The time seam for event timestamps and duration.</param>
    /// <param name="ct">Cancels the run.</param>
    /// <returns><c>200</c> with the §10 run response (sync), <c>202</c> with the running state (async), or <c>400</c> for an unrunnable pin.</returns>
    [WolverinePost("/runs")]
    public static async Task<IResult> Handle(
        StartRunRequest request,
        IDocumentSession session,
        IBrowserBackendRegistry registry,
        IDownloadSinkRegistry sinks,
        CredentialScrubber scrubber,
        IRunSecretScope secretScope,
        IMessageBus bus,
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
                session, registry, sinks, scrubber, secretScope, bus, limits, clock, ct);
        }

        return await DispatchAsync(
            request, request.Payload, request.Payload.GetRawText(), ComputeScriptHash(request.Payload), null, null,
            session, registry, sinks, scrubber, secretScope, bus, limits, clock, ct);
    }

    // Routes the resolved payload to the synchronous inline path (default) or the async executor-saga path.
    private static Task<IResult> DispatchAsync(
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
        RunLimits limits,
        TimeProvider clock,
        CancellationToken ct) =>
        request.Async
            ? StartBackgroundRunAsync(payload, script, scriptHash, payloadId, payloadRevision, request.Inputs, session, scrubber, bus, clock, ct)
            : ExecuteInlineAsync(payload, scriptHash, payloadId, payloadRevision, request.Inputs, session, registry, sinks, scrubber, secretScope, limits, clock, ct);

    // The synchronous run path (P1–P4, unchanged): open the per-run secret scope, pin RunStarted, interpret, and persist the
    // scrubbed trace + terminal event — all in the request's one transaction. Byte-for-byte the prior behaviour, so every
    // acceptance and parity golden stands.
    private static async Task<IResult> ExecuteInlineAsync(
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
        var runId = Guid.NewGuid();
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
        var runId = Guid.NewGuid();
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
