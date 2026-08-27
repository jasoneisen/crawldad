using System.Globalization;
using System.Text.Json;
using Crawldad.Api.Infrastructure.Security;
using JasperFx.Events;
using Marten;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace Crawldad.Api.Features.Runs;

/// <summary><c>GET /runs/{id}/events</c>: streams a run's trace as Server-Sent Events. On (re)connect it backfills from
/// the durable Marten stream from a client-supplied last-seen sequence, then follows the live tail until a terminal
/// event closes it. The frame <c>id</c> is the stream version, so a reconnect resumes exactly — no frame lost or duplicated.</summary>
public static class RunEventsEndpoint
{
    /// <summary>Handles <c>GET /runs/{id}/events</c> by returning the SSE streaming result.</summary>
    [WolverineGet("/runs/{id}/events")]
    public static IResult Handle(Guid id, IDocumentStore store, RunEventSignals signals, TenantContext tenant, TimeProvider clock) =>
        new RunEventStream(id, store, signals, tenant.TenantId, clock);
}

/// <summary>The SSE streaming result for one run: backfill-from-durable-stream then live-tail-until-terminal. Query
/// sessions are scoped to <paramref name="tenantId"/>, so a run in another tenant fetches nothing and 404s exactly
/// like an unknown run.</summary>
internal sealed class RunEventStream(Guid runId, IDocumentStore store, RunEventSignals signals, string tenantId, TimeProvider clock) : IResult
{
    // The tail poll backstop: a missed in-process wakeup only defers a re-read by at most this long — the durable re-read is
    // the correctness guarantee, this bounds latency (and lets a disconnect be noticed promptly).
    private static readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(100);

    // The SSE idle keepalive cadence: after this long with nothing written, the tail emits a comment frame so an
    // intermediary's idle timeout (Azure Front Door / Container Apps Envoy / corporate proxies, commonly 60–240 s) never
    // drops a quiet stream mid-run — the load-bearing 15 s heartbeat ARCHITECTURE.md §B.1 relies on. Hardcoded rather than
    // an option: a single, rarely-tuned cadence with no natural home among the resource-limit knobs, pinned to the
    // documented 15 s (a real frame resets it, so it fires only across a genuine gap between a run's events).
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

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
        var heartbeat = new SseHeartbeat(clock, HeartbeatInterval);
        while (true)
        {
            var changed = signal.Changed;
            var events = await FetchAsync(ct);
            var wroteFrame = false;
            foreach (var e in events)
            {
                if (e.Version <= lastSent)
                {
                    continue; // already delivered (backfill from Last-Event-ID, or a prior tail pass)
                }

                await response.WriteAsync(RunEventFrames.Format(e.Version, e.EventType.Name, e.Data), ct);
                lastSent = e.Version;
                wroteFrame = true;
            }

            // A real frame resets the idle window; an otherwise-silent pass emits a keepalive comment once the window
            // elapses, so an intermediary's idle timeout never drops a quiet stream mid-run. The comment carries no id,
            // so Last-Event-ID resume is untouched — and there is no background timer, so a teardown has nothing to leak.
            if (wroteFrame)
            {
                heartbeat.MarkWritten();
            }
            else if (heartbeat.IsDue())
            {
                await response.WriteAsync(RunEventFrames.Keepalive, ct);
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
    // the request's tenant: a stream id belonging to another tenant fetches nothing, so it 404s like an unknown run.
    private async Task<IReadOnlyList<IEvent>> FetchAsync(CancellationToken token)
    {
        await using var session = store.QuerySession(tenantId);
        return await session.Events.FetchStreamAsync(runId, token: token);
    }

    /// <summary>Reads the client's last-seen sequence for reconnect: the <c>Last-Event-ID</c> header first, then a
    /// <c>lastEventId</c> query param, else 0 (stream from the start). A non-numeric value is ignored.</summary>
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

/// <summary>The SSE frame codec, split out so its formatting + terminal-detection are unit-testable without a live stream.</summary>
internal static class RunEventFrames
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>The idle keepalive: an SSE comment frame (leading <c>:</c>) carrying no <c>id</c>/<c>event</c>/<c>data</c>,
    /// so it resets an intermediary's idle timer without disturbing <c>Last-Event-ID</c> resume or surfacing to an
    /// EventSource consumer's message handlers.</summary>
    public const string Keepalive = ": keepalive\n\n";

    /// <summary>The terminal trace events that close an SSE tail — the run reached one of these and there is nothing more to stream.</summary>
    public static bool IsTerminal(Type eventType) =>
        eventType == typeof(RunSucceeded) || eventType == typeof(RunFailed) || eventType == typeof(RunCancelled);

    /// <summary>Formats one SSE frame: the stream <c>version</c> as the frame <c>id</c> (so <c>Last-Event-ID</c> resumes
    /// exactly), the event's CLR type name as <c>event</c>, and the (already-scrubbed) event data as JSON <c>data</c>.</summary>
    public static string Format(long version, string eventName, object data) =>
        $"id: {version}\nevent: {eventName}\ndata: {JsonSerializer.Serialize(data, data.GetType(), _json)}\n\n";
}

/// <summary>The SSE tail's idle keepalive clock: decides when a comment frame is due so an intermediary's idle timeout
/// never drops a quiet stream mid-run. Reset-on-traffic — a real frame pushes the next keepalive a full interval out,
/// since an idle timer only needs resetting when nothing else is flowing. Driven purely off the injected
/// <see cref="TimeProvider"/> (no background timer to leak on teardown), so it is deterministic under a controllable
/// clock and inert under a frozen one.</summary>
internal sealed class SseHeartbeat(TimeProvider clock, TimeSpan interval)
{
    private DateTimeOffset _lastWrite = clock.GetUtcNow();

    /// <summary>Records a real frame write, resetting the idle window so the next keepalive is a full interval away.</summary>
    public void MarkWritten() => _lastWrite = clock.GetUtcNow();

    /// <summary>Whether a full idle interval has elapsed since the last write; when true it resets the window, so
    /// keepalives pace at the interval rather than firing on every poll once overdue.</summary>
    public bool IsDue()
    {
        var now = clock.GetUtcNow();
        if (now - _lastWrite < interval)
        {
            return false;
        }

        _lastWrite = now;
        return true;
    }
}
