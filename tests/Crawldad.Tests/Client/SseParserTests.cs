using System.Text;
using Crawldad.Client.Sse;

namespace Crawldad.Tests.Client;

/// <summary>Unit tests for the client's <see cref="SseParser"/> — the <c>text/event-stream</c> line reader that powers
/// <c>StreamRunEventsAsync</c>. Exercised directly against in-memory streams: frames, multi-line data, keepalive
/// comments, the optional single space, id carry-forward, empty dispatch, and mid-stream cancellation.</summary>
public class SseParserTests
{
    private static MemoryStream StreamOf(string text) => new(Encoding.UTF8.GetBytes(text));

    private static async Task<List<SseMessage>> ParseAllAsync(string text, CancellationToken ct = default)
    {
        var messages = new List<SseMessage>();
        await foreach (var message in SseParser.ParseAsync(StreamOf(text), ct))
        {
            messages.Add(message);
        }

        return messages;
    }

    [Fact]
    public async Task Parses_a_single_frame_with_id_event_and_data()
    {
        var messages = await ParseAllAsync("id: 7\nevent: RunStarted\ndata: {\"a\":1}\n\n");

        var message = messages.ShouldHaveSingleItem();
        message.Id.ShouldBe("7");
        message.EventType.ShouldBe("RunStarted");
        message.Data.ShouldBe("{\"a\":1}");
    }

    [Fact]
    public async Task Parses_multiple_frames_in_order()
    {
        var messages = await ParseAllAsync(
            "id: 1\nevent: RunStarted\ndata: a\n\nid: 2\nevent: RunSucceeded\ndata: b\n\n");

        messages.Select(m => m.EventType).ShouldBe(["RunStarted", "RunSucceeded"]);
        messages.Select(m => m.Id).ShouldBe(["1", "2"]);
    }

    [Fact]
    public async Task Joins_multi_line_data_with_newlines_and_strips_the_trailing_one()
    {
        var messages = await ParseAllAsync("data: line1\ndata: line2\n\n");

        messages.ShouldHaveSingleItem().Data.ShouldBe("line1\nline2");
    }

    [Fact]
    public async Task Defaults_the_event_type_to_message_when_absent()
    {
        var messages = await ParseAllAsync("data: hi\n\n");

        messages.ShouldHaveSingleItem().EventType.ShouldBe("message");
    }

    [Fact]
    public async Task Ignores_keepalive_comment_frames()
    {
        var messages = await ParseAllAsync(": keepalive\n\nid: 5\nevent: StepStarted\ndata: x\n\n");

        var message = messages.ShouldHaveSingleItem(); // the comment produced no event
        message.Id.ShouldBe("5");
        message.EventType.ShouldBe("StepStarted");
    }

    [Fact]
    public async Task Strips_exactly_one_leading_space_and_tolerates_none()
    {
        var messages = await ParseAllAsync("data: withspace\n\ndata:nospace\n\ndata:  two\n\n");

        messages.Select(m => m.Data).ShouldBe(["withspace", "nospace", " two"]); // only the first space is removed
    }

    [Fact]
    public async Task Carries_the_last_event_id_forward_to_a_following_frame_without_one()
    {
        var messages = await ParseAllAsync("id: 9\ndata: a\n\ndata: b\n\n");

        messages.Select(m => m.Id).ShouldBe(["9", "9"]); // the second frame inherits the persistent last-event-id
    }

    [Fact]
    public async Task A_blank_line_with_no_data_dispatches_nothing()
    {
        var messages = await ParseAllAsync("\n\nevent: OnlyEvent\n\ndata: real\n\n");

        // The leading blank lines and the data-less "event only" frame dispatch nothing; only the real frame does.
        var message = messages.ShouldHaveSingleItem();
        message.EventType.ShouldBe("message");
        message.Data.ShouldBe("real");
    }

    [Fact]
    public async Task Ignores_unknown_fields_and_an_id_containing_a_null_char()
    {
        // "retry" is an unknown field; the id carries a U+0000 so it is ignored (last-event-id stays null).
        var input = $"retry: 1000\nid: a{(char)0}b\ndata: x\n\n";
        var messages = await ParseAllAsync(input);

        var message = messages.ShouldHaveSingleItem();
        message.Id.ShouldBeNull();
        message.Data.ShouldBe("x");
    }

    [Fact]
    public async Task A_field_line_with_no_colon_is_a_field_with_empty_value()
    {
        // "data" with no colon appends an empty value, so the dispatched frame has empty data.
        var messages = await ParseAllAsync("data\n\n");

        messages.ShouldHaveSingleItem().Data.ShouldBe("");
    }

    [Fact]
    public async Task Ends_cleanly_at_end_of_stream()
    {
        (await ParseAllAsync("")).ShouldBeEmpty(); // EOF with nothing buffered
    }

    [Fact]
    public async Task Honors_cancellation_mid_stream()
    {
        using var cts = new CancellationTokenSource();
        var enumerator = SseParser.ParseAsync(StreamOf("data: a\n\ndata: b\n\n"), cts.Token).GetAsyncEnumerator();
        try
        {
            (await enumerator.MoveNextAsync()).ShouldBeTrue();
            enumerator.Current.Data.ShouldBe("a");

            await cts.CancelAsync();

            // The parser checks cancellation at the top of every line read, so the next advance throws even though the
            // rest of the frame is already buffered in memory.
            await Should.ThrowAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }
}
