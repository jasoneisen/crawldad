using System.Security.Claims;
using Bunit;
using Crawldad.Client;
using Crawldad.Contracts.Tenancy;
using Crawldad.Portal.Components.Pages.App;
using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Client;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Portal;

/// <summary>bUnit rendering of the account page in isolation (fake tenant context + fake linker). Issue #119 simplified the
/// surface: the portal is console-mode only for data, the workspace's display NAME is its identity (its raw id appears only
/// in the copyable Workspace ID field), and the switcher / member-management chrome is single-workspace-first. Covers the
/// unconfigured / no-workspace / resolved states; the profile identity + copyable id; membership role + operator-managed;
/// the multi-workspace switch list and the members section (team vs solo-owner invite); the "claim an existing workspace"
/// form's success/failure/missing-email/blank paths (never echoing the key); and the "create a free workspace" affordance.</summary>
public class AccountComponentTests : BunitContext
{
    private static readonly TenantProfileResponse _profile = new("tenant-alpha", "Alpha Co", "Team", 5, 100);

    private static readonly UsageResponse _usage = new(
        new UsageSlots(2, 5),
        new UsageQueueStats(3, 214, 1200),
        412,
        new UsageEvents(10000, 100, 1240, 6800));

    public AccountComponentTests() =>
        Services.AddSingleton<AntiforgeryStateProvider>(new StubAntiforgeryStateProvider());

    // ---- render states --------------------------------------------------------------------------------------------

    [Fact]
    public void Unconfigured_console_shows_the_honest_not_configured_state()
    {
        var cut = RenderPage(new FakeTenantContext(tenant: null, configured: false), Authed());

        cut.Find("[data-testid=link-status]").TextContent.ShouldContain("Console access not configured");
        cut.FindAll("[data-testid=console-unconfigured]").Count.ShouldBe(1);
        cut.FindAll("[data-testid=get-started]").ShouldBeEmpty();
        cut.FindAll("[data-testid=usage-panel]").ShouldBeEmpty();
    }

    [Fact]
    public void Zero_workspace_shows_the_get_started_affordance_and_the_claim_disclosure()
    {
        var cut = RenderPage(new FakeTenantContext(tenant: null), Authed());

        cut.Find("[data-testid=link-status]").TextContent.ShouldContain("No workspace yet");
        cut.Find("[data-testid=provision-form]").GetAttribute("action").ShouldBe("/app/workspace/provision");
        cut.Find("[data-testid=claim-disclosure]").ShouldNotBeNull();
        cut.FindAll("#link-form").Count.ShouldBe(1);
        cut.FindAll("[data-testid=usage-panel]").ShouldBeEmpty();
    }

    [Fact]
    public void Resolved_workspace_renders_the_name_heading_the_copyable_id_and_live_usage()
    {
        var handler = ApiReturning(_profile, _usage);
        var cut = RenderPage(new FakeTenantContext(Linked("tenant-alpha", handler)), Authed());

        // The display NAME is the identity; the raw id appears ONLY in the copyable Workspace ID field.
        cut.Find("[data-testid=workspace-heading]").TextContent.ShouldContain("Alpha Co");
        cut.Find("[data-testid=link-status]").TextContent.ShouldContain("Alpha Co");
        cut.Find("[data-testid=workspace-id]").GetAttribute("value").ShouldBe("tenant-alpha");
        cut.Find("[data-testid=usage-panel]").ShouldNotBeNull();
        cut.Find("[data-testid=usage-slots]").TextContent.ShouldContain("2");
        cut.Find("[data-testid=usage-queue]").TextContent.ShouldContain("100");
        cut.Find("[data-testid=usage-p95]").TextContent.ShouldContain("1.2s");
        cut.Find("[data-testid=usage-events]").TextContent.ShouldContain("6800");
        cut.Find("[data-testid=usage-runs]").TextContent.ShouldContain("412");
        cut.Find("[data-testid=plan-tier]").TextContent.ShouldContain("Team");
    }

    [Fact]
    public void Usage_that_the_api_rejects_shows_a_friendly_error_and_a_generic_heading()
    {
        // GET /tenant 401 → CrawldadException, caught: usage-error, and the profile is null so the heading falls back to a
        // generic label rather than leaking the id.
        var handler = ClientTestHarness.Always(() => ClientTestHarness.Empty(System.Net.HttpStatusCode.Unauthorized));
        var cut = RenderPage(new FakeTenantContext(Linked("tenant-alpha", handler)), Authed());

        cut.FindAll("[data-testid=usage-error]").Count.ShouldBe(1);
        cut.FindAll("[data-testid=usage-panel]").ShouldBeEmpty();
        cut.Find("[data-testid=workspace-heading]").TextContent.ShouldContain("Your workspace"); // never the raw id
    }

