using Bunit;
using Crawldad.Portal.Components.Pages;

namespace Crawldad.Tests.Portal;

/// <summary>bUnit rendering of the public marketing landing page ("/"): the above-the-fold pitch, the required
/// sections, conversion CTAs that route to /login, the BUSINESS_MODEL.md pricing numbers, and the copy-accuracy
/// invariant that webhooks + drift monitoring are presented as shipped (never "coming soon"). The page is pure
/// static markup, so a single render exercises it fully; the full-pipeline render (through LandingLayout) is
/// covered by the HTTP test.</summary>
public class MarketingHomeTests : BunitContext
{
    private IRenderedComponent<Home> RenderHome() => Render<Home>();

    [Fact]
    public void Leads_with_the_dream_outcome_headline()
    {
        var cut = RenderHome();

        var h1 = cut.Find("h1").TextContent;
        h1.ShouldContain("Browser automations that");
        h1.ShouldContain("rot");
    }

    [Fact]
    public void Covers_every_required_section()
    {
        var cut = RenderHome();

        // above-the-fold pitch, how-it-works, personas/on-ramps, features, pricing, faq, contact/CTA
        foreach (var id in new[] { "top", "how", "personas", "features", "pricing", "faq", "contact" })
        {
            cut.FindAll($"#{id}").Count.ShouldBe(1, $"expected exactly one #{id} section");
        }
    }

    [Fact]
    public void Conversion_ctas_route_to_the_existing_login()
    {
        var cut = RenderHome();

        // nav (sign in + start free), hero, the three self-serve plan cards, the final CTA, and the footer sign-in.
        cut.FindAll("a[href=\"/login\"]").Count.ShouldBe(8);

        // The primary hero CTA specifically.
        var hero = cut.Find("section#top");
        var primary = hero.QuerySelector("a.btn-brand");
        primary!.GetAttribute("href").ShouldBe("/login");
        primary.TextContent.Trim().ShouldBe("Start free");
    }

    [Fact]
    public void Ships_no_dead_placeholder_anchors()
    {
        var cut = RenderHome();

        // Every link resolves to a real in-page anchor, /login, or a mailto: — none of the mockup's "#" stubs.
        cut.FindAll("a[href=\"#\"]").ShouldBeEmpty();
    }

    [Fact]
    public void Prices_the_four_tiers_on_the_business_model_numbers()
    {
        var cut = RenderHome();
        var markup = cut.Markup;

        // Tier names + headline prices (docs/BUSINESS_MODEL.md).
        foreach (var token in new[] { "Free", "Team", "Scale", "Enterprise", "$0", "$99", "$499", "Custom" })
        {
            markup.ShouldContain(token);
        }

        // Included concurrent-slot counts and the featured tier.
        foreach (var token in new[] { "2 slots", "10 slots", "50 slots", "Most popular" })
        {
            markup.ShouldContain(token);
        }

        // The slot-ladder worked example and fair-use guardrails are reproduced verbatim from the doc.
        markup.ShouldContain("$2,099/mo");
    }

    [Fact]
    public void Presents_webhooks_and_drift_as_shipped_not_coming_soon()
    {
        var cut = RenderHome();
        var markup = cut.Markup;

        // Copy-accuracy guard. What ships and must be stated plainly: signed webhooks, and on-READ drift
        // detection (GET /payloads/{id}/drift-status, GET /runs/{id}/drift, SelectorMiss events).
        markup.ShouldContain("Signed webhooks");
        markup.ShouldContain("Drift monitoring");           // the feature-card label is accurate
        markup.ShouldContain("drift status on demand");     // the shipped on-read pull model, not an automatic monitor
        markup.ShouldContain("live in the product today");  // grid integrity: everything shown is shipped

        // What is NOT shipped and must not be claimed: the SCHEDULED canary that would poll/alert proactively is
        // roadmap (issue #47, fed by #7). No canary language, and nothing hedged as unavailable either.
        markup.ShouldNotContain("canar", Case.Insensitive); // "canary"/"canaries" — the scheduled monitor is roadmap
        markup.ShouldNotContain("coming soon", Case.Insensitive);
        markup.ShouldNotContain("roadmap", Case.Insensitive);
    }

    [Fact]
    public void Carries_the_risk_reversal_promises()
    {
        var cut = RenderHome();
        var markup = cut.Markup;

        markup.ShouldContain("Transparency");
        markup.ShouldContain("Export everything");
        markup.ShouldContain("Engine-fault credit");
        markup.ShouldContain("30-day refund");
    }
}
