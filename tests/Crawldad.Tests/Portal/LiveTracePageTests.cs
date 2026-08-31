using System.IO;
using System.Net;
using Bunit;
using Crawldad.Contracts.Runs;
using Crawldad.Portal.Components.Pages.App;
using Crawldad.Portal.Runs;
using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Portal;

/// <summary>bUnit rendering of the interactive live-trace component over a scripted SSE transport: the not-linked state,
/// live frame-by-frame rendering, the terminal completion banner + run-detail link, Last-Event-ID resume across a
/// dropped tail, the bounded-reconnect disconnected state + manual reconnect, friendly API-error states, the
/// timeline fallback for an erased-events run, auto-scroll toggling, the rolling event cap, and disposal tearing the
/// SSE connection down.</summary>
public class LiveTracePageTests : BunitContext
{
    private static readonly Guid _runId = new("2a7c5e19-0000-0000-0000-000000000001");
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(10);

    public LiveTracePageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupVoid("scrollTo", _ => true).SetVoidResult(); // the auto-scroll window hop — recorded + completed
    }

    private static string Frame(long id, string type, string data = "{}") =>
        $"id: {id}\nevent: {type}\ndata: {data}\n\n";

    private static string FrameNoId(string type, string data = "{}") =>
        $"event: {type}\ndata: {data}\n\n"; // an id-less frame — the row shows "—" and the resume cursor is unchanged

    // Renders the component with its tenant client riding the given transport (owned by the HttpClient the SDK holds).
    private IRenderedComponent<LiveTrace> RenderLive(
        Func<CapturedRequest, HttpResponseMessage> responder, int maxReconnect = 5, int maxEvents = 1000)
    {
        var handler = new StubHttpMessageHandler(responder);
        Services.AddSingleton<ICircuitTenantResolver>(new FakeCircuitTenantResolver(PortalRunsTestSupport.TenantOver(handler)));
        return Render<LiveTrace>(p => p
            .Add(x => x.RunId, _runId)
            .Add(x => x.MaxReconnectAttempts, maxReconnect)
            .Add(x => x.MaxEvents, maxEvents));
    }

    [Fact]
    public void Not_linked_shows_the_link_workspace_empty_state()
    {
        Services.AddSingleton<ICircuitTenantResolver>(new FakeCircuitTenantResolver(tenant: null));

        var cut = Render<LiveTrace>(p => p.Add(x => x.RunId, _runId));

        cut.Find("[data-testid=not-linked]").ShouldNotBeNull();
        cut.Markup.ShouldContain("No workspace yet");
        cut.FindAll("[data-testid=status-header]").ShouldBeEmpty(); // nothing streamed
    }

    [Fact]
    public void Streams_frames_as_they_arrive_updating_counts_status_and_the_last_event_id()
    {
        var content = new PushSseContent();
        var cut = RenderLive(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        content.Push(Frame(1, "RunStarted", "{\"region\":\"eu-west-1\"}"));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=event-row]").Count.ShouldBe(1), _wait);

        content.Push(Frame(2, "StepStarted", "{\"index\":1,\"kind\":\"goto\"}"));
        content.Push(Frame(3, "SelectorMiss", "{\"selector\":\"#x\"}"));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=event-row]").Count.ShouldBe(3), _wait);

        cut.Find("[data-testid=count-steps]").TextContent.Trim().ShouldBe("1");
        cut.Find("[data-testid=count-misses]").TextContent.Trim().ShouldBe("1");
        cut.Find("[data-testid=count-events]").TextContent.Trim().ShouldBe("3");
        cut.Find("[data-testid=phase-note]").TextContent.ShouldContain("Following the live tail");
        cut.Markup.ShouldContain("Last-Event-ID 3"); // resume cursor tracks the newest frame id
        cut.Find("[data-testid=live-cursor]").ShouldNotBeNull(); // still live → the blinking tail cursor shows
    }

    [Fact]
    public void An_id_less_leading_frame_renders_a_dash_and_leaves_the_resume_cursor_unset()
    {
        var content = new PushSseContent();
        var cut = RenderLive(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        content.Push(FrameNoId("RunStarted", "{\"region\":\"eu-west-1\"}")); // no id → Id is null (nothing to carry forward)
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=event-row]").Count.ShouldBe(1), _wait);

        cut.Find("[data-testid=event-row] .id").TextContent.ShouldBe("—"); // id-less row shows the dash
        cut.Markup.ShouldNotContain("Last-Event-ID");                      // no numbered frame yet → resume cursor unset
    }

    [Fact]
    public void A_terminal_frame_shows_the_completion_banner_and_run_detail_link()
    {
        var sse = Frame(1, "RunStarted") + Frame(2, "StepStarted") + Frame(3, "RunSucceeded", "{\"finishedAt\":\"2026-08-27T00:00:00Z\"}");
        var cut = RenderLive(_ => ClientTestHarness.EventStream(sse));

        cut.WaitForAssertion(() => cut.Find("[data-testid=completion]").ShouldNotBeNull(), _wait);
        cut.Markup.ShouldContain("Run succeeded");
        cut.Find($"[data-testid=completion-link][href=\"/app/runs/{_runId}\"]").ShouldNotBeNull();
        cut.FindAll("[data-testid=live-cursor]").ShouldBeEmpty(); // no longer live
    }

    [Fact]
    public void A_failed_terminal_frame_headlines_the_failure()
    {
        var sse = Frame(1, "RunStarted") + Frame(2, "RunFailed", "{\"failure\":{\"code\":\"record_not_accessible\"}}");
        var cut = RenderLive(_ => ClientTestHarness.EventStream(sse));

        cut.WaitForAssertion(() => cut.Find("[data-testid=completion]").ShouldNotBeNull(), _wait);
        cut.Markup.ShouldContain("Run failed");
        cut.Find("[data-testid=completion].run-failure-banner").ShouldNotBeNull();
    }

    [Fact]
    public void A_mid_stream_drop_resumes_from_the_last_event_id()
    {
        // First attempt streams one frame then the connection resets mid-tail; the resume (a second, still-open stream)
        // delivers the terminal frame.
        var first = new PushSseContent();
        var resume = new PushSseContent();
        var handler = new StubHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = req.LastEventId is null ? first : resume,
        });
        Services.AddSingleton<ICircuitTenantResolver>(new FakeCircuitTenantResolver(PortalRunsTestSupport.TenantOver(handler)));

        var cut = Render<LiveTrace>(p => p.Add(x => x.RunId, _runId));

        first.Push(Frame(5, "StepStarted"));
        first.Fault(new IOException("connection reset")); // a transient drop, not a clean close

        // The page shows the resuming state while it reconnects (blocked awaiting the resume stream)...
        cut.WaitForAssertion(() => cut.Find("[data-testid=phase-note]").TextContent.ShouldContain("resuming"), _wait);

        // ...then the resume delivers the terminal frame.
        resume.Push(Frame(6, "RunSucceeded"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=completion]").ShouldNotBeNull(), _wait);
        cut.Markup.ShouldContain("Run succeeded");
        // The resume request carried Last-Event-ID = the last frame seen before the drop.
        handler.Requests[0].LastEventId.ShouldBeNull(); // first attempt started fresh
        handler.Requests[^1].LastEventId.ShouldBe("5"); // resume picked up exactly after frame 5
    }

    [Fact]
    public void Reconnects_are_bounded_then_the_disconnected_state_offers_a_manual_reconnect()
    {
        var calls = 0;
        var cut = RenderLive(
            _ =>
            {
                calls++;
                return calls <= 3
                    ? ClientTestHarness.EventStream("")                          // empty tail closes → a drop with no progress
                    : ClientTestHarness.EventStream(Frame(9, "RunSucceeded"));   // the manual reconnect succeeds
            },
            maxReconnect: 2);

        cut.WaitForAssertion(() => cut.Find("[data-testid=disconnected]").ShouldNotBeNull(), _wait);

        cut.Find("[data-testid=reconnect]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=completion]").ShouldNotBeNull(), _wait);
    }

    [Fact]
    public void An_unauthorized_key_shows_a_friendly_relink_error()
    {
        var cut = RenderLive(_ => ClientTestHarness.Empty(HttpStatusCode.Unauthorized));

        cut.WaitForAssertion(() => cut.Find("[data-testid=stream-error]").ShouldNotBeNull(), _wait);
        cut.Find("[data-testid=error-message]").TextContent.ShouldContain("Re-link your workspace");
    }

    [Fact]
    public void An_unexpected_api_status_shows_a_generic_error()
    {
        var cut = RenderLive(_ => ClientTestHarness.Text(HttpStatusCode.InternalServerError, "boom"));

        cut.WaitForAssertion(() => cut.Find("[data-testid=stream-error]").ShouldNotBeNull(), _wait);
        cut.Find("[data-testid=error-message]").TextContent.ShouldContain("temporarily unavailable");
        cut.Markup.ShouldNotContain("boom"); // no raw API body leaked into the UI
    }

    [Fact]
    public void An_erased_events_stream_falls_back_to_the_timeline_final_state()
    {
        var timeline = new RunTimelineResponse(_runId, "county.parcel.search", "hash", null, null, [], "us-east-1",
            RunStatus.Succeeded, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 3820, [], [], [], [], [], [], null);
        var cut = RenderLive(req => req.Path.EndsWith("/events", StringComparison.Ordinal)
            ? ClientTestHarness.Empty(HttpStatusCode.NotFound)
            : ClientTestHarness.Json(timeline));

        cut.WaitForAssertion(() => cut.Find("[data-testid=completion]").ShouldNotBeNull(), _wait);
        cut.Markup.ShouldContain("Run succeeded");
    }

    [Fact]
    public void A_run_that_is_entirely_gone_shows_the_not_found_state()
    {
        // Both the events stream and the timeline 404 → the run itself is gone.
        var cut = RenderLive(_ => ClientTestHarness.Empty(HttpStatusCode.NotFound));

        cut.WaitForAssertion(() => cut.Find("[data-testid=not-found]").ShouldNotBeNull(), _wait);
        cut.Markup.ShouldContain("Run not found");
    }

    [Fact]
    public void Auto_scroll_follows_the_tail_and_stops_when_toggled_off()
    {
        int Scrolls() => JSInterop.Invocations.Count(i => string.Equals(i.Identifier, "scrollTo", StringComparison.Ordinal));

        var content = new PushSseContent();
        var cut = RenderLive(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        // Two frames render (each's post-render OnAfterRenderAsync hops the window) — by the time the 2nd row is present,
        // the 1st render's scroll is definitely recorded, so this is timing-deterministic.
        content.Push(Frame(1, "StepStarted"));
        content.Push(Frame(2, "StepStarted"));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=event-row]").Count.ShouldBe(2), _wait);
        Scrolls().ShouldBeGreaterThanOrEqualTo(1); // auto-scroll on by default → the tail was followed

        cut.Find("[data-testid=autoscroll-toggle]").Change(false);
        var scrollsWhenOff = Scrolls();

        content.Push(Frame(3, "StepStarted"));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=event-row]").Count.ShouldBe(3), _wait);
        // A frame arrived and re-rendered, but auto-scroll is off → no further scrollTo was issued.
        Scrolls().ShouldBe(scrollsWhenOff);

        // Turning it back on resumes following the tail.
        cut.Find("[data-testid=autoscroll-toggle]").Change(true);
        content.Push(Frame(4, "StepStarted"));
        cut.WaitForAssertion(() => Scrolls().ShouldBeGreaterThan(scrollsWhenOff), _wait);
    }

    [Fact]
    public void The_feed_keeps_only_the_most_recent_rows_under_the_cap()
    {
        var content = new PushSseContent();
        var cut = RenderLive(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content }, maxEvents: 2);

        content.Push(Frame(1, "StepStarted", "{\"index\":1}"));
        content.Push(Frame(2, "StepStarted", "{\"index\":2}"));
        content.Push(Frame(3, "StepStarted", "{\"index\":3}"));

        // Retry until frame 3 is processed: the oldest row (index 1) is dropped, the two newest remain (decoded text).
        cut.WaitForAssertion(
            () =>
            {
                var data = cut.FindAll("[data-testid=event-row] .data").Select(static e => e.TextContent).ToList();
                data.Count.ShouldBe(2);
                data.ShouldContain(static c => c.Contains("\"index\":3", StringComparison.Ordinal));
                data.ShouldNotContain(static c => c.Contains("\"index\":1", StringComparison.Ordinal));
            },
            _wait);
    }

    [Fact]
    public void The_header_counters_keep_counting_past_the_display_cap()
    {
        var content = new PushSseContent();
        var cut = RenderLive(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content }, maxEvents: 2);

        // Five frames arrive (four StepStarted, one SelectorMiss) with a display cap of 2. The feed keeps only the two
        // newest rows, but the header counters are monotonic — incremented as frames arrive, independent of the capped
        // window — so they reflect everything seen instead of plateauing at the cap.
        content.Push(Frame(1, "StepStarted"));
        content.Push(Frame(2, "SelectorMiss"));
        content.Push(Frame(3, "StepStarted"));
        content.Push(Frame(4, "StepStarted"));
        content.Push(Frame(5, "StepStarted"));

        cut.WaitForAssertion(() => cut.Find("[data-testid=count-events]").TextContent.Trim().ShouldBe("5"), _wait);
        cut.Find("[data-testid=count-steps]").TextContent.Trim().ShouldBe("4");   // past the 2-row cap
        cut.Find("[data-testid=count-misses]").TextContent.Trim().ShouldBe("1");
        cut.FindAll("[data-testid=event-row]").Count.ShouldBe(2);                  // the display window is still capped
    }

    [Fact]
    public async Task Disposal_during_the_timeline_fallback_tears_it_down_cleanly()
    {
        // Events are gone (404 → timeline fallback), and the timeline read blocks; disposing mid-fallback must cancel it
        // rather than fault the background loop.
        var timeline = new BlockingHttpContent();
        var handler = new StubHttpMessageHandler(req => req.Path.EndsWith("/events", StringComparison.Ordinal)
            ? ClientTestHarness.Empty(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = timeline });
        Services.AddSingleton<ICircuitTenantResolver>(new FakeCircuitTenantResolver(PortalRunsTestSupport.TenantOver(handler)));

        var cut = Render<LiveTrace>(p => p.Add(x => x.RunId, _runId));

        // Wait until the fallback has issued the (now-blocking) timeline read, then dispose mid-call.
        cut.WaitForAssertion(
            () => handler.Requests.Any(r => r.Path.EndsWith("/timeline", StringComparison.Ordinal)).ShouldBeTrue(),
            _wait);
        await Renderer.DisposeComponents();

        await timeline.Cancelled.WaitAsync(_wait); // the timeline read was cancelled — a clean teardown, no fault surfaced
    }

    [Fact]
    public async Task Disposing_the_component_cancels_the_sse_connection()
    {
        var content = new PushSseContent();
        var cut = RenderLive(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        content.Push(Frame(1, "StepStarted"));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=event-row]").Count.ShouldBe(1), _wait);

        // Dispose the component directly — the exact call the framework makes at circuit teardown — and deterministically:
        // it cancels the stream token, which the SSE read observes and tears down (no leaked connection).
        cut.Instance.Dispose();

        await content.Cancelled.WaitAsync(_wait); // times out → the read was NOT cancelled → fails the test
    }
}
