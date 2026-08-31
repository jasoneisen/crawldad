using Bunit;
using Crawldad.Contracts.Runs;
using Crawldad.Portal.Components.Pages.App;
using Crawldad.Portal.Runs;
using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Client;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Portal;

/// <summary>bUnit rendering of the runs LIST page over a stubbed tenant client: the not-linked empty state, the filtered
/// and unfiltered empty states, the row variants (pinned/inline, terminal/running, failed, region present/absent), and
/// the first-/later-page pager.</summary>
public class RunsPageTests : BunitContext
{
    private static readonly DateTimeOffset _started = new(2026, 8, 27, 8, 22, 14, TimeSpan.Zero);
    private static readonly Guid _runA = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid _runB = new("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid _runC = new("cccccccc-0000-0000-0000-000000000003");

    private void Linked(RunListResponse response)
    {
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Json(response));
        Services.AddSingleton<IPortalTenantContext>(new FakePortalTenantContext(PortalRunsTestSupport.TenantOver(handler)));
    }

    private void NotLinked() =>
        Services.AddSingleton<IPortalTenantContext>(new FakePortalTenantContext(tenant: null));

    [Fact]
    public void Not_linked_shows_the_link_workspace_empty_state()
    {
        NotLinked();

        var cut = Render<Runs>();

        cut.Find("[data-testid=not-linked]").ShouldNotBeNull();
        cut.Markup.ShouldContain("No workspace yet");
        cut.Find("a[href=\"/app/account\"]").ShouldNotBeNull();
    }

    [Fact]
    public void A_forbidden_read_shows_the_workspace_unavailable_state_not_a_500()
    {
        // A stale/suspended active-workspace selection: the console gate 403s the list read (and GET /workspaces too, so no
        // in-page switch). The page degrades to the honest "unavailable" state pointing at Account — never a 500.
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Empty(System.Net.HttpStatusCode.Forbidden));
        Services.AddSingleton<IPortalTenantContext>(new FakePortalTenantContext(PortalRunsTestSupport.TenantOver(handler)));

        var cut = Render<Runs>();

        cut.Find("[data-testid=workspace-unavailable]").ShouldNotBeNull();
        cut.Find("a[href=\"/app/account\"]").ShouldNotBeNull(); // the escape to Account (re-select / claim)
    }

    [Fact]
    public void Empty_and_unfiltered_shows_no_runs_yet()
    {
        Linked(new RunListResponse([], 1, 25, 0, HasMore: false));

        var cut = Render<Runs>();

        cut.Find("[data-testid=no-runs]").ShouldNotBeNull();
        cut.Markup.ShouldContain("No runs yet");
    }

    [Fact]
    public void Empty_and_filtered_shows_no_runs_match_and_marks_the_filter_active()
    {
        Linked(new RunListResponse([], 1, 25, 0, HasMore: false));
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/app/runs?status=Failed");

        var cut = Render<Runs>();

        cut.Find("[data-testid=no-runs]").ShouldNotBeNull();
        cut.Markup.ShouldContain("No runs match");
        // The status query bound through => the Failed pill is the active one.
        cut.Find("[data-testid=status-filter] a.nav-link.active").TextContent.Trim().ShouldBe("Failed");
    }

    [Fact]
    public void Rows_render_every_variant_with_first_page_paging()
    {
        // Pinned + revision + terminal stats (warn misses) + failure + region.
        var rowA = new RunListItem(_runA, RunStatus.Failed, _started, 3820,
            new RunListFailure("terminal", "record_not_accessible"), "county.parcel.search-detail", Guid.NewGuid(), 5, Inline: false, "us-east-1",
            new RunListStats(7, 2, 3));
        // Inline + running: no duration, no stats, no failure, no region.
        var rowB = new RunListItem(_runB, RunStatus.Running, _started, null, null, "inline-demo", null, null, Inline: true, null, null);
        // Pinned but revision-less + terminal stats (no misses) + region.
        var rowC = new RunListItem(_runC, RunStatus.Succeeded, _started, 8610, null, "sos.business.lookup", Guid.NewGuid(), null, Inline: false, "us-east-1",
            new RunListStats(24, 3, 0));
        Linked(new RunListResponse([rowA, rowB, rowC], 1, 25, 53, HasMore: true));

        var cut = Render<Runs>();

        cut.FindAll("[data-testid=run-row]").Count.ShouldBe(3);
        cut.Find($"a[href=\"/app/runs/{_runA}\"]").TextContent.ShouldBe(RunView.ShortId(_runA));
        var rowFailure = cut.Find("[data-testid=row-failure]").TextContent;
        rowFailure.ShouldContain("record_not_accessible"); // failure code
        rowFailure.ShouldContain("terminal");              // failure class
        cut.Markup.ShouldContain("r5");        // rowA revision pill
        cut.Markup.ShouldContain("inline");    // rowB inline marker
        cut.Markup.ShouldContain("m warn");    // rowA selector-miss warn styling
        cut.Markup.ShouldContain("us-east-1"); // region present
        cut.Markup.ShouldContain("3.82s");     // rowA duration
        // First page: prev is disabled (no link), next is a live link, and the summary counts this page of the total.
        cut.FindAll("[data-testid=page-prev]").ShouldBeEmpty();
        cut.Find("[data-testid=page-next]").ShouldNotBeNull();
        cut.Find("[data-testid=page-summary]").TextContent.ShouldContain("Showing 1 to 3 of 53");
    }

    [Fact]
    public void Later_page_enables_prev_and_disables_next()
    {
        var row = new RunListItem(_runA, RunStatus.Succeeded, _started, 2140, null, "demo", Guid.NewGuid(), 2, Inline: false, "us-east-1",
            new RunListStats(24, 3, 0));
        // The API echoes page 2 in the response, which drives the pager display and links (independent of the query).
        Linked(new RunListResponse([row], 2, 25, 30, HasMore: false));

        var cut = Render<Runs>();

        cut.Find("[data-testid=page-prev]").ShouldNotBeNull();     // prev now a live link
        cut.FindAll("[data-testid=page-next]").ShouldBeEmpty();    // next disabled (no further page)
        cut.Find("[data-testid=page-current]").TextContent.ShouldBe("2");
        cut.Find("[data-testid=page-summary]").TextContent.ShouldContain("Showing 26 to 26 of 30");
    }
}
