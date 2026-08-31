using System.Net;
using Bunit;
using Crawldad.Contracts.Runs;
using Crawldad.Portal.Components.Pages.App;
using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Client;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Portal;

/// <summary>bUnit rendering of the run DETAIL page over a stubbed tenant client: the not-linked and not-found empty
/// states, and the full detail for a failed pinned run (failure banner + detail, failing-step styling, screenshots,
/// captures, drift) vs. a minimal succeeded inline run (no failure/artifacts/drift), plus a failed run whose failure
/// carried no artifact refs and whose pinned revision is still current.</summary>
public class RunDetailPageTests : BunitContext
{
    private static readonly Guid _runId = new("7b3ad9f2-1c4e-4a08-9f21-2c9e5d1a4f60");
    private static readonly Guid _payloadId = new("9a3c0000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset _started = new(2026, 8, 27, 8, 22, 14, TimeSpan.Zero);

    // A pinned run now renders the antiforgery-protected replay form, whose token lookup needs a state provider.
    public RunDetailPageTests() =>
        Services.AddSingleton<AntiforgeryStateProvider>(new StubAntiforgeryStateProvider());

    private void Linked(RunTimelineResponse timeline, RunDriftResponse? drift = null)
    {
        var handler = new StubHttpMessageHandler(request =>
            request.Path.EndsWith("/timeline", StringComparison.Ordinal)
                ? ClientTestHarness.Json(timeline)
                : ClientTestHarness.Json(drift!)); // /drift is requested only for a pinned run
        Services.AddSingleton<IPortalTenantContext>(new FakePortalTenantContext(PortalRunsTestSupport.TenantOver(handler)));
    }

    private void LinkedButUnknownRun()
    {
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Empty(HttpStatusCode.NotFound));
        Services.AddSingleton<IPortalTenantContext>(new FakePortalTenantContext(PortalRunsTestSupport.TenantOver(handler)));
    }

    private void NotLinked() =>
        Services.AddSingleton<IPortalTenantContext>(new FakePortalTenantContext(tenant: null));

    private IRenderedComponent<RunDetail> RenderDetail() =>
        Render<RunDetail>(ps => ps.Add(p => p.RunId, _runId));

    private static RunTimelineResponse Timeline(
        RunStatus status,
        Guid? payloadId,
        int? revision,
        string? region,
        IReadOnlyList<RunTimelineStep> steps,
        IReadOnlyList<RunTimelineScreenshot> screenshots,
        IReadOnlyList<RunTimelineCapture> captures,
        IReadOnlyList<string> missedSelectors,
        RunTimelineFailure? failure) =>
        new(_runId, "county.parcel.search-detail", "scripthash", payloadId, revision, ["parcel"], region, status,
            _started, _started.AddSeconds(4), 3820, steps, [], [], screenshots, captures, missedSelectors, failure);

    [Fact]
    public void Not_linked_shows_the_link_workspace_empty_state()
    {
        NotLinked();

        var cut = RenderDetail();

        cut.Find("[data-testid=not-linked]").ShouldNotBeNull();
        cut.Markup.ShouldContain("No workspace yet");
    }

    [Fact]
    public void A_forbidden_read_shows_the_workspace_unavailable_state_not_a_500()
    {
        // A stale/suspended active-workspace selection: the console gate 403s the timeline read (a non-404 failure). Degrade
        // to the honest "unavailable" state pointing at Account — never a 500.
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Empty(HttpStatusCode.Forbidden));
        Services.AddSingleton<IPortalTenantContext>(new FakePortalTenantContext(PortalRunsTestSupport.TenantOver(handler)));

        var cut = RenderDetail();

