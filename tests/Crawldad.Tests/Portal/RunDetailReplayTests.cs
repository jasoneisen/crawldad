using System.Net;
using System.Text.Json;
using Bunit;
using Crawldad.Contracts.Runs;
using Crawldad.Portal.Components.Pages.App;
using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Client;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Portal;

/// <summary>bUnit rendering of the run-detail page's <b>Replay</b> slice (issue #119, P3): the antiforgery-protected
/// resupplied-inputs form for a pinned run (vs. the disabled note for an inline run), the successful async replay that
/// redirects to the new run's live trace, the write-only inputs never echoed back, and the friendly inline errors for
/// invalid JSON, a full queue, an erased run, and a generic API fault — never a 500, never a secret.</summary>
public class RunDetailReplayTests : BunitContext
{
    private static readonly Guid _runId = new("7b3ad9f2-1c4e-4a08-9f21-2c9e5d1a4f60");
    private static readonly Guid _payloadId = new("9a3c0000-0000-0000-0000-000000000001");
    private static readonly Guid _newRunId = new("1111aaaa-2222-bbbb-3333-cccc4444dddd");
    private static readonly DateTimeOffset _started = new(2026, 8, 27, 8, 22, 14, TimeSpan.Zero);

    // A pinned run renders the antiforgery-protected replay form, whose token lookup needs a state provider.
    public RunDetailReplayTests() =>
        Services.AddSingleton<AntiforgeryStateProvider>(new StubAntiforgeryStateProvider());

    private NavigationManager Nav => Services.GetRequiredService<NavigationManager>();

    private IRenderedComponent<RunDetail> RenderDetail() =>
        Render<RunDetail>(ps => ps.Add(p => p.RunId, _runId));

    private void UseHandler(StubHttpMessageHandler handler) =>
        Services.AddSingleton<IPortalTenantContext>(new FakePortalTenantContext(PortalRunsTestSupport.TenantOver(handler)));

    // ----- render: pinned vs inline vs not-linked -----

    [Fact]
    public void Pinned_run_renders_the_replay_form_and_header_action()
    {
        UseHandler(Handler(PinnedTimeline(), Drift()));

        var cut = RenderDetail();

        cut.Find("[data-testid=replay-card]").ShouldNotBeNull();
        cut.Find("[data-testid=replay-form]").ShouldNotBeNull();
        cut.Find("[data-testid=replay-action]").ShouldNotBeNull();     // the header "Replay" anchor
        cut.FindAll("[data-testid=replay-inline-note]").ShouldBeEmpty();
        cut.FindAll("[data-testid=replay-error]").ShouldBeEmpty();     // no error before a submit
        cut.Find("[data-testid=replay-card]").TextContent.ShouldContain("r5"); // pinned revision shown in the copy
    }

    [Fact]
    public void Inline_run_disables_replay_with_an_explanatory_note()
    {
        UseHandler(Handler(InlineTimeline(), drift: null)); // inline => the page never calls /drift

        var cut = RenderDetail();

        cut.Find("[data-testid=replay-inline-note]").TextContent.ShouldContain("inline");
        cut.Find("[data-testid=replay-action-disabled]").ShouldNotBeNull(); // header button, disabled
        cut.FindAll("[data-testid=replay-form]").ShouldBeEmpty();
        cut.FindAll("[data-testid=replay-action]").ShouldBeEmpty();
    }

    [Fact]
    public void Not_linked_state_offers_no_replay()
    {
        Services.AddSingleton<IPortalTenantContext>(new FakePortalTenantContext(tenant: null));

        var cut = RenderDetail();

        cut.Find("[data-testid=not-linked]").ShouldNotBeNull();
        cut.FindAll("[data-testid=replay-card]").ShouldBeEmpty();
        cut.FindAll("[data-testid=replay-form]").ShouldBeEmpty();
    }

    // ----- successful replay -----

    [Fact]
    public void Replaying_with_inputs_posts_them_async_and_redirects_to_the_live_trace()
    {
        var handler = Handler(PinnedTimeline(), Drift());
        UseHandler(handler);
        var cut = RenderDetail();

        cut.Instance.ReplayForm.Inputs = """{ "parcel": "123-45" }""";
        cut.Find("[data-testid=replay-form]").Submit();

        var replay = handler.Requests.Single(r => r.Path.EndsWith("/replay", StringComparison.Ordinal));
        replay.Method.ShouldBe(HttpMethod.Post);
        replay.Path.ShouldBe($"/runs/{_runId}/replay"); // pin re-derived server-side from this run id
        using var body = JsonDocument.Parse(replay.Body);
        body.RootElement.GetProperty("inputs").GetProperty("parcel").GetString().ShouldBe("123-45");
        body.RootElement.GetProperty("async").GetBoolean().ShouldBeTrue(); // async → 202 → live trace
        Nav.Uri.ShouldEndWith($"/app/live/{_newRunId}");
    }

