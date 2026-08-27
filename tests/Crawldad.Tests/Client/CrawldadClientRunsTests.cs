using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Crawldad.Client;
using Crawldad.Contracts.Runs;

namespace Crawldad.Tests.Client;

/// <summary>Unit tests for the runs surface over a stub handler: the sync/async start dichotomy, pinned starts, the
/// read/control endpoints, replay, screenshot fetch (bare + prefixed ref), and the request shape (verb, path, auth
/// header, normalized body).</summary>
public class CrawldadClientRunsTests
{
    private static readonly RunStats _stats = new(0, 0, 0, 0, 0, 0);

    private static JsonElement JsonElementOf(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static readonly JsonElement _payload = JsonElementOf("""{ "crawldad": "1", "name": "demo" }""");

    [Fact]
    public async Task Create_sync_returns_the_terminal_run_response_and_sends_the_expected_request()
    {
        var runId = Guid.NewGuid();
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new RunResponse(runId, RunStatus.Succeeded, JsonElementOf("""{ "ok": true }"""), null, _stats)));
        var client = ClientTestHarness.ClientFor(handler);

        var result = await client.CreateInlineRunAsync(_payload);

        result.IsCompleted.ShouldBeTrue();
        result.RunId.ShouldBe(runId);
        result.Status.ShouldBe(RunStatus.Succeeded);
        result.Completed!.Result!.Value.GetProperty("ok").GetBoolean().ShouldBeTrue();

