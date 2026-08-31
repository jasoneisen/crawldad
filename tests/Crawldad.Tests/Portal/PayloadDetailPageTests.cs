using System.Net;
using System.Text.Json;
using Bunit;
using Crawldad.Contracts.Drift;
using Crawldad.Contracts.Payloads;
using Crawldad.Contracts.Runs;
using Crawldad.Portal.Components.Pages.App;
using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Client;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Portal;

/// <summary>bUnit rendering of the read-only payload detail page (<c>/app/payloads/{id}</c>): the not-linked and
/// not-found empty states, the header + revision viewer (with a <c>?rev=N</c> selection clamped to 1..head), and the
/// drift card across its NoData / degraded / populated shapes.</summary>
public class PayloadDetailPageTests : BunitContext
{
    private static readonly Guid _id = Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301");

    private void Use(IPortalTenantContext context) => Services.AddSingleton(context);

    private IRenderedComponent<PayloadDetail> RenderDetail(string? query = null)
    {
        if (query is not null)
        {
            Services.GetRequiredService<NavigationManager>().NavigateTo($"/app/payloads/{_id}{query}");
        }

        return Render<PayloadDetail>(ps => ps.Add(p => p.PayloadId, _id.ToString()));
    }

    [Fact]
    public void Not_linked_shows_the_link_your_workspace_empty_state()
    {
        Use(PayloadsWebhooksTenantContext.NotLinked());

        RenderDetail().Find("[data-testid=not-linked]").TextContent.ShouldContain("No workspace yet");
    }

    [Fact]
    public void A_forbidden_read_shows_the_workspace_unavailable_state_not_a_500()
    {
        // A stale/suspended active-workspace selection: the console gate 403s the payload read (a non-404 failure). Degrade
        // to the honest "unavailable" state pointing at Account — never a 500.
        Use(PayloadsWebhooksTenantContext.LinkedTo(new StubHttpMessageHandler(_ => ClientTestHarness.Empty(HttpStatusCode.Forbidden))));

        var cut = RenderDetail();

        cut.Find("[data-testid=workspace-unavailable]").ShouldNotBeNull();
        cut.Find("a[href=\"/app/account\"]").ShouldNotBeNull();
    }

    [Fact]
    public void A_malformed_id_is_not_found_without_a_call()
    {
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Empty(HttpStatusCode.InternalServerError));
        Use(PayloadsWebhooksTenantContext.LinkedTo(handler));

        var cut = Render<PayloadDetail>(ps => ps.Add(p => p.PayloadId, "not-a-guid"));

        cut.Find("[data-testid=not-found]").TextContent.ShouldContain("Payload not found");
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public void A_missing_payload_is_not_found()
    {
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Empty(HttpStatusCode.NotFound));
        Use(PayloadsWebhooksTenantContext.LinkedTo(handler));

