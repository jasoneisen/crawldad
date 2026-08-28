using System.Security.Claims;
using System.Security.Cryptography;
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

/// <summary>bUnit rendering of the account page in isolation (fake tenant context + fake linker): the not-linked, live,
/// usage-error, and re-link-needed states; the workspace-link form's success (redirect) / failure (error) / missing-
/// email / blank paths; and the guarantee that a submitted key is never echoed back into the markup.</summary>
public class AccountComponentTests : BunitContext
{
    private static readonly TenantProfileResponse _profile = new("tenant-alpha", "alpha@crawldad.test", "Team", 5, 100);

    private static readonly UsageResponse _usage = new(
        new UsageSlots(2, 5),
        new UsageQueueStats(3, 214, 1200),
        412,
        new UsageEvents(10000, 100, 1240, 6800));

    public AccountComponentTests() =>
        Services.AddSingleton<AntiforgeryStateProvider>(new StubAntiforgeryStateProvider());

    // ---- render states --------------------------------------------------------------------------------------------

    [Fact]
    public void Unlinked_account_shows_not_linked_and_the_usage_empty_state()
    {
        var cut = RenderPage(new FakeTenantContext(tenant: null), Authed());

        cut.Find("[data-testid=link-status]").TextContent.ShouldContain("Not linked");
        cut.FindAll("[data-testid=usage-unlinked]").Count.ShouldBe(1);
        cut.FindAll("[data-testid=usage-panel]").ShouldBeEmpty();
        // The link form + operator info card are always present.
        cut.FindAll("#link-form").Count.ShouldBe(1);
        cut.Markup.ShouldContain("API keys");
    }

    [Fact]
    public void Linked_account_renders_live_usage_and_plan()
    {
        var handler = ApiReturning(_profile, _usage);
        var cut = RenderPage(new FakeTenantContext(Linked("tenant-alpha", handler)), Authed());

        cut.Find("[data-testid=link-status]").TextContent.ShouldContain("tenant-alpha");
        cut.Find("[data-testid=usage-panel]").ShouldNotBeNull();
        cut.Find("[data-testid=usage-slots]").TextContent.ShouldContain("2");
        cut.Find("[data-testid=usage-slots]").TextContent.ShouldContain("5");
        cut.Find("[data-testid=usage-queue]").TextContent.ShouldContain("3");
        cut.Find("[data-testid=usage-queue]").TextContent.ShouldContain("100");
        cut.Find("[data-testid=usage-p95]").TextContent.ShouldContain("1.2s");
        cut.Find("[data-testid=usage-events]").TextContent.ShouldContain("6800");
        cut.Find("[data-testid=usage-runs]").TextContent.ShouldContain("412");
        cut.Find("[data-testid=plan-tier]").TextContent.ShouldContain("Team");
    }

    [Fact]
    public void Usage_that_the_api_rejects_shows_a_friendly_error_not_a_crash()
    {
        // A stored key the API now rejects (401 → CrawldadException) must not 500 the page.
        var handler = ClientTestHarness.Always(() => ClientTestHarness.Empty(System.Net.HttpStatusCode.Unauthorized));
        var cut = RenderPage(new FakeTenantContext(Linked("tenant-alpha", handler)), Authed());

        cut.FindAll("[data-testid=usage-error]").Count.ShouldBe(1);
        cut.FindAll("[data-testid=usage-panel]").ShouldBeEmpty();
    }

    [Fact]
    public void Usage_transport_failure_also_shows_the_friendly_error()
    {
        var handler = new ThrowingHandler(new HttpRequestException("api down"));
        var cut = RenderPage(new FakeTenantContext(Linked("tenant-alpha", handler)), Authed());

        cut.FindAll("[data-testid=usage-error]").Count.ShouldBe(1);
    }

    [Fact]
    public void An_undecryptable_stored_key_prompts_a_relink_rather_than_crashing()
    {
        // A rotated/lost Data-Protection ring makes Unprotect throw CryptographicException from inside the resolve.
        var cut = RenderPage(new FakeTenantContext(tenant: null, fault: new CryptographicException("ring rotated")), Authed());

        cut.Find("[data-testid=link-status]").TextContent.ShouldContain("Re-link required");
        cut.FindAll("[data-testid=usage-relink]").Count.ShouldBe(1);
        cut.FindAll("[data-testid=usage-panel]").ShouldBeEmpty();
    }

