using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Crawldad.Contracts.Runs;
using Crawldad.Web.Features.Runs.Interpreter;
using Crawldad.Web.Infrastructure.Browser;
using Crawldad.Web.Infrastructure.Storage;
using Marten;
using Wolverine.Http;

namespace Crawldad.Web.Features.Runs;

/// <summary>
/// <c>POST /runs</c> (§10, Deliverable 4): executes an inline payload <b>synchronously</b> and returns the shaped
/// result or a typed failure — both HTTP 200 (a failed <em>run</em> is not a failed request). The handler owns its
/// Marten session inline (the P1 synchronous design): <c>StartStream(RunStarted)</c> → run the interpreter →
/// append <c>RunSucceeded</c>/<c>RunFailed</c> → save. A malformed request is a 400 via the FluentValidation
/// middleware. The long-running executor saga (§14.2) replaces the inline execution in Phase 5.
/// </summary>
public static class StartRunEndpoint
{
    /// <summary>Handles <c>POST /runs</c>.</summary>
    /// <param name="request">The inline payload + inputs.</param>
    /// <param name="session">The request-scoped Marten session (Wolverine tracked-session compatible).</param>
    /// <param name="registry">Resolves the backend adapter named by the payload's <c>config.backend</c>.</param>
    /// <param name="sinks">Resolves the download sink named by a <c>download.to</c> target's <c>kind</c> (§9.3).</param>
    /// <param name="clock">The time seam for event timestamps and duration.</param>
    /// <param name="ct">Cancels the run.</param>
    /// <returns>The §10 run response.</returns>
    [WolverinePost("/runs")]
    public static async Task<RunResponse> Handle(
        StartRunRequest request,
        IDocumentSession session,
        IBrowserBackendRegistry registry,
        IDownloadSinkRegistry sinks,
        TimeProvider clock,
        CancellationToken ct)
    {
        var runId = Guid.NewGuid();
        var input = JsonValues.FromJson(request.Inputs) as Dictionary<string, object?> ?? new(StringComparer.Ordinal);
        var payloadName = request.Payload.TryGetProperty("name", out var name) ? name.GetString()! : "unnamed";

        session.Events.StartStream<Run>(
            runId,
            new RunStarted(payloadName, ComputeScriptHash(request.Payload), clock.GetUtcNow(), [.. input.Keys]));

        var outcome = await new RunInterpreter(request.Payload, input, registry, sinks, clock).RunAsync(ct);

        // The interpreter's trace events (LogEmitted/RunAttemptFailed) land between RunStarted and the terminal event,
        // in occurrence order — part of the run's observable trace whether it ultimately succeeds or fails (§13).
        foreach (var traceEvent in outcome.Events)
        {
            session.Events.Append(runId, traceEvent);
        }

        object finished = outcome.Status == RunStatus.Succeeded
            ? new RunSucceeded(outcome.Stats, clock.GetUtcNow())
            : new RunFailed(outcome.Failure!, outcome.Stats, clock.GetUtcNow());
        session.Events.Append(runId, finished);

        await session.SaveChangesAsync(ct);

        return new RunResponse(runId, outcome.Status, outcome.Result, outcome.Failure, outcome.Stats);
    }

    private static string ComputeScriptHash(JsonElement payload) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload.GetRawText())));
}
