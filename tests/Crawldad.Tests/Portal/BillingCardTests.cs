using System.Net;
using System.Security.Claims;
using Bunit;
using Crawldad.Client;
using Crawldad.Contracts.Billing;
using Crawldad.Contracts.Tenancy;
using Crawldad.Portal.Components.Pages.App;
using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Client;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Portal;

/// <summary>bUnit rendering of the account page's billing card: the plan ladder + current plan + upgrade/manage controls
/// when configured, the friendly "not yet available" state when the provider is unconfigured or the config call fails,
/// the "link your workspace" prompt when unlinked, and the "Free" fallback when no tier is marked current.</summary>
public class BillingCardTests : BunitContext
{
    private static readonly TenantProfileResponse _profile = new("tenant-alpha", "alpha@crawldad.test", "Team", 10, 100);

    private static readonly UsageResponse _usage = new(
        new UsageSlots(1, 10), new UsageQueueStats(0, 0, 0), 5, new UsageEvents(10000, 1, 10, 10));

    private static readonly BillingConfigResponse _configured = new(true, "team",
    [
        new BillingTierOption("free", "Free", "$0", 2, false, false),
        new BillingTierOption("team", "Team", "$99/mo", 10, true, true),      // current
        new BillingTierOption("scale", "Scale", "$499/mo", 50, true, false),  // upgrade
        new BillingTierOption("enterprise", "Enterprise", "Custom", null, false, false), // contact sales
    ]);

    public BillingCardTests() =>
        Services.AddSingleton<AntiforgeryStateProvider>(new StubAntiforgeryStateProvider());

    [Fact]
    public void The_billing_card_shows_the_plan_ladder_when_configured()
    {
        var cut = Render(Linked(Api(_configured)));

        cut.Find("[data-testid=billing-panel]").ShouldNotBeNull();
        cut.Find("[data-testid=billing-current]").TextContent.ShouldContain("Team");
        cut.Find("[data-testid=billing-tier-current]").ShouldNotBeNull();       // team is current → badge, no upgrade button
        cut.FindAll("[data-testid=billing-upgrade-team]").ShouldBeEmpty();
        cut.Find("[data-testid=billing-upgrade-scale]").ShouldNotBeNull();      // scale is a self-serve upgrade
        cut.Find("[data-testid=billing-manage]").ShouldNotBeNull();
        cut.Markup.ShouldContain("Contact sales");                              // free / enterprise are not self-serve
    }

    [Fact]
    public void The_upgrade_form_posts_to_the_checkout_endpoint_with_the_tier()
    {
        var cut = Render(Linked(Api(_configured)));

        var form = cut.Find("[data-testid=billing-upgrade-scale]").Closest("form")!;
        form.GetAttribute("action").ShouldBe("/app/billing/checkout");
        form.QuerySelector("input[name=tier]")!.GetAttribute("value").ShouldBe("scale");
    }

    [Fact]
    public void The_billing_card_shows_not_available_when_the_provider_is_unconfigured()
    {
        var unconfigured = new BillingConfigResponse(false, null, []);
        var cut = Render(Linked(Api(unconfigured)));

        cut.Find("[data-testid=billing-unavailable]").ShouldNotBeNull();
        cut.FindAll("[data-testid=billing-panel]").ShouldBeEmpty();
    }

    [Fact]
    public void The_billing_card_shows_not_available_when_the_config_call_fails()
    {
        // The /billing/config call errors (a stored key the API now rejects) → the card degrades, never crashes.
        var cut = Render(Linked(Api(billing: null)));

        cut.Find("[data-testid=billing-unavailable]").ShouldNotBeNull();
    }

    [Fact]
    public void The_billing_card_prompts_to_link_when_unlinked()
    {
        var cut = Render(new FakeTenantContext(null));

        cut.Find("[data-testid=billing-unlinked]").ShouldNotBeNull();
        cut.FindAll("[data-testid=billing-panel]").ShouldBeEmpty();
    }

    [Fact]
    public void The_current_plan_defaults_to_free_when_none_is_marked_current()
    {
        var noCurrent = new BillingConfigResponse(true, null,
        [
            new BillingTierOption("team", "Team", "$99/mo", 10, true, false),
        ]);
        var cut = Render(Linked(Api(noCurrent)));

        cut.Find("[data-testid=billing-current]").TextContent.ShouldContain("Free");
    }

    private IRenderedComponent<Account> Render(IPortalTenantContext ctx)
    {
        Services.AddSingleton(ctx);
        Services.AddSingleton<IWorkspaceLinker>(new NoopLinker());
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Email, "owner@example.com")], "TestCookie")),
        };
        return Render<Account>(ps => ps.AddCascadingValue<HttpContext>(http));
    }

    private static FakeTenantContext Linked(StubHttpMessageHandler handler) =>
        new(new PortalTenant("tenant-alpha", new CrawldadClient(
            new HttpClient(handler) { BaseAddress = ClientTestHarness.BaseUrl },
            new CrawldadClientOptions { BaseUrl = ClientTestHarness.BaseUrl, ApiKey = ClientTestHarness.ApiKey })));

    // Routes /billing/config → the config (or 503 when null, simulating a failed call), /usage → usage, else → profile.
    private static StubHttpMessageHandler Api(BillingConfigResponse? billing) =>
        new(req =>
            req.Path.EndsWith("tenant/keys", StringComparison.Ordinal) ? ClientTestHarness.Json(new TenantApiKeyList([]))
            : req.Path.EndsWith("billing/config", StringComparison.Ordinal)
                ? (billing is null ? ClientTestHarness.Empty(HttpStatusCode.ServiceUnavailable) : ClientTestHarness.Json(billing))
            : req.Path.EndsWith("usage", StringComparison.Ordinal)
                ? ClientTestHarness.Json(_usage)
                : ClientTestHarness.Json(_profile));

    private sealed class FakeTenantContext(PortalTenant? tenant) : IPortalTenantContext
    {
        public Task<PortalTenant?> TryResolveAsync(CancellationToken cancellationToken = default) => Task.FromResult(tenant);

        public Task<PortalTenant> RequireAsync(CancellationToken cancellationToken = default) => Task.FromResult(tenant!);
    }

    private sealed class NoopLinker : IWorkspaceLinker
    {
        public Task<WorkspaceLinkResult> LinkAsync(string email, string tenantId, string apiKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkspaceLinkResult(WorkspaceLinkOutcome.Linked, "ok"));
    }
}
