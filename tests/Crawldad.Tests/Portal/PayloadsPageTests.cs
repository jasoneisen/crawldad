using Bunit;
using Crawldad.Contracts.Drift;
using Crawldad.Contracts.Payloads;
using Crawldad.Portal.Components.Pages.App;
using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Portal;

/// <summary>bUnit rendering of the payload registry list (<c>/app/payloads</c>): the not-linked empty state, the
/// linked-but-empty state, and a populated registry whose per-row drift badge reflects each <see cref="DriftState"/> and
/// degrades to no badge when the drift read fails.</summary>
public class PayloadsPageTests : BunitContext
{
    private void Use(IPortalTenantContext context) => Services.AddSingleton(context);

    [Fact]
    public void No_workspace_shows_the_get_started_empty_state()
    {
        Use(PayloadsWebhooksTenantContext.NotLinked());

        var cut = Render<Payloads>();

        cut.Find("[data-testid=not-linked]").TextContent.ShouldContain("No workspace yet");
        cut.Markup.ShouldContain("/app/account");
        cut.FindAll("[data-testid=tenant]").ShouldBeEmpty();
    }

    [Fact]
    public void Unconfigured_console_shows_the_honest_not_configured_state()
    {
        // Console access is unconfigured on the deployment (an operator misconfig): the shared WorkspaceEmptyState renders
        // the honest "console access isn't configured" state — never a 500, never a misleading "no workspace" prompt.
        Use(PayloadsWebhooksTenantContext.NotConfigured());

        var cut = Render<Payloads>();

        cut.Find("[data-testid=console-unconfigured]").TextContent.ShouldContain("Console access isn't configured");
    }

    [Fact]
    public void A_forbidden_read_shows_the_workspace_unavailable_state_not_a_500()
    {
        // A stale/suspended active-workspace selection: the console gate 403s the list read. Degrade to the honest
        // "unavailable" state pointing at Account — never a 500.
        Use(PayloadsWebhooksTenantContext.LinkedTo(new StubHttpMessageHandler(_ => ClientTestHarness.Empty(System.Net.HttpStatusCode.Forbidden))));

        var cut = Render<Payloads>();

        cut.Find("[data-testid=workspace-unavailable]").ShouldNotBeNull();
        cut.Find("a[href=\"/app/account\"]").ShouldNotBeNull();
    }

    [Fact]
    public void Linked_but_empty_registry_shows_the_no_payloads_state()
    {
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Json(new PayloadListResponse([])));
        Use(PayloadsWebhooksTenantContext.LinkedTo(handler));

        var cut = Render<Payloads>();

        cut.Find("[data-testid=empty]").TextContent.ShouldContain("No payloads yet");
        // The raw tenant id is no longer rendered on data pages (issue #119 id de-emphasis — it lives only in Account).
        cut.FindAll("[data-testid=tenant]").ShouldBeEmpty();
    }

    [Fact]
    public void Populated_registry_renders_a_row_per_payload_with_a_drift_badge()
    {
        var drifted = Guid.NewGuid();
        var steady = Guid.NewGuid();
        var warming = Guid.NewGuid();
        var nodata = Guid.NewGuid();
        var errored = Guid.NewGuid();

        var handler = new StubHttpMessageHandler(req =>
        {
            if (string.Equals(req.Path, "/payloads", StringComparison.Ordinal))
            {
                return ClientTestHarness.Json(new PayloadListResponse(
                [
                    Item(drifted, "county.parcel.search-detail", 5, PayloadStatus.Active),
                    Item(steady, "accela.permits.search", 8, PayloadStatus.Active),
                    Item(warming, "recorder.deed.capture", 11, PayloadStatus.Active),
                    Item(nodata, "sos.business.lookup", 2, PayloadStatus.Active),
                    Item(errored, "legacy.taxroll.scrape", 3, PayloadStatus.Archived),
                ]));
            }

            if (req.Path.Contains(drifted.ToString(), StringComparison.Ordinal))
            {
                return ClientTestHarness.Json(Drift(drifted, DriftState.Drifted, drifted: true));
            }

            if (req.Path.Contains(steady.ToString(), StringComparison.Ordinal))
            {
                return ClientTestHarness.Json(Drift(steady, DriftState.Steady, drifted: false));
            }

            if (req.Path.Contains(warming.ToString(), StringComparison.Ordinal))
            {
                return ClientTestHarness.Json(Drift(warming, DriftState.WarmingUp, drifted: false));
            }

            if (req.Path.Contains(nodata.ToString(), StringComparison.Ordinal))
            {
                return ClientTestHarness.Json(Drift(nodata, DriftState.NoData, drifted: false));
            }

            return ClientTestHarness.Empty(System.Net.HttpStatusCode.NotFound); // errored payload's drift read fails
        });
        Use(PayloadsWebhooksTenantContext.LinkedTo(handler));

        var cut = Render<Payloads>();

        cut.FindAll("[data-testid=payload-row]").Count.ShouldBe(5);
        cut.Find("[data-testid=count]").TextContent.ShouldBe("5");
        cut.FindAll("[data-testid=drift]").Select(node => node.TextContent).ShouldBe(
            ["drifted", "steady", "warming up", "no data"]);
        // The row whose drift read 404'd shows no badge, just the em dash.
        cut.FindAll("[data-testid=drift-unknown]").Count.ShouldBe(1);
        // Names link to the detail route; the archived payload carries the archived status badge.
        cut.Find($"a[href='/app/payloads/{drifted}']").TextContent.ShouldBe("county.parcel.search-detail");
        cut.Markup.ShouldContain("archived");
    }

    private static PayloadListItem Item(Guid id, string name, int revision, PayloadStatus status) =>
        new(id, name, revision, "abcdef0123456789", status, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static PayloadDriftStatus Drift(Guid id, DriftState state, bool drifted) =>
        new(id, "p", null, state, drifted, 0, 0, 0, 0, null, null, [], null);
}
