using System.Globalization;
using System.Runtime.CompilerServices;
using Crawldad.Client.Sse;

namespace Crawldad.Client;

/// <summary>The Server-Sent Events trace surface.</summary>
public sealed partial class CrawldadClient
{
    /// <summary>Streams a run's trace as typed frames (<c>GET /runs/{id}/events</c>). On connect the server backfills the
    /// durable stream from <paramref name="lastEventId"/> (resume exactly on reconnect — no frame lost or duplicated),
    /// then follows the live tail until a terminal frame closes it. Keepalive comment frames are consumed silently.
    /// Enumeration is fully cancellable: stop iterating (or cancel <paramref name="ct"/>) and the connection tears down.</summary>
    /// <param name="runId">The run whose trace to stream.</param>
    /// <param name="lastEventId">The last frame id already seen, to resume after it; null streams from the start.</param>
    /// <param name="ct">Cancels the stream.</param>
    /// <returns>An async sequence of trace frames, ending with a terminal frame (<see cref="RunEventFrame.IsTerminal"/>).</returns>
    /// <exception cref="CrawldadNotFoundException">No such run for this tenant (<c>404</c>).</exception>
    public async IAsyncEnumerable<RunEventFrame> StreamRunEventsAsync(
        Guid runId, long? lastEventId = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = await BuildRequestAsync(HttpMethod.Get, $"runs/{runId}/events", ct);
        request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
        if (lastEventId is not null)
        {
            request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId.Value.ToString(CultureInfo.InvariantCulture));
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateErrorAsync(response, ct);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await foreach (var message in SseParser.ParseAsync(stream, ct))
        {
            yield return new RunEventFrame(ParseFrameId(message.Id), message.EventType, message.Data);
        }
    }

    private static long? ParseFrameId(string? id) =>
        id is not null && long.TryParse(id, CultureInfo.InvariantCulture, out var value) ? value : null;
}
