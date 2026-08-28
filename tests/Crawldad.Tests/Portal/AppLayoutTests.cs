using System.Net;
using Bunit;
using Crawldad.Client;
using Crawldad.Contracts.Tenancy;
using Crawldad.Portal.Auth;
using Crawldad.Portal.Components.Layout;
using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Client;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Portal;

/// <summary>bUnit rendering of the authenticated app shell: the vertical nav, the signed-in email, the antiforgery-
/// guarded sign-out form, and the sidebar usage widget — live slot/queue numbers for a linked user, and a quiet
/// placeholder for every degraded case (unlinked, or an API/decrypt hiccup) so a data problem never breaks the chrome.</summary>
public class AppLayoutTests : BunitContext
{
    public AppLayoutTests() =>
        Services.AddSingleton<AntiforgeryStateProvider>(new StubAntiforgeryStateProvider());

    private IRenderedComponent<AppLayout> RenderShell(
        string email = "dana@example.com",
        string body = "<p>body-marker</p>",
        IPortalTenantContext? context = null)
    {
        // Default to the not-linked context — the shell's chrome tests don't care about usage, and an unlinked user is
        // the quiet-placeholder path the widget must always be safe to render.
        Services.AddSingleton(context ?? new FakePortalTenantContext(tenant: null));
        var http = new DefaultHttpContext { User = PortalPrincipal.Create(email, null) };
        return Render<AppLayout>(ps => ps
            .AddCascadingValue<HttpContext>(http)
            .Add(l => l.Body, body));
    }

    [Fact]
    public void Renders_the_brand_and_the_vertical_nav()
    {
        var cut = RenderShell("dana@example.com");

        cut.Find(".navbar-vertical").ShouldNotBeNull();
        cut.Markup.ShouldContain("Crawl");
        cut.Markup.ShouldContain("dad");
        cut.FindAll("a.nav-link").Count.ShouldBe(5);
    }

    [Fact]
    public void Shows_the_signed_in_email_and_an_antiforgery_guarded_sign_out()
    {
        var cut = RenderShell("dana@example.com");

        cut.Find("[data-testid=user-email]").TextContent.ShouldContain("dana@example.com");

        var form = cut.Find("form[action=\"/auth/signout\"]");
        form.GetAttribute("method").ShouldBe("post");
        form.QuerySelector("input[name=__RequestVerificationToken]").ShouldNotBeNull();
        cut.Find("[data-testid=sign-out]").TextContent.ShouldContain("Sign out");
    }

    [Fact]
    public void Renders_the_page_body()
    {
        var cut = RenderShell("dana@example.com", "<section>runs-here</section>");

        cut.Markup.ShouldContain("runs-here");
    }

    // ---- usage widget ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_usage_widget_shows_live_slots_and_queue_for_a_linked_user()
    {
        var handler = ApiReturning(
            new TenantProfileResponse("tenant-alpha", "Alpha", "Team", 5, 100),
            new UsageResponse(new UsageSlots(2, 5), new UsageQueueStats(3, 0, 0), 0, new UsageEvents(0, 0, 0, 0)));
        var cut = RenderShell(context: LinkedContext(handler));

        var usage = cut.Find("[data-testid=usage-indicator]");
        usage.QuerySelector("[data-testid=usage-slots]")!.TextContent.Replace(" ", "", StringComparison.Ordinal).ShouldBe("2/5");
        usage.QuerySelector("[data-testid=usage-queue]")!.TextContent.Replace(" ", "", StringComparison.Ordinal).ShouldBe("3/100");
        // The meter reflects the real percentage (2 of 5 = 40%), not the placeholder's flat 0%.
        var bar = usage.QuerySelector(".progress-bar")!;
        bar.GetAttribute("style")!.ShouldContain("width:40%");
        bar.GetAttribute("aria-valuenow").ShouldBe("40");
    }

    [Fact]
    public void The_usage_widget_guards_a_zero_slot_allowance_without_dividing_by_zero()
    {
        var handler = ApiReturning(
            new TenantProfileResponse("tenant-zero", "Zero", Tier: null, 0, 0),
            new UsageResponse(new UsageSlots(0, 0), new UsageQueueStats(0, 0, 0), 0, new UsageEvents(0, 0, 0, 0)));
        var cut = RenderShell(context: LinkedContext(handler));

        cut.Find("[data-testid=usage-slots]").TextContent.Replace(" ", "", StringComparison.Ordinal).ShouldBe("0/0");
        cut.Find(".progress-bar").GetAttribute("style")!.ShouldContain("width:0%"); // guarded, no NaN
    }

