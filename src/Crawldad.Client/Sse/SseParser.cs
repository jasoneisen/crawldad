using System.Runtime.CompilerServices;
using System.Text;

namespace Crawldad.Client.Sse;

/// <summary>One dispatched Server-Sent Event: the persistent last-event <see cref="Id"/> in effect for this event, the
/// <see cref="EventType"/> (defaulting to <c>message</c> when the stream omits it), and the concatenated
/// <see cref="Data"/>. Comment/keepalive lines never produce one of these.</summary>
/// <param name="Id">The last-event-id in effect (persists across events until a new <c>id:</c> line changes it), or null.</param>
/// <param name="EventType">The event type name from the stream's <c>event:</c> field, or <c>message</c> by default.</param>
/// <param name="Data">The event data, with data lines joined by newlines and the trailing newline stripped.</param>
internal sealed record SseMessage(string? Id, string EventType, string Data);

/// <summary>A minimal, allocation-light reader for the <c>text/event-stream</c> wire format, implementing the WHATWG
/// event-stream parsing rules that matter for the Crawldad trace: <c>id</c>/<c>event</c>/<c>data</c> fields, the single
/// optional space after a field's colon, multi-line <c>data</c>, comment (keepalive) lines beginning with <c>:</c>, and
/// blank-line dispatch. Split out from the HTTP client so it is unit-testable against a plain in-memory stream — frames,
/// keepalives, id carry-forward, and mid-stream cancellation all without a socket.</summary>
internal static class SseParser
{
    /// <summary>Parses <paramref name="stream"/> as an event stream, yielding one <see cref="SseMessage"/> per dispatched
    /// event. Comment lines (keepalives) are honored — they reset nothing and are never yielded. Cancellation is checked
    /// on every line, so a caller that stops enumerating (or cancels <paramref name="ct"/>) tears down promptly even if
    /// the underlying stream still has buffered bytes.</summary>
    /// <param name="stream">The response body stream (UTF-8).</param>
    /// <param name="ct">Cancels the enumeration.</param>
    /// <returns>An async sequence of dispatched events.</returns>
    public static async IAsyncEnumerable<SseMessage> ParseAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var builder = new EventBuilder();

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                yield break; // end of stream
            }

            if (line.Length == 0)
            {
                // Blank line dispatches the buffered event (if it has data), resetting the per-event buffers.
                if (builder.TryDispatch(out var message))
                {
                    yield return message;
                }
            }
            else if (line[0] != ':')
            {
                builder.Consume(line); // ':' opens a comment/keepalive — ignored, resets nothing
            }
        }
    }

    /// <summary>Accumulates one event's fields across lines and dispatches it on a blank line. The last-event-id persists
    /// across dispatches; the event-type and data reset each time.</summary>
    private sealed class EventBuilder
    {
        private readonly StringBuilder _data = new();
        private string? _lastEventId;
        private string _eventType = "";

        /// <summary>Applies one non-blank, non-comment field line to the buffers.</summary>
        public void Consume(string line)
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            string field;
            string value;
            if (colon < 0)
            {
                field = line;
                value = "";
            }
            else
            {
                field = line[..colon];
                value = line[(colon + 1)..];
                if (value.StartsWith(' '))
                {
                    value = value[1..]; // a single leading space after the colon is stripped
                }
            }

            switch (field)
            {
                case "event":
                    _eventType = value;
                    break;
                case "data":
                    _data.Append(value).Append('\n');
                    break;
                case "id" when !value.Contains('\0', StringComparison.Ordinal):
                    _lastEventId = value; // a U+0000 in the id is ignored per spec (the Crawldad stream never sends one)
                    break;
                default:
                    break; // unknown field (e.g. "retry"), or an id containing U+0000 — ignored
            }
        }

        /// <summary>Dispatches the buffered event on a blank line, if it accumulated any data. Resets the data and event
        /// type; the last-event-id persists so a following id-less event inherits it.</summary>
        public bool TryDispatch(out SseMessage message)
        {
            if (_data.Length == 0)
            {
                _eventType = "";
                message = null!;
                return false;
            }

            // Every data line appended exactly one trailing '\n', so a non-empty buffer always ends with one — strip it
            // (the spec's "remove the last U+000A") to get the joined data.
            var data = _data.ToString(0, _data.Length - 1);
            message = new SseMessage(_lastEventId, _eventType.Length == 0 ? "message" : _eventType, data);
            _data.Clear();
            _eventType = "";
            return true;
        }
    }
}