    [Fact]
    public void Usage_transport_failure_also_shows_the_friendly_error()
    {
        var handler = new ThrowingHandler(new HttpRequestException("api down"));
        var cut = RenderPage(new FakeTenantContext(Linked("tenant-alpha", handler)), Authed());

        cut.FindAll("[data-testid=usage-error]").Count.ShouldBe(1);
    }

    [Fact]
    public void Zero_allowances_and_a_long_wait_render_without_dividing_by_zero()
    {
        var profile = new TenantProfileResponse("tenant-zero", "Zero Co", Tier: null, 0, 0);
        var usage = new UsageResponse(new UsageSlots(0, 0), new UsageQueueStats(0, 0, 252_000), 0, new UsageEvents(0, 0, 0, 0));
        var cut = RenderPage(new FakeTenantContext(Linked("tenant-zero", ApiReturning(profile, usage))), Authed());

        cut.Find("[data-testid=usage-p95]").TextContent.ShouldContain("4m 12s");
        cut.Find("[data-testid=plan-tier]").TextContent.ShouldContain("Default"); // null tier falls back
        cut.Markup.ShouldContain("width:0%"); // guarded meter, no NaN
    }

    [Fact]
    public void The_signed_in_email_is_shown()
    {
        var cut = RenderPage(new FakeTenantContext(tenant: null), Authed("dana@meridiantitle.co"));

        cut.Find("[data-testid=account-email]").TextContent.ShouldBe("dana@meridiantitle.co");
    }

    // ---- membership / role ----------------------------------------------------------------------------------------

    [Fact]
    public void The_profile_shows_the_signed_in_users_role()
    {
        // A prior revoked (inactive) membership for the same user is skipped; the active one is the current membership.
        var memberships = new TenantMembershipList(
        [
            new(Guid.NewGuid(), "dana@meridiantitle.co", MembershipRole.Owner, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, false),
            new(Guid.NewGuid(), "dana@meridiantitle.co", MembershipRole.Owner, DateTimeOffset.UnixEpoch, null, true),
        ]);
        var handler = ApiReturning(_profile, _usage, memberships);
        var cut = RenderPage(new FakeTenantContext(Linked("tenant-alpha", handler)), Authed("dana@meridiantitle.co"));

        cut.Find("[data-testid=membership-status]").TextContent.ShouldContain("Owner");
    }

    [Fact]
    public void An_env_tenant_shows_the_operator_managed_members_state()
    {
        // GET /tenant/memberships is a 400 self_service_unavailable for an env tenant — surfaced as a clean state, no crash,
        // and no role is shown in the profile (there is no membership).
        var handler = new StubHttpMessageHandler(req =>
            req.Path.EndsWith("tenant/memberships", StringComparison.Ordinal)
                ? ClientTestHarness.JsonRaw(System.Net.HttpStatusCode.BadRequest, "{\"title\":\"self_service_unavailable\"}")
                : req.Path.EndsWith("tenant/keys", StringComparison.Ordinal) ? ClientTestHarness.Json(new TenantApiKeyList([]))
                : req.Path.EndsWith("usage", StringComparison.Ordinal) ? ClientTestHarness.Json(_usage)
                : ClientTestHarness.Json(_profile));
        var cut = RenderPage(new FakeTenantContext(Linked("tenant-alpha", handler)), Authed());

        cut.Find("[data-testid=members-operator-managed]").ShouldNotBeNull();
        cut.FindAll("[data-testid=membership-status]").ShouldBeEmpty(); // no membership → no role in the profile
    }

    [Fact]
    public void A_malformed_membership_body_degrades_cleanly()
    {
        // A 2xx membership body missing its list must not crash the page (nor without an email claim); the profile simply
        // shows no role and the solo-invite state is not reached (not an Owner).
        var handler = new StubHttpMessageHandler(req =>
            req.Path.EndsWith("tenant/memberships", StringComparison.Ordinal) ? ClientTestHarness.JsonRaw(System.Net.HttpStatusCode.OK, "{}")
            : req.Path.EndsWith("tenant/keys", StringComparison.Ordinal) ? ClientTestHarness.Json(new TenantApiKeyList([]))
            : req.Path.EndsWith("usage", StringComparison.Ordinal) ? ClientTestHarness.Json(_usage)
            : ClientTestHarness.Json(_profile));
        var cut = RenderPage(new FakeTenantContext(Linked("tenant-alpha", handler)), Authed(email: null));

        cut.FindAll("[data-testid=membership-status]").ShouldBeEmpty();
    }

