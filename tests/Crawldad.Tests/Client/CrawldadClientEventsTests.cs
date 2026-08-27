using System.Net;
using System.Text.Json;
using Crawldad.Client;

namespace Crawldad.Tests.Client;

/// <summary>Unit tests for <c>StreamRunEventsAsync</c> over a stub handler: it yields typed frames (skipping keepalive
/// comments), sets the SSE Accept and Last-Event-ID headers, parses frame ids (numeric, non-numeric, absent), exposes
/// terminal detection and typed data access, and maps a 404 to the typed exception before streaming.</summary>
public class CrawldadClientEventsTests
{
    private static async Task<List<RunEventFrame>> DrainAsync(CrawldadClient client, Guid runId, long? lastEventId = null)
    {
        var frames = new List<RunEventFrame>();
        await foreach (var frame in client.StreamRunEventsAsync(runId, lastEventId))
        {
            frames.Add(frame);
        }

        return frames;
    }

    [Fact]
    public async Task Streams_typed_frames_skipping_keepalives_and_flags_the_terminal_frame()
    {
        const string Sse =
            "id: 1\nevent: RunStarted\ndata: {\"region\":\"fake\"}\n\n" +
            ": keepalive\n\n" +
            "id: 2\nevent: RunSucceeded\ndata: {\"finishedAt\":\"2026-01-01T00:00:00Z\"}\n\n";
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.EventStream(Sse));
        var client = ClientTestHarness.ClientFor(handler);

        var frames = await DrainAsync(client, Guid.NewGuid());

        frames.Count.ShouldBe(2); // the keepalive comment produced no frame
        frames[0].EventType.ShouldBe("RunStarted");
        frames[0].Id.ShouldBe(1);
        frames[0].IsTerminal.ShouldBeFalse();
        frames[0].DataAs<JsonElement>().GetProperty("region").GetString().ShouldBe("fake");
        frames[1].EventType.ShouldBe("RunSucceeded");
        frames[1].Id.ShouldBe(2);
        frames[1].IsTerminal.ShouldBeTrue();

        handler.Last.Accept.ShouldBe("text/event-stream");
    }

    [Fact]
    public async Task Sends_the_last_event_id_header_on_resume()
    {
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.EventStream("id: 6\nevent: RunSucceeded\ndata: {}\n\n"));
        var client = ClientTestHarness.ClientFor(handler);

        await DrainAsync(client, Guid.NewGuid(), lastEventId: 5);

        handler.Last.LastEventId.ShouldBe("5");
    }

    [Fact]
    public async Task Frame_ids_that_are_absent_or_non_numeric_surface_as_null()
    {
        // First frame has no id (absent → null); the second has a non-numeric id (parse fails → null).
        const string Sse = "event: A\ndata: 1\n\nid: xyz\nevent: B\ndata: 2\n\n";
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.EventStream(Sse));
        var client = ClientTestHarness.ClientFor(handler);

        var frames = await DrainAsync(client, Guid.NewGuid());

        frames[0].Id.ShouldBeNull();
        frames[1].Id.ShouldBeNull();
    }

    [Fact]
    public async Task An_unknown_run_is_a_404_before_streaming()
    {
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Empty(HttpStatusCode.NotFound));
        var client = ClientTestHarness.ClientFor(handler);

        await Should.ThrowAsync<CrawldadNotFoundException>(async () => await DrainAsync(client, Guid.NewGuid()));
    }
}