    [Fact]
    public void An_unlinked_user_sees_the_quiet_usage_placeholder()
    {
        var cut = RenderShell(context: new FakePortalTenantContext(tenant: null));

        cut.Find("[data-testid=usage-slots]").TextContent.ShouldContain("—");
        cut.Find("[data-testid=usage-queue]").TextContent.Trim().ShouldBe("idle");
        cut.Find(".progress-bar").GetAttribute("style")!.ShouldContain("0%");
    }

    [Fact]
    public void An_api_hiccup_keeps_the_quiet_placeholder_and_never_breaks_the_shell()
    {
        // The tenant resolves, but the usage/tenant reads 500 — the widget degrades to the placeholder rather than
        // faulting the whole authenticated shell.
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Empty(HttpStatusCode.InternalServerError));
        var cut = RenderShell(context: LinkedContext(handler));

        cut.Find("[data-testid=usage-slots]").TextContent.ShouldContain("—");
        cut.Find("[data-testid=usage-queue]").TextContent.Trim().ShouldBe("idle");
        // The shell chrome is intact — the nav and sign-out still rendered.
        cut.FindAll("a.nav-link").Count.ShouldBe(5);
        cut.Find("[data-testid=sign-out]").ShouldNotBeNull();
    }

    // ---- workspace switcher (issue #119 PR6) ----------------------------------------------------------------------

    [Fact]
    public void The_switcher_shows_the_single_workspace_as_static_text_in_stored_key_mode()
    {
        // Stored-key mode is single-workspace: the switcher shows the workspace name as text, with no switch dropdown.
        var handler = ApiReturning(new TenantProfileResponse("tenant-alpha", "Alpha Co", "Team", 5, 100),
            new UsageResponse(new UsageSlots(0, 5), new UsageQueueStats(0, 0, 0), 0, new UsageEvents(0, 0, 0, 0)));
        var cut = RenderShell(context: LinkedContext(handler));

        cut.Find("[data-testid=workspace-current]").TextContent.ShouldContain("Alpha Co"); // the profile display name
        cut.FindAll("[data-testid=workspace-menu]").ShouldBeEmpty();                        // no dropdown for one workspace
    }

    [Fact]
    public void The_switcher_lists_workspaces_and_switch_forms_in_console_mode()
    {
        var handler = ApiReturningWorkspaces(
            new TenantProfileResponse("tenant-alpha", "Alpha Co", "Team", 5, 100),
            new UsageResponse(new UsageSlots(0, 5), new UsageQueueStats(0, 0, 0), 0, new UsageEvents(0, 0, 0, 0)),
            new WorkspaceList([new("tenant-alpha", "Alpha Co", MembershipRole.Owner), new("tenant-beta", "Beta Co", MembershipRole.Member)]));
        var cut = RenderShell(context: ConsoleContext("tenant-alpha", handler));

        cut.Find("[data-testid=workspace-active]").TextContent.ShouldContain("Alpha Co"); // the active workspace label
        cut.FindAll("[data-testid=workspace-option]").Count.ShouldBe(2);
        // The non-active workspace is a switch form posting the tenant id to the switch endpoint.
        var switchForm = cut.Find("form[action=\"/app/workspace\"]");
        switchForm.GetAttribute("method").ShouldBe("post");
        switchForm.QuerySelector("input[name=workspace]")!.GetAttribute("value").ShouldBe("tenant-beta");
    }

    private static FakePortalTenantContext LinkedContext(StubHttpMessageHandler handler) =>
        new(PortalRunsTestSupport.TenantOver(handler));

    private static FakePortalTenantContext ConsoleContext(string activeTenant, StubHttpMessageHandler handler) =>
        new(new PortalTenant(activeTenant, new CrawldadClient(
            new HttpClient(handler) { BaseAddress = ClientTestHarness.BaseUrl },
            new CrawldadClientOptions { BaseUrl = ClientTestHarness.BaseUrl, ApiKey = ClientTestHarness.ApiKey }), PortalAuthMode.Console));

    // A console-mode stub answering the shell's three reads: GET /workspaces → the switcher list, GET /usage → usage, else profile.
    private static StubHttpMessageHandler ApiReturningWorkspaces(TenantProfileResponse profile, UsageResponse usage, WorkspaceList workspaces) =>
        new(req =>
            req.Path.EndsWith("workspaces", StringComparison.Ordinal) ? ClientTestHarness.Json(workspaces)
            : req.Path.EndsWith("usage", StringComparison.Ordinal) ? ClientTestHarness.Json(usage)
            : ClientTestHarness.Json(profile));

    // A stub API that answers the widget's two reads: GET /usage → the usage snapshot, anything else (GET /tenant) → the
    // profile.
    private static StubHttpMessageHandler ApiReturning(TenantProfileResponse profile, UsageResponse usage) =>
        new(req => req.Path.EndsWith("usage", StringComparison.Ordinal)
            ? ClientTestHarness.Json(usage)
            : ClientTestHarness.Json(profile));
}