    [Fact]
    public void Replaying_with_blank_inputs_sends_an_empty_object_and_redirects()
    {
        var handler = Handler(PinnedTimeline(), Drift());
        UseHandler(handler);
        var cut = RenderDetail();

        // ReplayForm.Inputs left at its seeded null — resupplied inputs are optional.
        cut.Find("[data-testid=replay-form]").Submit();

        var replay = handler.Requests.Single(r => r.Path.EndsWith("/replay", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(replay.Body);
        body.RootElement.GetProperty("inputs").ValueKind.ShouldBe(JsonValueKind.Object); // Undefined normalized to {}
        Nav.Uri.ShouldEndWith($"/app/live/{_newRunId}");
    }

    // ----- inline error paths (never a 500, never echo the submitted inputs) -----

    [Fact]
    public void Invalid_json_inputs_show_an_error_and_never_call_the_api()
    {
        var handler = Handler(PinnedTimeline(), Drift());
        UseHandler(handler);
        var cut = RenderDetail();

        cut.Instance.ReplayForm.Inputs = "{ not valid json";
        cut.Find("[data-testid=replay-form]").Submit();

        cut.Find("[data-testid=replay-error]").TextContent.ShouldContain("valid JSON object");
        handler.Requests.ShouldNotContain(r => r.Path.EndsWith("/replay", StringComparison.Ordinal));
        cut.Markup.ShouldNotContain("not valid json"); // write-only: the submitted inputs are never echoed back
        Nav.Uri.ShouldNotContain("/app/live/");
    }

    [Fact]
    public void Non_object_json_inputs_are_rejected_client_side()
    {
        var handler = Handler(PinnedTimeline(), Drift());
        UseHandler(handler);
        var cut = RenderDetail();

        cut.Instance.ReplayForm.Inputs = "[1, 2, 3]"; // valid JSON, but inputs must be an object
        cut.Find("[data-testid=replay-form]").Submit();

        cut.Find("[data-testid=replay-error]").ShouldNotBeNull();
        handler.Requests.ShouldNotContain(r => r.Path.EndsWith("/replay", StringComparison.Ordinal));
    }

    [Fact]
    public void A_full_queue_surfaces_a_friendly_error_and_stays_on_the_page()
    {
        var handler = Handler(PinnedTimeline(), Drift(),
            replay: _ => ClientTestHarness.JsonRaw(HttpStatusCode.TooManyRequests,
                """{ "code": "queue_depth_exceeded", "message": "The tenant queue is at its cap." }"""));
        UseHandler(handler);
        var cut = RenderDetail();

        cut.Instance.ReplayForm.Inputs = "{}";
        cut.Find("[data-testid=replay-form]").Submit();

        cut.Find("[data-testid=replay-error]").TextContent.ShouldContain("queue is full");
        Nav.Uri.ShouldNotContain("/app/live/"); // no redirect — the user stays put with the error
    }

    [Fact]
    public void An_erased_run_surfaces_a_friendly_not_found_error()
    {
        var handler = Handler(PinnedTimeline(), Drift(),
            replay: _ => ClientTestHarness.Empty(HttpStatusCode.NotFound));
        UseHandler(handler);
        var cut = RenderDetail();

        cut.Find("[data-testid=replay-form]").Submit();

        cut.Find("[data-testid=replay-error]").TextContent.ShouldContain("no longer exists");
        Nav.Uri.ShouldNotContain("/app/live/");
    }

    [Fact]
    public void A_generic_api_fault_surfaces_the_fallback_message()
    {
        var handler = Handler(PinnedTimeline(), Drift(),
            replay: _ => ClientTestHarness.Empty(HttpStatusCode.InternalServerError));
        UseHandler(handler);
        var cut = RenderDetail();

        cut.Find("[data-testid=replay-form]").Submit();

        cut.Find("[data-testid=replay-error]").TextContent.ShouldContain("try again");
        Nav.Uri.ShouldNotContain("/app/live/");
    }

    // ----- fixtures -----

    private static RunTimelineResponse PinnedTimeline() =>
        Timeline(_payloadId, revision: 5);

    private static RunTimelineResponse InlineTimeline() =>
        Timeline(payloadId: null, revision: null);

    private static RunTimelineResponse Timeline(Guid? payloadId, int? revision) =>
        new(_runId, "county.parcel.search-detail", "scripthash", payloadId, revision, ["parcel"], "us-east-1",
            RunStatus.Failed, _started, _started.AddSeconds(4), 3820,
            [new RunTimelineStep(0, "goto", _started, 100)], [], [], [], [], [], Failure: null);

    private static RunDriftResponse Drift() =>
        new(_runId, _payloadId, 5, "scripthash", 5, "scripthash", Drifted: false);

    // A stub answering the run-detail reads (/timeline, /drift) and the POST /replay, which defaults to a 202 Accepted
    // carrying the new run id (async dispatch) unless the test overrides it to script a rejection/fault.
    private static StubHttpMessageHandler Handler(
        RunTimelineResponse timeline,
        RunDriftResponse? drift,
        Func<CapturedRequest, HttpResponseMessage>? replay = null) =>
        new(request =>
        {
            if (request.Path.EndsWith("/replay", StringComparison.Ordinal))
            {
                return replay?.Invoke(request)
                    ?? ClientTestHarness.Json(
                        new RunStateResponse(_newRunId, RunStatus.Queued, null, null, null, null), HttpStatusCode.Accepted);
            }

            if (request.Path.EndsWith("/drift", StringComparison.Ordinal))
            {
                return drift is not null ? ClientTestHarness.Json(drift) : ClientTestHarness.Empty(HttpStatusCode.NotFound);
            }

            return ClientTestHarness.Json(timeline);
        });
}