    [Fact]
    public void Zero_allowances_and_a_long_wait_render_without_dividing_by_zero()
    {
        var profile = new TenantProfileResponse("tenant-zero", "z@crawldad.test", Tier: null, 0, 0);
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

    // ---- console access + membership (issue #119 PR4) -------------------------------------------------------------

    [Fact]
    public void Console_mode_with_a_membership_shows_the_console_badge_and_the_owner_role()
    {
        // A prior revoked (inactive) membership for the same user is skipped; the active one is the current membership.
        var memberships = new TenantMembershipList(
        [
            new(Guid.NewGuid(), "dana@meridiantitle.co", MembershipRole.Owner, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, false),
            new(Guid.NewGuid(), "dana@meridiantitle.co", MembershipRole.Owner, DateTimeOffset.UnixEpoch, null, true),
        ]);
        var handler = ApiReturning(_profile, _usage, memberships);
        var cut = RenderPage(new FakeTenantContext(Linked("tenant-alpha", handler, PortalAuthMode.Console)), Authed("dana@meridiantitle.co"));

        cut.Find("[data-testid=console-mode]").TextContent.ShouldContain("Console");
        cut.Find("[data-testid=console-key-state]").TextContent.ShouldContain("stored key retained"); // a transition key remains
        cut.Find("[data-testid=membership-status]").TextContent.ShouldContain("Owner");
    }

    [Fact]
    public void Console_mode_with_no_stored_key_shows_the_key_retired_state()
    {
        var memberships = new TenantMembershipList(
            [new(Guid.NewGuid(), "dana@meridiantitle.co", MembershipRole.Owner, DateTimeOffset.UnixEpoch, null, true)]);
        var handler = ApiReturning(_profile, _usage, memberships);
        var cut = RenderPage(
            new FakeTenantContext(Linked("tenant-alpha", handler, PortalAuthMode.Console, storedKeyRetained: false)),
            Authed("dana@meridiantitle.co"));

        cut.Find("[data-testid=console-mode]").TextContent.ShouldContain("Console");
        cut.Find("[data-testid=console-key-state]").TextContent.ShouldContain("no stored key"); // the key was retired
    }

    [Fact]
    public void Key_mode_with_no_membership_shows_the_stored_key_badge_and_no_membership()
    {
        var cut = RenderPage(new FakeTenantContext(Linked("tenant-alpha", ApiReturning(_profile, _usage))), Authed());

        cut.Find("[data-testid=console-mode]").TextContent.ShouldContain("Stored key");
        cut.Find("[data-testid=membership-status]").TextContent.ShouldContain("No membership");
    }

    [Fact]
    public void An_env_tenant_shows_the_operator_managed_membership_state()
    {
        // GET /tenant/memberships is a 400 self_service_unavailable for an env tenant — surfaced as a clean state, no crash.
        var handler = new StubHttpMessageHandler(req =>
            req.Path.EndsWith("tenant/memberships", StringComparison.Ordinal)
                ? ClientTestHarness.JsonRaw(System.Net.HttpStatusCode.BadRequest, "{\"title\":\"self_service_unavailable\"}")
                : req.Path.EndsWith("tenant/keys", StringComparison.Ordinal) ? ClientTestHarness.Json(new TenantApiKeyList([]))
                : req.Path.EndsWith("usage", StringComparison.Ordinal) ? ClientTestHarness.Json(_usage)
                : ClientTestHarness.Json(_profile));
        var cut = RenderPage(new FakeTenantContext(Linked("tenant-alpha", handler)), Authed());

        cut.Find("[data-testid=membership-status]").TextContent.ShouldContain("Operator-managed");
    }

    [Fact]
    public void A_linked_account_with_no_email_and_a_malformed_membership_body_degrades_cleanly()
    {
        // Defensive edges: no email claim (still authorized to render), and a 2xx membership body missing its list — neither
        // crashes the page; the console row simply shows "no membership".
        var handler = new StubHttpMessageHandler(req =>
            req.Path.EndsWith("tenant/memberships", StringComparison.Ordinal) ? ClientTestHarness.JsonRaw(System.Net.HttpStatusCode.OK, "{}")
            : req.Path.EndsWith("tenant/keys", StringComparison.Ordinal) ? ClientTestHarness.Json(new TenantApiKeyList([]))
            : req.Path.EndsWith("usage", StringComparison.Ordinal) ? ClientTestHarness.Json(_usage)
            : ClientTestHarness.Json(_profile));
        var cut = RenderPage(new FakeTenantContext(Linked("tenant-alpha", handler)), Authed(email: null));

        cut.Find("[data-testid=membership-status]").TextContent.ShouldContain("No membership");
    }

    // ---- workspace-link form --------------------------------------------------------------------------------------

    [Fact]
    public void A_successful_link_redirects_to_the_confirmed_page()
    {
        var linker = new FakeLinker(new WorkspaceLinkResult(WorkspaceLinkOutcome.Linked, "ok"));
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
    public void A_failed_link_never_echoes_the_submitted_key_in_the_markup()
    {
        var linker = new FakeLinker(new WorkspaceLinkResult(WorkspaceLinkOutcome.InvalidKey, "That API key was rejected."));
        var cut = RenderPage(new FakeTenantContext(tenant: null), Authed(), linker);

        SubmitLink(cut, "tenant-alpha", "SUPER-SECRET-KEY-9999");

        cut.Markup.ShouldNotContain("SUPER-SECRET-KEY-9999"); // password input, never rendered back
    }

    [Fact]
    public void A_missing_email_claim_is_handled_without_calling_the_linker()
    {
        var linker = new FakeLinker(new WorkspaceLinkResult(WorkspaceLinkOutcome.Linked, "ok"));
        var cut = RenderPage(new FakeTenantContext(tenant: null), Authed(email: null), linker);

        SubmitLink(cut, "tenant-alpha", "sk_live_secret_key");

        linker.Calls.ShouldBeEmpty();
        cut.Find("[data-testid=link-error]").TextContent.ShouldContain("session has expired");
    }

    [Fact]
    public void Blank_fields_are_rejected_client_side_without_calling_the_linker()
    {
        var linker = new FakeLinker(new WorkspaceLinkResult(WorkspaceLinkOutcome.Linked, "ok"));
        var cut = RenderPage(new FakeTenantContext(tenant: null), Authed(), linker);

        cut.Find("#link-form").Submit();

        linker.Calls.ShouldBeEmpty();
        cut.Markup.ShouldContain("Enter your workspace ID.");
        cut.Markup.ShouldContain("Enter your API key.");
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private IRenderedComponent<Account> RenderPage(IPortalTenantContext ctx, HttpContext http, IWorkspaceLinker? linker = null)
    {
        Services.AddSingleton(ctx);
        Services.AddSingleton(linker ?? new FakeLinker(new WorkspaceLinkResult(WorkspaceLinkOutcome.Linked, "ok")));
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

    private static PortalTenant Linked(string tenantId, HttpMessageHandler handler, PortalAuthMode authMode = PortalAuthMode.Key, bool storedKeyRetained = true) =>
        new(tenantId, new CrawldadClient(new HttpClient(handler) { BaseAddress = ClientTestHarness.BaseUrl },
            new CrawldadClientOptions { BaseUrl = ClientTestHarness.BaseUrl, ApiKey = ClientTestHarness.ApiKey }), authMode, storedKeyRetained);

    private static StubHttpMessageHandler ApiReturning(TenantProfileResponse profile, UsageResponse usage, TenantMembershipList? memberships = null) =>
        new(req =>
            req.Path.EndsWith("tenant/keys", StringComparison.Ordinal) ? ClientTestHarness.Json(new TenantApiKeyList([]))
            : req.Path.EndsWith("tenant/memberships", StringComparison.Ordinal) ? ClientTestHarness.Json(memberships ?? new TenantMembershipList([]))
            : req.Path.EndsWith("usage", StringComparison.Ordinal) ? ClientTestHarness.Json(usage)
            : ClientTestHarness.Json(profile));

    private static DefaultHttpContext Authed(string? email = "owner@example.com")
    {
        Claim[] claims = email is null ? [] : [new Claim(ClaimTypes.Email, email)];
        return new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "TestCookie")) };
    }

    private sealed class FakeTenantContext(PortalTenant? tenant, Exception? fault = null) : IPortalTenantContext
    {
        public Task<PortalTenant?> TryResolveAsync(CancellationToken cancellationToken = default) =>
            fault is not null ? Task.FromException<PortalTenant?>(fault) : Task.FromResult(tenant);

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
