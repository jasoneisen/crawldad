using System.IO;
using Crawldad.Api.Infrastructure.Browser;
using Crawldad.Tests.Support;

namespace Crawldad.Tests.Integration;

/// <summary>Fake≡real parity for the structured <c>Sel</c> role/text/xpath variants: drives the same page the fake
/// serves (<see cref="Unit.SelectorVariantTests"/>) through real headless Chromium, asserting <c>GetByRole</c>,
/// <c>GetByText</c>, and <c>xpath=</c> resolve identically — Playwright itself is the oracle. Zero live traffic.</summary>
[Collection(RealChromiumCollection.Name)]
public sealed class RealChromiumSelectorParityTests(RealChromiumFixture fixture)
{
    private static readonly CancellationToken _ct = CancellationToken.None;

    private static string Fixture(string file) =>
        File.ReadAllText(Path.Combine(Runner.FixturesRoot, "selector-variants", file));

    private static LocalSite Site() => new LocalSite()
        .Map("/page.html", "text/html", Fixture("page.html"))
        .Map("/record.html", "text/html", Fixture("record.html"));

    private async Task<IPageHandle> OpenAsync(LocalSite site)
    {
        var binding = new BackendBinding("local", null, null);
        var session = await fixture.LocalBackend.ConnectAsync(binding, SessionPolicy.Default, _ct);
        var page = await session.NewPageAsync(_ct);
        await page.GotoAsync(site.Url("/page.html"), "load", null, _ct);
        return page;
    }

    [Fact]
    public async Task GetByRole_matches_and_names_exactly_as_the_fake_does()
    {
        using var site = Site();
        var page = await OpenAsync(site);

        // role set (name null — the option-free GetByRole path)
        (await page.GetByRole("button", null).CountAsync(_ct)).ShouldBe(2);
        (await page.GetByRole("link", null).CountAsync(_ct)).ShouldBe(2);
        (await page.GetByRole("heading", null).CountAsync(_ct)).ShouldBe(1);
        (await page.GetByRole("textbox", null).CountAsync(_ct)).ShouldBe(1);
        (await page.GetByRole("listitem", null).CountAsync(_ct)).ShouldBe(3);
        (await page.GetByRole("switch", null).CountAsync(_ct)).ShouldBe(1);      // AriaRole.Switch, an explicit role
        (await page.GetByRole("switch", "Toggle").CountAsync(_ct)).ShouldBe(1);  // …and its accessible name (text)

        // accessible name (the option-bearing GetByRole path): text and aria-label sources, case-insensitive substring
        (await page.GetByRole("button", "Search Records").CountAsync(_ct)).ShouldBe(1);
        (await page.GetByRole("button", "Reset the form").CountAsync(_ct)).ShouldBe(1); // aria-label
        (await page.GetByRole("heading", "enforcement").CountAsync(_ct)).ShouldBe(1);   // substring
        (await page.GetByRole("link", "Open Record").TextContentAsync(_ct)).ShouldBe("Open Record");
        (await page.GetByRole("button", "Nonexistent").CountAsync(_ct)).ShouldBe(0);     // no-match
    }

    [Fact]
    public async Task GetByText_matches_the_innermost_carrier_exactly_as_the_fake_does()
    {
        using var site = Site();
        var page = await OpenAsync(site);

        (await page.GetByText("Open Record").TextContentAsync(_ct)).ShouldBe("Open Record");
        (await page.GetByText("Ready to search").CountAsync(_ct)).ShouldBe(1);  // the inner <span>, not its <div>
        (await page.GetByText("notice").CountAsync(_ct)).ShouldBe(2);           // both <p class="note"> (multi-match)
        (await page.GetByText("no such copy anywhere").CountAsync(_ct)).ShouldBe(0); // no-match
    }

    [Fact]
    public async Task Xpath_engine_queries_the_document_exactly_as_the_fake_does()
    {
        using var site = Site();
        var page = await OpenAsync(site);

        (await page.Locator("xpath=//button").CountAsync(_ct)).ShouldBe(2);
        (await page.Locator("xpath=//a[@id='openRecord']").TextContentAsync(_ct)).ShouldBe("Open Record");
        (await page.Locator("xpath=//li").CountAsync(_ct)).ShouldBe(3);
        (await page.Locator("xpath=//table").CountAsync(_ct)).ShouldBe(0); // no-match
    }

    [Fact]
    public async Task Nth_agrees_with_the_fake_in_range_and_diverges_on_a_negative_index()
    {
        using var site = Site();
        var page = await OpenAsync(site);
        var li = page.Locator("li"); // the same three <li> (Alpha / Bravo / Charlie) the fake serves

        // Accepted 0-based domain, identical to the fake: in-range narrows to one, past-the-end to none — the domain
        // the interpreter lets through to a backend, and it is fake≡real.
        (await li.Nth(0).TextContentAsync(_ct)).ShouldBe("Alpha item");
        (await li.Nth(2).TextContentAsync(_ct)).ShouldBe("Charlie item");
        (await li.Nth(3).CountAsync(_ct)).ShouldBe(0);

        // DIVERGENCE — empirical basis for rejecting a negative nth: Playwright's Nth(-1) is the LAST element, while
        // the fake yields no match for a negative index. A negative nth can't mean the same thing on both backends,
        // so the interpreter classifies it (index_out_of_range) before it reaches either.
        (await li.Nth(-1).CountAsync(_ct)).ShouldBe(1);
        (await li.Nth(-1).TextContentAsync(_ct)).ShouldBe("Charlie item");
    }

    [Fact]
    public async Task Click_on_a_role_selected_link_navigates()
    {
        using var site = Site();
        var page = await OpenAsync(site);

        // the role=link "Open Record" resolves to the single #openRecord anchor; clicking it navigates to /record.html.
        await page.RunAndWaitForRequestAsync(
            () => page.GetByRole("link", "Open Record").ClickAsync(null, _ct),
            site.Url("/record.html"), null, null, _ct);

        (await page.Locator("#recordMarker").TextContentAsync(_ct)).ShouldBe("Record 42");
    }

    [Fact]
    public async Task Fill_on_a_role_selected_textbox_targets_the_one_input()
    {
        using var site = Site();
        var page = await OpenAsync(site);

        var textbox = page.GetByRole("textbox", "Start Date");
        (await textbox.CountAsync(_ct)).ShouldBe(1); // resolves the single input on both backends
        await Should.NotThrowAsync(() => textbox.FillAsync("01/02/2026", _ct));
    }
}