        RenderDetail().Find("[data-testid=not-found]").TextContent.ShouldContain("Payload not found");
    }

    [Fact]
    public void Default_view_shows_the_head_revision_and_the_full_revision_ladder()
    {
        Use(PayloadsWebhooksTenantContext.LinkedTo(FullHandler(head: 5, driftState: DriftState.Steady)));

        var cut = RenderDetail();

        cut.Find("[data-testid=status]").TextContent.ShouldBe("active");
        cut.Find("[data-testid=viewing-rev]").TextContent.ShouldContain("r5");
        cut.Find("[data-testid=viewing-rev]").TextContent.ShouldContain("head");
        cut.Find("[data-testid=script]").TextContent.ShouldContain("\"rev\": 5");
        cut.FindAll("[data-testid=revision-row]").Count.ShouldBe(5); // r5..r1
    }

    [Fact]
    public void A_rev_query_selects_that_revision_clamped_to_head()
    {
        Use(PayloadsWebhooksTenantContext.LinkedTo(FullHandler(head: 5, driftState: DriftState.Steady)));

        // ?rev=99 is clamped down to the head (5); ?rev=2 selects r2.
        RenderDetail("?rev=2").Find("[data-testid=script]").TextContent.ShouldContain("\"rev\": 2");
        RenderDetail("?rev=99").Find("[data-testid=viewing-rev]").TextContent.ShouldContain("r5");
    }

    [Fact]
    public void Drifted_payload_shows_the_chip_selector_table_and_evidence()
    {
        var runId = Guid.NewGuid();
        // pinned:false exercises the "no pinned revision" datacard branch; the three selectors span
        // missing/present, baseline-floor yes/no, and drifted/not-alarmed.
        var handler = FullHandler(head: 3, driftState: DriftState.Drifted, drifted: true, pinned: false, selectors:
        [
            new SelectorDriftDetail("#ctl00_lblRecordNumber", Drifted: true, BaselineFloor: false, MissingInLatest: true),
            new SelectorDriftDetail("#ctl00_lblOwner", Drifted: false, BaselineFloor: true, MissingInLatest: true),
            new SelectorDriftDetail("#present", Drifted: false, BaselineFloor: false, MissingInLatest: false),
        ], evidence: new DriftEvidence(runId, RunStatus.Failed, DateTimeOffset.UnixEpoch, "screenshots/x", ["captures/a"], ["screenshots/b"]));
        Use(PayloadsWebhooksTenantContext.LinkedTo(handler));

        var cut = RenderDetail();

        cut.Find("[data-testid=drift-chip]").TextContent.ShouldContain("Drift detected");
        cut.Find("[data-testid=drift-state]").TextContent.ShouldBe("drifted");
        cut.FindAll("[data-testid=selector-row]").Count.ShouldBe(3);
        cut.Markup.ShouldContain("present");
        cut.Find($"a[href='/app/runs/{runId}']").ShouldNotBeNull();
        cut.Find("[data-testid=evidence]").TextContent.ShouldContain("failing-step screenshot");
    }

    [Fact]
    public void An_archived_payload_with_warming_up_drift_and_a_short_hash_renders()
    {
        Use(PayloadsWebhooksTenantContext.LinkedTo(
            FullHandler(head: 1, driftState: DriftState.WarmingUp, status: PayloadStatus.Archived, hash: "abc123")));

        var cut = RenderDetail();

        cut.Find("[data-testid=status]").TextContent.ShouldBe("archived");
        cut.Find("[data-testid=drift-state]").TextContent.ShouldBe("warming up");
        // A short hash is shown verbatim (no ellipsis truncation).
        cut.Find("[data-testid=identity]").TextContent.ShouldContain("abc123");
        cut.Find("[data-testid=identity]").TextContent.ShouldNotContain("…");
    }

    [Fact]
    public void No_canary_data_shows_the_nodata_note_and_no_chip()
    {
        Use(PayloadsWebhooksTenantContext.LinkedTo(FullHandler(head: 1, driftState: DriftState.NoData)));

        var cut = RenderDetail();

        cut.Find("[data-testid=drift-nodata]").ShouldNotBeNull();
        cut.FindAll("[data-testid=drift-chip]").ShouldBeEmpty();
        cut.FindAll("[data-testid=selector-row]").ShouldBeEmpty();
    }

    [Fact]
    public void A_failed_drift_read_drops_the_drift_card_but_keeps_the_page()
    {
        var handler = new StubHttpMessageHandler(req =>
        {
            if (req.Path.EndsWith("/drift-status", StringComparison.Ordinal))
            {
                return ClientTestHarness.Empty(HttpStatusCode.NotFound);
            }

            return RespondPayload(req, head: 2);
        });
        Use(PayloadsWebhooksTenantContext.LinkedTo(handler));

        var cut = RenderDetail();

        cut.FindAll("[data-testid=drift-card]").ShouldBeEmpty();
        cut.Find("[data-testid=script]").ShouldNotBeNull(); // the rest of the page still renders
    }

    [Fact]
    public void A_transient_5xx_drift_read_also_drops_the_card_but_keeps_the_page()
    {
        // A 500 is a CrawldadApiException, not a NotFound. Before the widen it failed the whole page; now the drift read
        // degrades exactly like the payloads LIST does — dropping only the drift card while the page still renders.
        var handler = new StubHttpMessageHandler(req =>
        {
            if (req.Path.EndsWith("/drift-status", StringComparison.Ordinal))
            {
                return ClientTestHarness.Empty(HttpStatusCode.InternalServerError);
            }

            return RespondPayload(req, head: 2);
        });
        Use(PayloadsWebhooksTenantContext.LinkedTo(handler));

        var cut = RenderDetail();

        cut.FindAll("[data-testid=drift-card]").ShouldBeEmpty();
        cut.Find("[data-testid=script]").ShouldNotBeNull();
    }

    // A handler that answers the three detail reads (payload, revision, drift-status).
    private static StubHttpMessageHandler FullHandler(
        int head,
        DriftState driftState,
        bool drifted = false,
        IReadOnlyList<SelectorDriftDetail>? selectors = null,
        DriftEvidence? evidence = null,
        bool pinned = true,
        PayloadStatus status = PayloadStatus.Active,
        string hash = "abcdef0123456789")
    {
        var drift = new PayloadDriftStatus(_id, "p", pinned ? head : null, driftState, drifted, 7, 3, drifted ? 1 : 0, 0,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, selectors ?? [], evidence);
        return new StubHttpMessageHandler(req =>
            req.Path.EndsWith("/drift-status", StringComparison.Ordinal)
                ? ClientTestHarness.Json(drift)
                : RespondPayload(req, head, status, hash));
    }

    // The payload identity read and the per-revision script read.
    private static HttpResponseMessage RespondPayload(CapturedRequest req, int head, PayloadStatus status = PayloadStatus.Active, string hash = "abcdef0123456789")
    {
        if (req.Path.Contains("/revisions/", StringComparison.Ordinal))
        {
            var revision = int.Parse(req.Path[(req.Path.LastIndexOf('/') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            return ClientTestHarness.Json(new PayloadRevisionResponse(_id, revision, hash, Script(revision)));
        }

        return ClientTestHarness.Json(new PayloadResponse(_id, "county.parcel.search-detail", head, hash, status));
    }

    private static JsonElement Script(int revision)
    {
        using var document = JsonDocument.Parse($$"""{ "rev": {{revision}} }""");
        return document.RootElement.Clone();
    }
}
