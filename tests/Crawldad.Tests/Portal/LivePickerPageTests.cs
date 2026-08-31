using Bunit;
using Crawldad.Contracts.Runs;
using Crawldad.Portal.Components.Pages.App;
using Crawldad.Portal.Runs;
using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Portal;

/// <summary>bUnit rendering of the live PICKER (static SSR) over a stubbed tenant client: the not-linked empty state, the
/// nothing-running empty state, and the active-runs list (running + queued rows, each linking to its interactive
/// trace).</summary>
public class LivePickerPageTests : BunitContext
{
    private static readonly DateTimeOffset _started = new(2026, 8, 27, 9, 15, 0, TimeSpan.Zero);
    private static readonly Guid _runA = new("aaaaaaaa-0000-0000-0000-0000000000a1");
    private static readonly Guid _runB = new("bbbbbbbb-0000-0000-0000-0000000000b2");

    // The picker reads running then queued as two separate status filters; the stub answers each by its query.
    private void Linked(RunListResponse running, RunListResponse queued)
    {
        var handler = new StubHttpMessageHandler(req =>
            req.Query.Contains("Queued", StringComparison.Ordinal)
                ? ClientTestHarness.Json(queued)
                : ClientTestHarness.Json(running));
        Services.AddSingleton<IPortalTenantContext>(new FakePortalTenantContext(PortalRunsTestSupport.TenantOver(handler)));
    }

    private static RunListResponse Page(params RunListItem[] runs) => new(runs, 1, 100, runs.Length, HasMore: false);

    [Fact]
    public void Not_linked_shows_the_link_workspace_empty_state()
    {
        Services.AddSingleton<IPortalTenantContext>(new FakePortalTenantContext(tenant: null));

        var cut = Render<Live>();

        cut.Find("[data-testid=not-linked]").ShouldNotBeNull();
        cut.Markup.ShouldContain("No workspace yet");
        cut.Find("a[href=\"/app/account\"]").ShouldNotBeNull();
    }

    [Fact]
    public void A_forbidden_read_shows_the_workspace_unavailable_state_not_a_500()
    {
        // A stale/suspended active-workspace selection: the console gate 403s the live reads. Degrade to the honest
        // "unavailable" state pointing at Account — never a 500.
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Empty(System.Net.HttpStatusCode.Forbidden));
        Services.AddSingleton<IPortalTenantContext>(new FakePortalTenantContext(PortalRunsTestSupport.TenantOver(handler)));

        var cut = Render<Live>();

        cut.Find("[data-testid=workspace-unavailable]").ShouldNotBeNull();
        cut.Find("a[href=\"/app/account\"]").ShouldNotBeNull();
    }

    [Fact]
    public void No_running_or_queued_runs_shows_the_nothing_running_state()
    {
        Linked(Page(), Page());

        var cut = Render<Live>();

        cut.Find("[data-testid=none-active]").ShouldNotBeNull();
        cut.Markup.ShouldContain("Nothing running right now");
    }

    [Fact]
    public void Active_runs_list_running_then_queued_each_linking_to_its_trace()
    {
        var running = new RunListItem(_runA, RunStatus.Running, _started, null, null, "county.parcel.search", Guid.NewGuid(), 4, Inline: false, "us-east-1", null);
        var queued = new RunListItem(_runB, RunStatus.Queued, _started, null, null, "recorder.deed.capture", null, null, Inline: true, null, null);
        Linked(Page(running), Page(queued));

        var cut = Render<Live>();

        var rows = cut.FindAll("[data-testid=active-row]");
        rows.Count.ShouldBe(2);
        // Running first, then queued — and each row's run id links to the interactive trace.
        cut.Find($"a[href=\"/app/live/{_runA}\"]").TextContent.ShouldBe(RunView.ShortId(_runA));
        cut.Find($"a[href=\"/app/live/{_runB}\"]").ShouldNotBeNull();
        cut.FindAll("[data-testid=watch-link]").Count.ShouldBe(2);
        cut.Markup.ShouldContain("r4");        // pinned running row's revision pill
        cut.Markup.ShouldContain("inline");    // inline queued row marker
        cut.Markup.ShouldContain("us-east-1"); // running row region
        cut.Markup.ShouldContain("status-dot-animated"); // live rows pulse
    }
}