    // ---- multi-workspace switch list + members (single-workspace-first) --------------------------------------------

    [Fact]
    public void A_multi_workspace_user_gets_the_switch_list()
    {
        var memberships = new TenantMembershipList([new(Guid.NewGuid(), "dana@meridiantitle.co", MembershipRole.Owner, DateTimeOffset.UnixEpoch, null, true)]);
        var workspaces = new WorkspaceList([new("tenant-alpha", "Alpha Co", MembershipRole.Owner), new("tenant-beta", "Beta Co", MembershipRole.Member)]);
        var handler = ApiReturning(_profile, _usage, memberships, workspaces);
        var cut = RenderPage(new FakeTenantContext(Linked("tenant-alpha", handler)), Authed("dana@meridiantitle.co"));

        cut.FindAll("[data-testid=workspace-row]").Count.ShouldBe(2);
        cut.Find("[data-testid=workspace-active-badge]").ShouldNotBeNull();           // the active workspace is badged
        cut.Find("[data-testid=workspace-switch]").ShouldNotBeNull();                 // the other offers a switch
        cut.Find("form[action=\"/app/workspace\"] input[name=workspace]").GetAttribute("value").ShouldBe("tenant-beta");
    }

    [Fact]
    public void A_single_workspace_user_sees_no_switch_list()
    {
        // Single-workspace-first: exactly one workspace → no switch list at all (the name is the heading).
        var workspaces = new WorkspaceList([new("tenant-alpha", "Alpha Co", MembershipRole.Owner)]);
        var handler = ApiReturning(_profile, _usage, workspaces: workspaces);
        var cut = RenderPage(new FakeTenantContext(Linked("tenant-alpha", handler)), Authed());

        cut.FindAll("[data-testid=workspaces-list]").ShouldBeEmpty();
        cut.FindAll("[data-testid=workspace-row]").ShouldBeEmpty();
    }

    [Fact]
    public void An_owner_of_a_team_sees_the_member_management_controls()
    {
        var memberships = new TenantMembershipList(
        [
            new(Guid.NewGuid(), "dana@meridiantitle.co", MembershipRole.Owner, DateTimeOffset.UnixEpoch, null, true),
            new(Guid.NewGuid(), "teammate@meridiantitle.co", MembershipRole.Member, DateTimeOffset.UnixEpoch, null, true),
        ]);
        var handler = ApiReturning(_profile, _usage, memberships);
        var cut = RenderPage(new FakeTenantContext(Linked("tenant-alpha", handler)), Authed("dana@meridiantitle.co"));

        cut.FindAll("[data-testid=member-row]").Count.ShouldBe(2);
        cut.Find("[data-testid=add-member-form]").ShouldNotBeNull();                  // an Owner can add members
        cut.FindAll("[data-testid=member-remove]").Count.ShouldBe(2);                 // and remove / change role
        cut.FindAll("[data-testid=member-role-toggle]").Count.ShouldBe(2);
    }

    [Fact]
    public void A_member_of_a_team_sees_the_roster_read_only()
    {
        var memberships = new TenantMembershipList(
        [
            new(Guid.NewGuid(), "dana@meridiantitle.co", MembershipRole.Member, DateTimeOffset.UnixEpoch, null, true),
            new(Guid.NewGuid(), "owner@meridiantitle.co", MembershipRole.Owner, DateTimeOffset.UnixEpoch, null, true),
        ]);
        var handler = ApiReturning(_profile, _usage, memberships);
        var cut = RenderPage(new FakeTenantContext(Linked("tenant-alpha", handler)), Authed("dana@meridiantitle.co"));

        cut.FindAll("[data-testid=member-row]").Count.ShouldBe(2);
        cut.FindAll("[data-testid=add-member-form]").ShouldBeEmpty();                 // a Member cannot manage members
        cut.FindAll("[data-testid=member-remove]").ShouldBeEmpty();
        cut.Find("[data-testid=members-readonly]").ShouldNotBeNull();
    }

    [Fact]
    public void A_solo_owner_sees_the_understated_invite_not_the_full_section()
    {
        // Single-workspace-first: a solo Owner (one workspace, one member) gets an understated invite entry, not the full
        // member-management table.
        var memberships = new TenantMembershipList([new(Guid.NewGuid(), "dana@meridiantitle.co", MembershipRole.Owner, DateTimeOffset.UnixEpoch, null, true)]);
        var handler = ApiReturning(_profile, _usage, memberships);
        var cut = RenderPage(new FakeTenantContext(Linked("tenant-alpha", handler)), Authed("dana@meridiantitle.co"));

        cut.Find("[data-testid=members-solo]").ShouldNotBeNull();
        cut.Find("[data-testid=add-member-form]").ShouldNotBeNull(); // the understated invite entry
        cut.FindAll("[data-testid=member-row]").ShouldBeEmpty();     // no full roster table for a solo workspace
    }

