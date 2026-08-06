using AngleSharp.Dom;
using Crawldad.Tests.Support;
using Crawldad.Web.Infrastructure.Browser;
using Crawldad.Web.Infrastructure.Browser.Fake;

namespace Crawldad.Tests.Unit;

/// <summary>
/// The record/replay <see cref="FakeBrowserBackend"/> and its lazy AngleSharp locators: navigation, transitions,
/// lazy re-query, fill/clear mutation, textContent/entity/concatenation semantics, GetByTitle, Filter, and the
/// wait/request primitives — all against the shipped caphome-search fixture.
/// </summary>
public class FakeBackendTests
{
    private static async Task<FakePageHandle> ResultsPageAsync()
    {
        var page = await Runner.FakePageAsync();
        await page.Locator(CapHome.SearchButton).ClickAsync(null, CapHome.Ct);
        return page;
    }

    [Fact]
    public async Task Page_starts_on_the_initial_state()
    {
        var page = await Runner.FakePageAsync();
        page.CurrentStateName.ShouldBe("form");
        page.Url.ShouldBe(CapHome.FormUrl);
    }

    [Fact]
    public async Task Goto_loads_the_matching_state_else_the_initial_state()
    {
        var page = await Runner.FakePageAsync();
        await page.GotoAsync(CapHome.FormUrl, null, null, CapHome.Ct);
        page.CurrentStateName.ShouldBe("form");

        await page.GotoAsync("https://example.com/somewhere-else", "load", 1000, CapHome.Ct);
        page.CurrentStateName.ShouldBe("form"); // no gotoUrl matches ⇒ initial state
    }

    [Fact]
    public async Task Locators_are_lazy_and_re_query_after_a_transition()
    {
        var page = await Runner.FakePageAsync();
        var rows = page.Locator(CapHome.GridRows);
        (await rows.CountAsync(CapHome.Ct)).ShouldBe(0); // form has no grid

        await page.Locator(CapHome.SearchButton).ClickAsync(null, CapHome.Ct); // postback ⇒ swap to results
        page.CurrentStateName.ShouldBe("results");

        (await rows.CountAsync(CapHome.Ct)).ShouldBe(15); // SAME handle now resolves the freshly rendered grid
    }

    [Fact]
    public async Task Fill_and_clear_mutate_the_value_attribute()
    {
        var page = await Runner.FakePageAsync();
        var start = page.Locator(CapHome.StartDate);

        await start.FillAsync("01/01/2024", CapHome.Ct);
        (await start.GetAttributeAsync("value", CapHome.Ct)).ShouldBe("01/01/2024");

        await start.ClearAsync(CapHome.Ct);
        (await start.GetAttributeAsync("value", CapHome.Ct)).ShouldBe(string.Empty);
    }

