using System.Globalization;
using System.Text.Json;
using Crawldad.Web.Infrastructure.Security;
using JasperFx.Events;
using Marten;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace Crawldad.Web.Features.Runs;

/// <summary>
/// <c>GET /runs/{id}/events</c> (§11 SSE): streams a run's trace as Server-Sent Events. On (re)connect it <b>backfills from
/// the durable Marten stream</b> from a client-supplied last-seen sequence (the <c>Last-Event-ID</c> header, or a
/// <c>lastEventId</c> query param), then follows the live tail until the run reaches a terminal event and closes. The frame
/// <c>id</c> is the event's stream version, so a reconnect with <c>Last-Event-ID</c> continues <b>exactly</b> where it left
/// off — no frame lost or duplicated across a disconnect, because the durable stream (not an in-memory buffer) is the
/// authoritative source; the in-process <see cref="RunEventSignals"/> only wakes the tail with low latency. Frames carry
/// already-<b>scrubbed</b> event data (§12), so nothing credential-bearing streams. An unknown run is <c>404</c>.
/// </summary>
public static class RunEventsEndpoint
{
    /// <summary>Handles <c>GET /runs/{id}/events</c> by returning the SSE streaming result.</summary>
    /// <param name="id">The run to stream.</param>
    /// <param name="store">The Marten store (the durable frame source).</param>
    /// <param name="signals">The in-process tail-wakeup hub.</param>
    /// <param name="tenant">The authenticated tenant — scopes the stream to this tenant's runs (CD-1).</param>
    /// <returns>The SSE streaming <see cref="IResult"/>.</returns>
    [WolverineGet("/runs/{id}/events")]
    public static IResult Handle(Guid id, IDocumentStore store, RunEventSignals signals, TenantContext tenant) =>
        new RunEventStream(id, store, signals, tenant.TenantId);
}

/// <summary>The SSE streaming result for one run (§11): backfill-from-durable-stream then live-tail-until-terminal.</summary>
/// <param name="runId">The run to stream.</param>
/// <param name="store">The Marten store (a fresh query session per read, so each read sees the latest committed events).</param>
/// <param name="signals">The tail-wakeup hub.</param>
/// <param name="tenantId">The tenant the query sessions are scoped to — a run in another tenant is unreadable here, so a
/// cross-tenant stream is an empty stream and answers 404 exactly as an unknown run does (CD-1).</param>
internal sealed class RunEventStream(Guid runId, IDocumentStore store, RunEventSignals signals, string tenantId) : IResult
{
    // The tail poll backstop: a missed in-process wakeup only defers a re-read by at most this long — the durable re-read is
    // the correctness guarantee, this bounds latency (and lets a disconnect be noticed promptly).
    private static readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(100);

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var ct = httpContext.RequestAborted;
        try
        {
            // Existence check before any SSE headers, so an unknown run is a clean 404 (not a half-open stream).
            if ((await FetchAsync(ct)).Count == 0)
            {
                httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var response = httpContext.Response;
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache";
            response.Headers["X-Accel-Buffering"] = "no"; // no proxy buffering — frames must flush as they are written
            await response.Body.FlushAsync(ct);

            await TailAsync(response, ParseLastEventId(httpContext), signals.For(runId), ct);
        }
        catch (OperationCanceledException)
        {
            // the client disconnected (RequestAborted) — stop tailing quietly
        }
    }

    // Backfills then follows the live tail: each pass re-reads the durable stream and writes frames past the last delivered
    // version (so nothing is duplicated), closing once the run's last event is terminal. Between passes it waits on the
    // in-process wakeup with a poll backstop — captured BEFORE the read so a notify during the read is never missed.
    private async Task TailAsync(HttpResponse response, long lastSent, RunSignal signal, CancellationToken ct)
    {
        while (true)
        {
            var changed = signal.Changed;
            var events = await FetchAsync(ct);
            foreach (var e in events)
            {
                if (e.Version <= lastSent)
                {
                    continue; // already delivered (backfill from Last-Event-ID, or a prior tail pass)
                }

                await response.WriteAsync(RunEventFrames.Format(e.Version, e.EventType.Name, e.Data), ct);
                lastSent = e.Version;
            }

            await response.Body.FlushAsync(ct);
            if (RunEventFrames.IsTerminal(events[^1].EventType))
            {
                return; // the run's last event is terminal and has been delivered — close the stream
            }

            try
            {
                await changed.WaitAsync(_pollInterval, ct);
            }
            catch (TimeoutException)
            {
                // no wakeup within the poll window — re-read anyway (the backstop)
            }
        }
    }

    // A fresh query session per read, so each read sees the latest committed events (no session-level snapshot). Scoped to
    // the request's tenant (CD-1): a stream id belonging to another tenant fetches nothing, so it 404s like an unknown run.
    private async Task<IReadOnlyList<IEvent>> FetchAsync(CancellationToken token)
    {
        await using var session = store.QuerySession(tenantId);
        return await session.Events.FetchStreamAsync(runId, token: token);
    }

    /// <summary>Reads the client's last-seen sequence (§11 reconnect): the <c>Last-Event-ID</c> header first, then a
    /// <c>lastEventId</c> query param, else 0 (stream from the start). A non-numeric value is ignored.</summary>
    /// <param name="httpContext">The request.</param>
    /// <returns>The last-seen stream version, or 0 to start from the beginning.</returns>
    internal static long ParseLastEventId(HttpContext httpContext)
    {
        if (httpContext.Request.Headers.TryGetValue("Last-Event-ID", out var header)
            && long.TryParse(header.ToString(), CultureInfo.InvariantCulture, out var fromHeader))
        {
            return fromHeader;
        }

        if (httpContext.Request.Query.TryGetValue("lastEventId", out var query)
            && long.TryParse(query.ToString(), CultureInfo.InvariantCulture, out var fromQuery))
        {
            return fromQuery;
        }

        return 0;
    }
}

/// <summary>The SSE frame codec (§11), split out so its formatting + terminal-detection are unit-testable without a live stream.</summary>
internal static class RunEventFrames
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>The terminal trace events that close an SSE tail — the run reached one of these and there is nothing more to stream.</summary>
    /// <param name="eventType">The event's CLR type.</param>
    /// <returns>True when the event ends the run.</returns>
    public static bool IsTerminal(Type eventType) =>
        eventType == typeof(RunSucceeded) || eventType == typeof(RunFailed) || eventType == typeof(RunCancelled);

    /// <summary>Formats one SSE frame: the stream <c>version</c> as the frame <c>id</c> (so <c>Last-Event-ID</c> resumes
    /// exactly), the event's CLR type name as <c>event</c>, and the (already-scrubbed) event data as JSON <c>data</c>.</summary>
    /// <param name="version">The event's stream version (the frame id).</param>
    /// <param name="eventName">The event's CLR type name.</param>
    /// <param name="data">The event data (scrubbed by construction — it comes from the persisted stream).</param>
    /// <returns>The SSE frame text (a single event terminated by a blank line).</returns>
    public static string Format(long version, string eventName, object data) =>
        $"id: {version}\nevent: {eventName}\ndata: {JsonSerializer.Serialize(data, data.GetType(), _json)}\n\n";
}