        cut.Find("[data-testid=workspace-unavailable]").ShouldNotBeNull();
        cut.Find("a[href=\"/app/account\"]").ShouldNotBeNull();
    }

    [Fact]
    public void Unknown_run_shows_the_not_found_empty_state()
    {
        LinkedButUnknownRun();

        var cut = RenderDetail();

        cut.Find("[data-testid=not-found]").ShouldNotBeNull();
        cut.Markup.ShouldContain("Run not found");
        cut.Markup.ShouldContain("7b3ad9f2");
    }

    [Fact]
    public void Failed_pinned_run_renders_failure_screenshot_captures_and_drift()
    {
        var steps = new List<RunTimelineStep>
        {
            new(0, "goto", _started, 640),
            new(5, "set", _started.AddSeconds(2), 10),
            new(6, "guard", _started.AddSeconds(3), 270), // the failing step
        };
        var failure = new RunTimelineFailure(
            "record_not_accessible", "Record not accessible (redirected to /Login.aspx)",
            new RunStepRef(6, "guard"), "screenshots/9f8c21.png", "captures/a3f19c.html");
        var timeline = Timeline(RunStatus.Failed, _payloadId, 5, "us-east-1",
            steps,
            // One named screenshot and one unnamed (its ref is shown instead of a name).
            [
                new RunTimelineScreenshot("screenshots/step5.png", "after-search", 2048),
                new RunTimelineScreenshot("screenshots/step7.png", Name: null, 1024),
            ],
            [new RunTimelineCapture("captures/a3f19c.html", 47100, "a3f19c8e")],
            ["#txtParcelMissing"],
            failure);
        var drift = new RunDriftResponse(_runId, _payloadId, 5, "scripthash", 8, "headhash", Drifted: true);
        Linked(timeline, drift);

        var cut = RenderDetail();

        // Header: pinned crumb with revision, and the run status.
        cut.Find("[data-testid=crumb-payload]").TextContent.ShouldContain("r5");
        cut.Find("[data-testid=stat-tiles]").ShouldNotBeNull();
        cut.Markup.ShouldContain("3.82s"); // duration tile

        // Failure banner + detail carry the code, and the failing step is styled.
        cut.Find("[data-testid=failure-banner]").TextContent.ShouldContain("record_not_accessible");
        cut.Find("[data-testid=failure-detail]").ShouldNotBeNull();
        cut.Find(".tl-step.fail").ShouldNotBeNull();
        cut.FindAll(".tl-step.ok").Count.ShouldBe(2);

        // Failure screenshot proxied through the portal; capture ref shown.
        cut.Find("[data-testid=failure-screenshot] img").GetAttribute("src")
            .ShouldBe($"/app/runs/{_runId}/screenshots/9f8c21.png");
        cut.Find("[data-testid=failure-capture]").TextContent.ShouldContain("captures/a3f19c.html");

        // Explicit screenshots + captures sections, and the drift chip.
        cut.Find("[data-testid=screenshots]").ShouldNotBeNull();
        cut.Markup.ShouldContain("after-search");          // the named screenshot's label
        cut.Markup.ShouldContain("screenshots/step7.png"); // the unnamed one falls back to its ref
        cut.Find("[data-testid=captures]").ShouldNotBeNull();
        cut.Find("[data-testid=drift-chip]").TextContent.ShouldContain("drifted");
        cut.Markup.ShouldContain("us-east-1");
    }

    [Fact]
    public void Succeeded_inline_run_hides_failure_artifacts_and_drift()
    {
        var timeline = Timeline(RunStatus.Succeeded, payloadId: null, revision: null, region: null,
            [new RunTimelineStep(0, "goto", _started, 100)],
            screenshots: [],
            captures: [],
            missedSelectors: [],
            failure: null);
        Linked(timeline); // inline => the page never calls /drift

        var cut = RenderDetail();

        cut.Find("[data-testid=crumb-payload]").TextContent.ShouldContain("inline");
        cut.FindAll("[data-testid=failure-banner]").ShouldBeEmpty();
        cut.FindAll("[data-testid=failure-detail]").ShouldBeEmpty();
        cut.FindAll("[data-testid=screenshots]").ShouldBeEmpty();
        cut.FindAll("[data-testid=captures]").ShouldBeEmpty();
        cut.FindAll("[data-testid=drift]").ShouldBeEmpty();
        cut.Markup.ShouldContain("—"); // region falls back to an em dash
    }

    [Fact]
    public void Failed_run_without_artifact_refs_and_still_current_revision()
    {
        var failure = new RunTimelineFailure("nav_failed", "Navigation failed", new RunStepRef(2, "goto"), ScreenshotRef: null, CaptureRef: null);
        var timeline = Timeline(RunStatus.Failed, _payloadId, 3, region: null,
            [new RunTimelineStep(0, "goto", _started, 100), new RunTimelineStep(2, "goto", _started.AddSeconds(1), 50)],
            screenshots: [],
            captures: [],
            missedSelectors: [],
            failure: failure);
        var drift = new RunDriftResponse(_runId, _payloadId, 3, "scripthash", 3, "scripthash", Drifted: false);
        Linked(timeline, drift);

        var cut = RenderDetail();

        cut.Find("[data-testid=failure-detail]").ShouldNotBeNull();
        cut.FindAll("[data-testid=failure-screenshot]").ShouldBeEmpty(); // no screenshot ref
        cut.FindAll("[data-testid=failure-capture]").ShouldBeEmpty();    // no capture ref
        cut.Find("[data-testid=drift-chip]").TextContent.ShouldContain("current"); // pinned == head
    }
}