    [Fact]
    public async Task Fill_and_clear_on_a_missing_element_are_no_ops()
    {
        var page = await Runner.FakePageAsync();
        var missing = page.Locator("#does-not-exist");

        await Should.NotThrowAsync(async () => await missing.FillAsync("x", CapHome.Ct));
        await Should.NotThrowAsync(async () => await missing.ClearAsync(CapHome.Ct));
        (await missing.CountAsync(CapHome.Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Running_the_fragment_fills_the_date_inputs_in_the_form_dom()
    {
        const string Inputs =
            """{ "backend": { "adapter": "fake", "options": { "fixture": "caphome-search" } }, "startDate": "01/01/2024", "endDate": "01/31/2024" }""";

        var (outcome, backend) = await Runner.RunWithFakeAsync(Runner.FragmentPayload(), Inputs);

        outcome.Status.ToString().ShouldBe("Succeeded");
        var form = backend.LastSession!.LastPage!.DocumentForState("form");
        form.QuerySelector(CapHome.StartDate)!.GetAttribute("value").ShouldBe("01/01/2024");
        form.QuerySelector(CapHome.EndDate)!.GetAttribute("value").ShouldBe("01/31/2024");
    }

    [Fact]
    public async Task TextContent_decodes_entities_and_concatenates_multi_node_cells()
    {
        var rows = (await ResultsPageAsync()).Locator(CapHome.GridRows);

        // data row 2 (index 4): "200 Oak &amp; Pine Ave" ⇒ decoded; "Owner&#39;s ..." ⇒ apostrophe
        (await rows.Nth(4).Locator("td:nth-child(5)").TextContentAsync(CapHome.Ct)).ShouldBe("200 Oak & Pine Ave");
        (await rows.Nth(4).Locator("td:nth-child(7)").TextContentAsync(CapHome.Ct)).ShouldBe("Owner's response pending");

        // data row 5 (index 7): nbsp-padded status (raw, untrimmed) + multi-span notes concatenated
        (await rows.Nth(7).Locator("td:nth-child(6)").TextContentAsync(CapHome.Ct)).ShouldBe(" Closed ");
        (await rows.Nth(7).Locator("td:nth-child(7)").TextContentAsync(CapHome.Ct)).ShouldBe("Part A Part B");
    }

    [Fact]
    public async Task TextContent_is_null_when_nothing_matches()
    {
        var rows = (await ResultsPageAsync()).Locator(CapHome.GridRows);
        // data row 3 (index 5) has no anchor in td:nth-child(3)
        (await rows.Nth(5).Locator("td:nth-child(3) a").TextContentAsync(CapHome.Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task GetAttribute_returns_raw_value_or_null()
    {
        var rows = (await ResultsPageAsync()).Locator(CapHome.GridRows);
        var link = rows.Nth(3).Locator("td:nth-child(3) a");

        (await link.GetAttributeAsync("href", CapHome.Ct)).ShouldBe("/LJCMG/Cap/CapDetail.aspx?id=1");
        (await link.GetAttributeAsync("data-nope", CapHome.Ct)).ShouldBeNull();               // attribute absent
        (await rows.Nth(5).Locator("td:nth-child(3) a").GetAttributeAsync("href", CapHome.Ct)).ShouldBeNull(); // element absent
    }

    [Fact]
    public async Task InnerText_and_innerHtml_read_the_first_match_else_empty()
    {
        var rows = (await ResultsPageAsync()).Locator(CapHome.GridRows);
        var typeCell = rows.Nth(3).Locator("td:nth-child(4)");

        (await typeCell.InnerTextAsync(CapHome.Ct)).ShouldBe("Enforcement");
        (await typeCell.InnerHTMLAsync(CapHome.Ct)).ShouldBe("Enforcement");

        var missing = rows.Nth(3).Locator("td.absent");
        (await missing.InnerTextAsync(CapHome.Ct)).ShouldBe(string.Empty);
        (await missing.InnerHTMLAsync(CapHome.Ct)).ShouldBe(string.Empty);
    }

    [Fact]
    public async Task GetByTitle_matches_the_title_attribute()
    {
        var page = await Runner.FakePageAsync();
        (await page.GetByTitle("Search records").CountAsync(CapHome.Ct)).ShouldBe(1);
        (await page.GetByTitle("No such title").CountAsync(CapHome.Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Filter_keeps_elements_whose_text_matches_the_regex()
    {
        var rows = (await ResultsPageAsync()).Locator(CapHome.GridRows);
        // exactly one data row has status "Void"
        (await rows.Filter("Void").CountAsync(CapHome.Ct)).ShouldBe(1);
        (await rows.Filter("nothing-matches-this").CountAsync(CapHome.Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Nth_and_First_narrow_the_set()
    {
        var rows = (await ResultsPageAsync()).Locator(CapHome.GridRows);
        (await rows.First.CountAsync(CapHome.Ct)).ShouldBe(1);
        (await rows.Nth(3).CountAsync(CapHome.Ct)).ShouldBe(1);
        (await rows.Nth(100).CountAsync(CapHome.Ct)).ShouldBe(0); // out of range ⇒ empty
    }

    [Fact]
    public async Task Child_locator_unions_and_dedupes_matches()
    {
        var page = await ResultsPageAsync();
        // The div and the table it contains both match "tr" on the same 15 nodes ⇒ deduped to 15, not 30.
        var both = page
            .Locator("#ctl00_PlaceHolderMain_dgvPermitList, #ctl00_PlaceHolderMain_dgvPermitList_gdvPermitList")
            .Locator("tr");
        (await both.CountAsync(CapHome.Ct)).ShouldBe(15);
    }

    [Fact]
    public async Task RunAndWaitForRequest_succeeds_when_the_trigger_emits_a_matching_request()
    {
        var page = await Runner.FakePageAsync();
        await page.RunAndWaitForRequestAsync(
            () => page.Locator(CapHome.SearchButton).ClickAsync(null, CapHome.Ct),
            CapHome.RequestPrefix, "POST", null, CapHome.Ct);
        page.CurrentStateName.ShouldBe("results");
    }

    [Fact]
    public async Task RunAndWaitForRequest_matches_any_method_when_method_is_null()
    {
        var page = await Runner.FakePageAsync();
        await page.RunAndWaitForRequestAsync(
            () => page.Locator(CapHome.SearchButton).ClickAsync(null, CapHome.Ct),
            CapHome.RequestPrefix, null, null, CapHome.Ct);
        page.CurrentStateName.ShouldBe("results");
    }

    [Fact]
    public async Task RunAndWaitForRequest_times_out_when_no_request_matches()
    {
        // No request emitted at all.
        var idle = await Runner.FakePageAsync();
        await Should.ThrowAsync<BrowserTimeoutException>(async () =>
            await idle.RunAndWaitForRequestAsync(() => Task.CompletedTask, "https://other.example/", "POST", null, CapHome.Ct));

        // A request IS emitted, but its URL does not match the awaited prefix (method null ⇒ '*' in the message).
        var wrongPrefix = await Runner.FakePageAsync();
        await Should.ThrowAsync<BrowserTimeoutException>(async () =>
            await wrongPrefix.RunAndWaitForRequestAsync(
                () => wrongPrefix.Locator(CapHome.SearchButton).ClickAsync(null, CapHome.Ct), "https://different/", null, null, CapHome.Ct));

        // A request IS emitted with a matching prefix, but the method differs.
        var wrongMethod = await Runner.FakePageAsync();
        await Should.ThrowAsync<BrowserTimeoutException>(async () =>
            await wrongMethod.RunAndWaitForRequestAsync(
                () => wrongMethod.Locator(CapHome.SearchButton).ClickAsync(null, CapHome.Ct), CapHome.RequestPrefix, "GET", null, CapHome.Ct));
    }

    [Fact]
    public async Task Clicks_that_match_no_transition_are_no_ops()
    {
        var page = await Runner.FakePageAsync();

        await page.Locator("#does-not-exist").ClickAsync(null, CapHome.Ct);     // null element
        page.CurrentStateName.ShouldBe("form");

        await page.Locator(CapHome.StartDate).ClickAsync(null, CapHome.Ct);             // from=form but not the trigger element
        page.CurrentStateName.ShouldBe("form");

        await page.Locator(CapHome.SearchButton).ClickAsync(null, CapHome.Ct);         // real transition
        page.CurrentStateName.ShouldBe("results");

        await page.Locator("td").First.ClickAsync(null, CapHome.Ct);           // no transition applies in 'results'
        page.CurrentStateName.ShouldBe("results");
    }

    [Fact]
    public async Task WaitFor_hidden_succeeds_for_absent_or_display_none_and_times_out_when_visible()
    {
        var page = await Runner.FakePageAsync();
        await page.WaitForLoadStateAsync("networkidle", null, CapHome.Ct); // no-op success

        await Should.NotThrowAsync(async () => await page.Locator(CapHome.Overlay).WaitForAsync("hidden", null, CapHome.Ct));      // display:none
        await Should.NotThrowAsync(async () => await page.Locator("#absent").WaitForAsync("hidden", null, CapHome.Ct));    // absent
        await Should.NotThrowAsync(async () => await page.Locator(CapHome.SearchButton).WaitForAsync("visible", 500, CapHome.Ct)); // other state ⇒ no-op

        await Should.ThrowAsync<BrowserTimeoutException>(async () =>
            await page.Locator(CapHome.SearchButton).WaitForAsync("hidden", null, CapHome.Ct)); // visible ⇒ timeout
    }

    [Fact]
    public async Task Connect_requires_a_fixture_option_and_an_existing_fixture()
    {
        var backend = new FakeBrowserBackend(Runner.FixturesRoot);

        await Should.ThrowAsync<FakeBackendException>(async () =>
            await backend.ConnectAsync(new BackendBinding("fake"), CapHome.Ct)); // no Options ⇒ no fixture

        await Should.ThrowAsync<FakeBackendException>(async () =>
            await backend.ConnectAsync(Runner.FakeBinding("no-such-fixture"), CapHome.Ct)); // fixture dir missing
    }

    [Fact]
    public async Task Session_disposes_cleanly()
    {
        var backend = new FakeBrowserBackend(Runner.FixturesRoot);
        var session = await backend.ConnectAsync(Runner.FakeBinding("caphome-search"), CapHome.Ct);
        await Should.NotThrowAsync(async () => await session.DisposeAsync());
    }
}