    [Fact]
    public void The_member_roster_renders_for_a_team_without_an_email_claim()
    {
        // A render with no email claim still lists a team's members read-only (the signed-in user matches none → not Owner).
        var memberships = new TenantMembershipList(
        [
            new(Guid.NewGuid(), "owner@x.test", MembershipRole.Owner, DateTimeOffset.UnixEpoch, null, true),
            new(Guid.NewGuid(), "member@x.test", MembershipRole.Member, DateTimeOffset.UnixEpoch, null, true),
        ]);
        var cut = RenderPage(new FakeTenantContext(Linked("tenant-alpha", ApiReturning(_profile, _usage, memberships))), Authed(email: null));

        cut.FindAll("[data-testid=member-row]").Count.ShouldBe(2);
        cut.Find("[data-testid=members-readonly]").ShouldNotBeNull();
    }

    [Fact]
    public void A_member_action_error_is_surfaced_from_the_redirect()
    {
        var memberships = new TenantMembershipList([new(Guid.NewGuid(), "owner@example.com", MembershipRole.Owner, DateTimeOffset.UnixEpoch, null, true)]);
        var cut = RenderPage(new FakeTenantContext(Linked("tenant-alpha", ApiReturning(_profile, _usage, memberships))), Authed(),
            query: "?memberError=A%20workspace%20must%20keep%20an%20Owner");

        cut.Find("[data-testid=members-action-error]").TextContent.ShouldContain("must keep an Owner");
    }

    // ---- claim-an-existing-workspace form -------------------------------------------------------------------------

    [Fact]
    public void A_successful_claim_redirects_to_the_confirmed_page()
    {
        var linker = new FakeLinker(new WorkspaceLinkResult(WorkspaceLinkOutcome.Claimed, "ok"));
        var cut = RenderPage(new FakeTenantContext(tenant: null), Authed("owner@example.com"), linker);

        SubmitLink(cut, "tenant-alpha", "sk_live_secret_key");

        linker.Calls.ShouldHaveSingleItem();
        linker.Calls[0].Email.ShouldBe("owner@example.com");
        linker.Calls[0].TenantId.ShouldBe("tenant-alpha");
        linker.Calls[0].ApiKey.ShouldBe("sk_live_secret_key");
        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("/app/account?linked=true");
    }

    [Fact]
    public void A_rejected_key_shows_the_error_and_does_not_redirect()
    {
        var linker = new FakeLinker(new WorkspaceLinkResult(WorkspaceLinkOutcome.InvalidKey, "That API key was rejected."));
        var cut = RenderPage(new FakeTenantContext(tenant: null), Authed(), linker);

        SubmitLink(cut, "tenant-alpha", "sk_live_wrong");

        cut.Find("[data-testid=link-error]").TextContent.ShouldContain("rejected");
        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("/"); // still on the base uri, no redirect
    }

    [Fact]
    public void A_failed_claim_never_echoes_the_submitted_key_in_the_markup()
    {
        var linker = new FakeLinker(new WorkspaceLinkResult(WorkspaceLinkOutcome.OperatorManaged, "can't be claimed"));
        var cut = RenderPage(new FakeTenantContext(tenant: null), Authed(), linker);

        SubmitLink(cut, "tenant-alpha", "SUPER-SECRET-KEY-9999");

        cut.Markup.ShouldNotContain("SUPER-SECRET-KEY-9999"); // password input, never rendered back
    }

    [Fact]
    public void A_missing_email_claim_is_handled_without_calling_the_linker()
    {
        var linker = new FakeLinker(new WorkspaceLinkResult(WorkspaceLinkOutcome.Claimed, "ok"));
        var cut = RenderPage(new FakeTenantContext(tenant: null), Authed(email: null), linker);

        SubmitLink(cut, "tenant-alpha", "sk_live_secret_key");

        linker.Calls.ShouldBeEmpty();
        cut.Find("[data-testid=link-error]").TextContent.ShouldContain("session has expired");
    }

    [Fact]
    public void Blank_fields_are_rejected_client_side_without_calling_the_linker()
    {
        var linker = new FakeLinker(new WorkspaceLinkResult(WorkspaceLinkOutcome.Claimed, "ok"));
        var cut = RenderPage(new FakeTenantContext(tenant: null), Authed(), linker);

        cut.Find("#link-form").Submit();

        linker.Calls.ShouldBeEmpty();
        cut.Markup.ShouldContain("Enter your workspace ID.");
        cut.Markup.ShouldContain("Enter your API key.");
    }