        handler.Last.Method.ShouldBe(HttpMethod.Post);
        handler.Last.Path.ShouldBe("/runs");
        handler.Last.Authorization.ShouldBe($"Bearer {ClientTestHarness.ApiKey}");
        using var body = JsonDocument.Parse(handler.Last.Body);
        body.RootElement.GetProperty("async").GetBoolean().ShouldBeFalse();
        body.RootElement.GetProperty("payload").GetProperty("name").GetString().ShouldBe("demo");
    }

    [Fact]
    public async Task Create_async_returns_the_accepted_state()
    {
        var runId = Guid.NewGuid();
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new RunStateResponse(runId, RunStatus.Running, null, null, null, null), HttpStatusCode.Accepted));
        var client = ClientTestHarness.ClientFor(handler);

        var result = await client.CreateInlineRunAsync(_payload, async: true);

        result.IsCompleted.ShouldBeFalse();
        result.RunId.ShouldBe(runId);
        result.Status.ShouldBe(RunStatus.Running);
        result.Accepted!.Status.ShouldBe(RunStatus.Running);
        using var body = JsonDocument.Parse(handler.Last.Body);
        body.RootElement.GetProperty("async").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Create_pinned_sends_the_payload_id_and_an_empty_placeholder_payload()
    {
        var payloadId = Guid.NewGuid();
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new RunStateResponse(Guid.NewGuid(), RunStatus.Queued, null, null, null, null, Position: 2), HttpStatusCode.Accepted));
        var client = ClientTestHarness.ClientFor(handler);

        var result = await client.CreatePinnedRunAsync(payloadId, revision: 3, inputs: JsonElementOf("""{ "k": 1 }"""), async: true);

        result.Accepted!.Status.ShouldBe(RunStatus.Queued);
        result.Accepted.Position.ShouldBe(2);
        using var body = JsonDocument.Parse(handler.Last.Body);
        body.RootElement.GetProperty("payloadId").GetGuid().ShouldBe(payloadId);
        body.RootElement.GetProperty("revision").GetInt32().ShouldBe(3);
        // The default (Undefined) payload is normalized to an empty object so it serializes cleanly and the server ignores it.
        body.RootElement.GetProperty("payload").ValueKind.ShouldBe(JsonValueKind.Object);
        body.RootElement.GetProperty("inputs").GetProperty("k").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Create_from_a_raw_request_preserves_supplied_inputs()
    {
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new RunResponse(Guid.NewGuid(), RunStatus.Succeeded, JsonElementOf("null"), null, _stats)));
        var client = ClientTestHarness.ClientFor(handler);

        var request = new StartRunRequest(_payload, JsonElementOf("""{ "backend": { "adapter": "fake" } }"""));
        await client.CreateRunAsync(request);

        using var body = JsonDocument.Parse(handler.Last.Body);
        body.RootElement.GetProperty("inputs").GetProperty("backend").GetProperty("adapter").GetString().ShouldBe("fake");
    }

    [Fact]
    public async Task Get_run_reads_the_state()
    {
        var runId = Guid.NewGuid();
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new RunStateResponse(runId, RunStatus.Succeeded, JsonElementOf("""{ "v": 1 }"""), null, null, _stats)));
        var client = ClientTestHarness.ClientFor(handler);

        var state = await client.GetRunAsync(runId);

        state.RunId.ShouldBe(runId);
        state.Status.ShouldBe(RunStatus.Succeeded);
        handler.Last.Method.ShouldBe(HttpMethod.Get);
        handler.Last.Path.ShouldBe($"/runs/{runId}");
    }

    [Fact]
    public async Task Cancel_posts_with_no_body_and_returns_the_ack()
    {
        var runId = Guid.NewGuid();
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new RunStateResponse(runId, RunStatus.Running, null, null, null, _stats), HttpStatusCode.Accepted));
        var client = ClientTestHarness.ClientFor(handler);

        var ack = await client.CancelRunAsync(runId);

        ack.RunId.ShouldBe(runId);
        handler.Last.Method.ShouldBe(HttpMethod.Post);
        handler.Last.Path.ShouldBe($"/runs/{runId}/cancel");
        handler.Last.Body.ShouldBeEmpty();
    }

    [Fact]
    public async Task Erase_sends_a_delete_and_returns_on_204()
    {
        var runId = Guid.NewGuid();
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Empty(HttpStatusCode.NoContent));
        var client = ClientTestHarness.ClientFor(handler);

        await client.EraseRunAsync(runId);

        handler.Last.Method.ShouldBe(HttpMethod.Delete);
        handler.Last.Path.ShouldBe($"/runs/{runId}");
    }

    [Fact]
    public async Task Replay_from_a_request_returns_the_new_run()
    {
        var newRunId = Guid.NewGuid();
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new RunResponse(newRunId, RunStatus.Succeeded, JsonElementOf("null"), null, _stats)));
        var client = ClientTestHarness.ClientFor(handler);

        var result = await client.ReplayRunAsync(Guid.NewGuid(), new ReplayRunRequest(JsonElementOf("""{ "a": 1 }""")));

        result.RunId.ShouldBe(newRunId);
        handler.Last.Path.ShouldEndWith("/replay");
        using var body = JsonDocument.Parse(handler.Last.Body);
        body.RootElement.GetProperty("inputs").GetProperty("a").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Replay_convenience_overload_normalizes_absent_inputs()
    {
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new RunStateResponse(Guid.NewGuid(), RunStatus.Running, null, null, null, null), HttpStatusCode.Accepted));
        var client = ClientTestHarness.ClientFor(handler);

        await client.ReplayRunAsync(Guid.NewGuid(), async: true);

        using var body = JsonDocument.Parse(handler.Last.Body);
        body.RootElement.GetProperty("inputs").ValueKind.ShouldBe(JsonValueKind.Object); // Undefined normalized to {}
        body.RootElement.GetProperty("async").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Timeline_and_drift_read_their_projections()
    {
        var runId = Guid.NewGuid();
        var timeline = new RunTimelineResponse(
            runId, "demo", "hash", null, null, ["backend"], "fake", RunStatus.Succeeded,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0, [], [], [], [], [], [], null);
        var drift = new RunDriftResponse(runId, null, null, "hash", null, null, false);
        var handler = new StubHttpMessageHandler(request =>
            request.Path.EndsWith("/timeline", StringComparison.Ordinal)
                ? ClientTestHarness.Json(timeline)
                : ClientTestHarness.Json(drift));
        var client = ClientTestHarness.ClientFor(handler);

        (await client.GetRunTimelineAsync(runId)).PayloadName.ShouldBe("demo");
        (await client.GetRunDriftAsync(runId)).Drifted.ShouldBeFalse();
    }

    [Fact]
    public async Task Screenshot_fetch_accepts_a_bare_ref_and_returns_bytes_type_and_etag()
    {
        var runId = Guid.NewGuid();
        var png = Encoding.UTF8.GetBytes("PNGBYTES");
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            response.Headers.ETag = new EntityTagHeaderValue("\"deadbeef\"");
            return response;
        });
        var client = ClientTestHarness.ClientFor(handler);

        var shot = await client.GetRunScreenshotAsync(runId, "abc.png");

        shot.Content.ToArray().ShouldBe(png);
        shot.ContentType.ShouldBe("image/png");
        shot.ETag.ShouldBe("\"deadbeef\"");
        handler.Last.Path.ShouldBe($"/runs/{runId}/screenshots/abc.png");
    }

    [Fact]
    public async Task Screenshot_fetch_strips_a_screenshots_prefix_and_defaults_type_and_etag()
    {
        var runId = Guid.NewGuid();
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        var client = ClientTestHarness.ClientFor(handler);

        var shot = await client.GetRunScreenshotAsync(runId, "screenshots/abc.png");

        handler.Last.Path.ShouldBe($"/runs/{runId}/screenshots/abc.png"); // the screenshots/ prefix was stripped
        shot.ContentType.ShouldBe("image/png"); // defaulted (ByteArrayContent sets no content type)
        shot.ETag.ShouldBeNull();
    }

    [Fact]
    public async Task Screenshot_fetch_percent_encodes_a_traversal_shaped_ref_into_one_segment()
    {
        var runId = Guid.NewGuid();
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) });
        var client = ClientTestHarness.ClientFor(handler);

        await client.GetRunScreenshotAsync(runId, "../../evil.png");

        // The '/' chars are percent-encoded, so the ref stays a single segment under /screenshots/ — the request path
        // never collapses upward to a foreign resource (the no-traversal guarantee is local, not left to the server).
        handler.Last.Path.ShouldBe($"/runs/{runId}/screenshots/..%2F..%2Fevil.png");
        handler.Last.Path.ShouldStartWith($"/runs/{runId}/screenshots/");
        handler.Last.Path.ShouldNotContain("screenshots/../"); // no un-encoded traversal segment survived
    }

    [Fact]
    public async Task Queue_stats_reads_the_snapshot()
    {
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Json(new QueueStatsResponse(3, 10, 1200)));
        var client = ClientTestHarness.ClientFor(handler);

        var stats = await client.GetQueueStatsAsync();

        stats.Queued.ShouldBe(3);
        stats.P95QueueWaitMs.ShouldBe(1200);
        handler.Last.Path.ShouldBe("/runs/queue-stats");
    }

    [Fact]
    public async Task List_runs_with_no_filters_hits_the_bare_path_and_reads_the_page()
    {
        var runId = Guid.NewGuid();
        var row = new RunListItem(runId, RunStatus.Succeeded, DateTimeOffset.UnixEpoch, 1500, null, "demo", null, null, Inline: true, null, null);
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new RunListResponse([row], 1, 25, 1, HasMore: false)));
        var client = ClientTestHarness.ClientFor(handler);

        var page = await client.ListRunsAsync();

        page.Total.ShouldBe(1);
        page.HasMore.ShouldBeFalse();
        page.Runs.ShouldHaveSingleItem().RunId.ShouldBe(runId);
        handler.Last.Method.ShouldBe(HttpMethod.Get);
        handler.Last.Path.ShouldBe("/runs");
        handler.Last.Query.ShouldBeEmpty(); // no filter/paging params => the bare path, no query string
        handler.Last.Authorization.ShouldBe($"Bearer {ClientTestHarness.ApiKey}");
    }

    [Fact]
    public async Task List_runs_encodes_every_filter_and_paging_parameter()
    {
        var payloadId = Guid.NewGuid();
        var from = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 8, 27, 23, 59, 59, TimeSpan.Zero);
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new RunListResponse([], 2, 50, 400, HasMore: true)));
        var client = ClientTestHarness.ClientFor(handler);

        var page = await client.ListRunsAsync(RunStatus.Failed, payloadId, from, to, page: 2, size: 50);

        page.HasMore.ShouldBeTrue();
        page.Page.ShouldBe(2);
        page.Size.ShouldBe(50);
        handler.Last.Path.ShouldBe("/runs");

        var query = handler.Last.Query;
        query.ShouldContain("status=Failed");
        query.ShouldContain($"payloadId={payloadId}");
        query.ShouldContain("page=2");
        query.ShouldContain("size=50");
        // The ISO-8601 bounds are URL-encoded — the '+' offset and ':' separators escape rather than ride raw.
        query.ShouldContain($"from={Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))}");
        query.ShouldContain($"to={Uri.EscapeDataString(to.ToString("O", CultureInfo.InvariantCulture))}");
        query.ShouldNotContain("+00:00");
    }
}
