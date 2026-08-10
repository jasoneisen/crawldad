using System.Text.Json;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Runs.Interpreter;
using Crawldad.Web.Infrastructure.Browser.Fake;

namespace Crawldad.Tests.Unit;

/// <summary>The structured-<c>Sel</c> role/text/xpath variants on the record/replay fake: <c>GetByRole</c> (accessible-name),
/// <c>GetByText</c> (innermost, normalised substring), and the <c>xpath=</c> engine — driven at the seam and through
/// <see cref="SelResolver"/>. The fake models the same semantics as Playwright, so <see cref="Integration.RealChromiumSelectorParityTests"/> can assert <c>fake ≡ real</c>.</summary>
public class SelectorVariantTests
{
    private static readonly CancellationToken _ct = CancellationToken.None;

    private static Task<FakePageHandle> PageAsync() => Runner.FakePageAsync("selector-variants");

    private static async Task<RunScope> ScopeAsync()
    {
        var page = await PageAsync();
        var scope = new RunScope(new Dictionary<string, object?>(StringComparer.Ordinal));
        scope.Bind(page);
        return scope;
    }

    private static JsonElement Json(string source)
    {
        using var doc = JsonDocument.Parse(source);
        return doc.RootElement.Clone();
    }

    private static Dictionary<string, object?> Map(params (string Key, object? Value)[] entries)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            map[key] = value;
        }

        return map;
    }

    // ----- GetByRole (seam) --------------------------------------------------

    [Fact]
    public async Task GetByRole_matches_the_roles_implicit_element_set()
    {
        var page = await PageAsync();

        (await page.GetByRole("button", null).CountAsync(_ct)).ShouldBe(2);   // #search + #reset
        (await page.GetByRole("link", null).CountAsync(_ct)).ShouldBe(2);     // #openRecord + #help
        (await page.GetByRole("heading", null).CountAsync(_ct)).ShouldBe(1);  // #hdr
        (await page.GetByRole("textbox", null).CountAsync(_ct)).ShouldBe(1);  // #startDate
        (await page.GetByRole("listitem", null).CountAsync(_ct)).ShouldBe(3); // the three <li>
    }

    [Fact]
    public async Task GetByRole_name_matches_text_or_aria_label_case_insensitively()
    {
        var page = await PageAsync();

        // accessible name from text content
        (await page.GetByRole("button", "Search Records").CountAsync(_ct)).ShouldBe(1);
        (await page.GetByRole("link", "Open Record").TextContentAsync(_ct)).ShouldBe("Open Record");
        (await page.GetByRole("heading", "enforcement").CountAsync(_ct)).ShouldBe(1); // substring, case-insensitive
        // accessible name from aria-label
        (await page.GetByRole("button", "Reset the form").CountAsync(_ct)).ShouldBe(1);
        (await page.GetByRole("textbox", "Start Date").CountAsync(_ct)).ShouldBe(1);
    }

    [Fact]
    public async Task GetByRole_no_match_is_count_zero()
    {
        var page = await PageAsync();
        (await page.GetByRole("button", "Nonexistent").CountAsync(_ct)).ShouldBe(0);
    }

    [Fact]
    public async Task GetByRole_falls_back_to_an_explicit_role_attribute_for_an_unlisted_role()
    {
        // "switch" is not in the implicit-role table, so it falls back to [role=switch] — matching <div role="switch">.
        var page = await PageAsync();
        (await page.GetByRole("switch", null).CountAsync(_ct)).ShouldBe(1);
        (await page.GetByRole("switch", "Toggle").CountAsync(_ct)).ShouldBe(1);
    }

    // ----- GetByText (seam) --------------------------------------------------

    [Fact]
    public async Task GetByText_matches_the_innermost_element_carrying_the_text()
    {
        var page = await PageAsync();

        (await page.GetByText("Open Record").TextContentAsync(_ct)).ShouldBe("Open Record");
        // "Ready to search" lives in the inner <span>; its <div> parent also contains it but is NOT the innermost, so
        // only the span matches (Playwright's "smallest element" rule).
        (await page.GetByText("Ready to search").CountAsync(_ct)).ShouldBe(1);
    }

    [Fact]
    public async Task GetByText_multi_match_counts_every_innermost_carrier()
    {
        // both <p class="note"> contain "notice"
        (await (await PageAsync()).GetByText("notice").CountAsync(_ct)).ShouldBe(2);
    }

    [Fact]
    public async Task GetByText_no_match_is_count_zero() =>
        (await (await PageAsync()).GetByText("no such copy anywhere").CountAsync(_ct)).ShouldBe(0);

    // ----- xpath (seam, via Locator's "xpath=" engine) -----------------------

    [Fact]
    public async Task Locator_xpath_engine_queries_the_document()
    {
        var page = await PageAsync();

        (await page.Locator("xpath=//button").CountAsync(_ct)).ShouldBe(2);
        (await page.Locator("xpath=//a[@id='openRecord']").TextContentAsync(_ct)).ShouldBe("Open Record");
        (await page.Locator("xpath=//li").CountAsync(_ct)).ShouldBe(3);
        (await page.Locator("xpath=//table").CountAsync(_ct)).ShouldBe(0);
    }

    // ----- nth (seam) ----------------------------------------------------------

    // .Nth is a lazy 0-based narrowing on the fake: in-range narrows to that element, past-the-end and negative both yield
    // no match (0-based only). The real backend diverges on negative (Playwright's Nth(-1) is the last element) — see
    // RealChromiumSelectorParityTests — which is why the interpreter rejects a negative nth before reaching either backend.
    [Fact]
    public async Task Nth_is_a_zero_based_narrowing_yielding_no_match_past_the_end_or_negative()
    {
        var li = (await PageAsync()).Locator("li"); // the three <li>: Alpha / Bravo / Charlie

        (await li.Nth(0).TextContentAsync(_ct)).ShouldBe("Alpha item");
        (await li.Nth(2).TextContentAsync(_ct)).ShouldBe("Charlie item");
        (await li.Nth(3).CountAsync(_ct)).ShouldBe(0);   // past the end → no match
        (await li.Nth(-1).CountAsync(_ct)).ShouldBe(0);  // fake models 0-based only → no match (real diverges: the last)
    }

    // ----- click / fill (seam) -----------------------------------------------

    [Fact]
    public async Task Click_on_a_role_selected_link_drives_the_transition()
    {
        var page = await PageAsync();
        page.CurrentStateName.ShouldBe("page");

        await page.GetByRole("link", "Open Record").ClickAsync(null, _ct); // resolves to #openRecord

        page.CurrentStateName.ShouldBe("record");
    }

    [Fact]
    public async Task Fill_on_a_role_selected_textbox_sets_the_value()
    {
        var page = await PageAsync();

        await page.GetByRole("textbox", "Start Date").FillAsync("01/02/2026", _ct);

        (await page.Locator("#startDate").GetAttributeAsync("value", _ct)).ShouldBe("01/02/2026");
    }

    // ----- resolver: structured Sel map --------------------------------------

    [Fact]
    public async Task ResolveMap_roots_at_role_text_and_xpath()
    {
        var scope = await ScopeAsync();

        (await scope.Sel.ResolveMap(Map(("role", "button"), ("name", "Search Records"))).CountAsync(_ct)).ShouldBe(1);
        (await scope.Sel.ResolveMap(Map(("role", "listitem"))).CountAsync(_ct)).ShouldBe(3); // role, no name
        (await scope.Sel.ResolveMap(Map(("text", "notice"))).CountAsync(_ct)).ShouldBe(2);
        (await scope.Sel.ResolveMap(Map(("xpath", "//a[@id='openRecord']"))).TextContentAsync(_ct)).ShouldBe("Open Record");
    }

    [Fact]
    public async Task ResolveMap_refinements_narrow_a_variant_root()
    {
        var scope = await ScopeAsync();

        (await scope.Sel.ResolveMap(Map(("role", "button"), ("first", true))).CountAsync(_ct)).ShouldBe(1);
        (await scope.Sel.ResolveMap(Map(("xpath", "//button"), ("nth", 1L))).CountAsync(_ct)).ShouldBe(1);
    }

    // ----- resolver: node-JSON selectors (each field evaluated) --------------

    [Fact]
    public async Task ResolveNode_object_evaluates_role_text_and_xpath_fields()
    {
        var scope = await ScopeAsync();

        (await (await scope.Sel.ResolveNodeAsync(Json("""{ "role": "button", "name": "Search Records" }"""), null, _ct)).CountAsync(_ct)).ShouldBe(1);
        (await (await scope.Sel.ResolveNodeAsync(Json("""{ "text": "notice" }"""), null, _ct)).CountAsync(_ct)).ShouldBe(2);
        (await (await scope.Sel.ResolveNodeAsync(Json("""{ "xpath": "//li" }"""), null, _ct)).CountAsync(_ct)).ShouldBe(3);
    }

    [Fact]
    public async Task ResolveNode_interpolates_the_name_field()
    {
        // name (like css/text/xpath/title) is a template — an ${input.…} reference renders before resolution. role is a
        // fixed ARIA vocabulary (schema enum), so it stays literal.
        var scope = new RunScope(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["label"] = "Search Records",
        });
        scope.Bind(await PageAsync());

        var handle = await scope.Sel.ResolveNodeAsync(Json("""{ "role": "button", "name": "${input.label}" }"""), null, _ct);
        (await handle.CountAsync(_ct)).ShouldBe(1);
    }

    [Fact]
    public async Task ResolveNode_string_selector_supports_the_xpath_engine()
    {
        var scope = await ScopeAsync();
        // a bare-string node selector rendered to "xpath=…" resolves through the same engine as the structured form.
        (await (await scope.Sel.ResolveNodeAsync(Json("\"xpath=//button\""), null, _ct)).CountAsync(_ct)).ShouldBe(2);
    }
}