    // ---- create-a-free-workspace affordance -----------------------------------------------------------------------

    [Fact]
    public void A_resolved_workspace_does_not_show_the_create_affordance()
    {
        var cut = RenderPage(new FakeTenantContext(Linked("tenant-alpha", ApiReturning(_profile, _usage))), Authed());

        cut.FindAll("[data-testid=provision-form]").ShouldBeEmpty(); // already has a workspace
        cut.FindAll("[data-testid=get-started]").ShouldBeEmpty();
    }

    [Fact]
    public void A_provision_error_is_surfaced_from_the_redirect()
    {
        var cut = RenderPage(new FakeTenantContext(tenant: null), Authed(), query: "?provisionError=We%20couldn%27t%20create%20your%20workspace");

        cut.Find("[data-testid=provision-error]").TextContent.ShouldContain("couldn't create your workspace");
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private IRenderedComponent<Account> RenderPage(IPortalTenantContext ctx, HttpContext http, IWorkspaceLinker? linker = null, string? query = null)
    {
        Services.AddSingleton(ctx);
        Services.AddSingleton(linker ?? new FakeLinker(new WorkspaceLinkResult(WorkspaceLinkOutcome.Claimed, "ok")));
        Services.AddSingleton<IPortalWorkspaceSelectionStore>(new StubWorkspaceSelectionStore());
        if (query is not null)
        {
            Services.GetRequiredService<NavigationManager>().NavigateTo($"/app/account{query}");
        }

        return Render<Account>(ps => ps.AddCascadingValue<HttpContext>(http));
    }

    // The API key is a deliberately-unbound password input (it must never echo), so bUnit's interactive renderer has no
    // change handler to drive. Set the form model directly, then submit — the name→model POST binding itself is covered
    // by the real-SSR integration test.
    private static void SubmitLink(IRenderedComponent<Account> cut, string tenantId, string apiKey)
    {
        cut.Instance.Input.TenantId = tenantId;
        cut.Instance.Input.ApiKey = apiKey;
        cut.Find("#link-form").Submit();
    }

    private static PortalTenant Linked(string tenantId, HttpMessageHandler handler) =>
        new(tenantId, new CrawldadClient(new HttpClient(handler) { BaseAddress = ClientTestHarness.BaseUrl },
            new CrawldadClientOptions { BaseUrl = ClientTestHarness.BaseUrl, ApiKey = ClientTestHarness.ApiKey }));

    private static StubHttpMessageHandler ApiReturning(TenantProfileResponse profile, UsageResponse usage, TenantMembershipList? memberships = null, WorkspaceList? workspaces = null) =>
        new(req =>
            req.Path.EndsWith("tenant/keys", StringComparison.Ordinal) ? ClientTestHarness.Json(new TenantApiKeyList([]))
            : req.Path.EndsWith("tenant/memberships", StringComparison.Ordinal) ? ClientTestHarness.Json(memberships ?? new TenantMembershipList([]))
            : req.Path.EndsWith("workspaces", StringComparison.Ordinal) ? ClientTestHarness.Json(workspaces ?? new WorkspaceList([]))
            : req.Path.EndsWith("usage", StringComparison.Ordinal) ? ClientTestHarness.Json(usage)
            : ClientTestHarness.Json(profile));

    private static DefaultHttpContext Authed(string? email = "owner@example.com")
    {
        Claim[] claims = email is null ? [] : [new Claim(ClaimTypes.Email, email)];
        return new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "TestCookie")) };
    }

    private sealed class FakeTenantContext(PortalTenant? tenant, bool configured = true) : IPortalTenantContext
    {
        public bool ConsoleConfigured => configured;

        public Task<PortalTenant?> TryResolveAsync(CancellationToken cancellationToken = default) => Task.FromResult(tenant);

        public Task<PortalTenant> RequireAsync(CancellationToken cancellationToken = default) =>
            tenant is null ? throw new NotLinkedException("not linked") : Task.FromResult(tenant);
    }

    private sealed class FakeLinker(WorkspaceLinkResult result) : IWorkspaceLinker
    {
        public List<(string Email, string TenantId, string ApiKey)> Calls { get; } = [];

        public Task<WorkspaceLinkResult> LinkAsync(string email, string tenantId, string apiKey, CancellationToken cancellationToken = default)
        {
            Calls.Add((email, tenantId, apiKey));
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingHandler(Exception ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw ex;
    }
}
