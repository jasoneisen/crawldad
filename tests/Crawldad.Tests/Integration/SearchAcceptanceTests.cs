using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Tests.Support;

namespace Crawldad.Tests.Integration;

/// <summary>The search-side acceptance gate: an enumerable golden corpus of M = 6 distinct searches driven through
/// <c>POST /runs</c> with the full <c>SearchEnforcementRecords</c> payload (<c>search-full.json</c>) against the
/// record/replay fake, each asserted byte-identical to a golden hand-derived from the C# reference. No Chromium, no live traffic.</summary>
[Collection(IntegrationCollection.Name)]
public class SearchAcceptanceTests(AppFixture fixture)
{
    // Page 2, row 3 of caphome-multipage — a mid-page known url. Cases (c)/(d) share it and differ ONLY in
    // priorCrawlComplete, isolating the callback's `return !crawledToEnd` branch.
    private const string _knownMidPage2 = "https://aca-prod.accela.com/LJCMG/Cap/CapDetail.aspx?id=p2-3";
    private const string _sharedDedupUrl = "https://aca-prod.accela.com/LJCMG/Cap/CapDetail.aspx?id=shared";

    private IAlbaHost Host => fixture.Host;

    // M = 6 golden searches: each equals its {newLinks, crawledToEnd, pages} golden byte-for-byte (list-order included),
    // and reports the exact request count (goto + one waitForRequest per search/pagination postback).
    [Theory]
    // (a) single-page results, NO pagination link — reuses caphome-search's rich-edge grid through the full payload.
    [InlineData("caphome-search", "golden-full", null, false, 2)]
    // (b) multi-page full crawl — crawledToEnd flips true on the last page.
    [InlineData("caphome-multipage", "golden-a-full", null, false, 4)]
    // (c) known-URL early stop, priorCrawlComplete=true — break at the known url (return !crawledToEnd=false); page 3 unvisited.
    [InlineData("caphome-multipage", "golden-b-early-stop", _knownMidPage2, true, 3)]
    // (d) known-URL, priorCrawlComplete=false — THE !crawledToEnd nuance: continue past the known url through to the end.
    [InlineData("caphome-multipage", "golden-c-continue", _knownMidPage2, false, 4)]
    // (e) empty results — crawledToEnd already true (last page), no pages pushed (break before the push).
    [InlineData("caphome-empty", "golden", null, false, 2)]
    // (f) cross-page duplicate url — distinct(newLinks) collapses the repeat while pages keeps both raw rows.
    [InlineData("caphome-dedup", "golden", null, false, 3)]
    public async Task Search_output_equals_golden(string fixtureDir, string golden, string? knownLink, bool priorCrawlComplete, int expectedRequests)
    {
        var knownUrls = knownLink is null ? Array.Empty<string>() : [knownLink];
        var (result, stats) = await RunSearchAsync(fixtureDir, knownUrls, priorCrawlComplete);

        AssertMatchesGolden($"{fixtureDir}/{golden}", result);
        stats.GetProperty("requests").GetInt32().ShouldBe(expectedRequests);
    }

    // (a) The single-page grid's row-3 has no <a>: attr(...,'href') null-propagates, coalesce(...,'') supplies '', and
    // the naive concat yields scheme://host only — the whole per-cell edge set flows through the full result shape.
    [Fact]
    public async Task Single_page_missing_anchor_row_resolves_to_scheme_host_only()
    {
        var (result, _) = await RunSearchAsync("caphome-search", knownUrls: [], priorCrawlComplete: false);

        result.GetProperty("crawledToEnd").GetBoolean().ShouldBeTrue(); // no pagination anchor ⇒ last page
        result.GetProperty("pages").GetArrayLength().ShouldBe(1);
        var newLinks = NewLinkUrls(result);
        newLinks.Count.ShouldBe(10);
        newLinks.ShouldContain("https://aca-prod.accela.com"); // row 3: missing href → scheme://host, no path
    }

    // (c) knownUrls=[_knownMidPage2] + priorCrawlComplete=TRUE ⇒ hitKnown with crawledToEnd already true ⇒ break
    // (return !crawledToEnd = false). page-1 urls + page-2 urls BEFORE the known one; the known url is NOT added,
    // the rest of page 2 is skipped, and page 3 is NEVER visited.
    [Fact]
    public async Task Early_stop_when_prior_crawl_complete_does_not_visit_page_three()
    {
        var (result, stats) = await RunSearchAsync("caphome-multipage", knownUrls: [_knownMidPage2], priorCrawlComplete: true);

        NewLinkIds(result).ShouldBe(["p1-1", "p1-2", "p1-3", "p1-4", "p2-1", "p2-2"]);
        NewLinkIds(result).ShouldNotContain("p2-3"); // the known url itself is never added
        result.GetProperty("pages").GetArrayLength().ShouldBe(2); // page 3 never visited
        stats.GetProperty("requests").GetInt32().ShouldBe(3);
    }

    // (d) THE !crawledToEnd nuance: the SAME known url, priorCrawlComplete=FALSE ⇒ hitKnown but crawledToEnd false ⇒
    // CONTINUE (return !crawledToEnd = true). The inner scan still breaks at the known url, so the REST of page 2
    // (p2-4/p2-5) is skipped, yet the crawl advances THROUGH page 3; crawledToEnd flips true on the last page.
    [Fact]
    public async Task Continue_past_known_url_when_prior_crawl_incomplete_then_reaches_the_end()
    {
        var (result, stats) = await RunSearchAsync("caphome-multipage", knownUrls: [_knownMidPage2], priorCrawlComplete: false);

        NewLinkIds(result).ShouldBe(["p1-1", "p1-2", "p1-3", "p1-4", "p2-1", "p2-2", "p3-1", "p3-2", "p3-3", "p3-4"]);
        result.GetProperty("crawledToEnd").GetBoolean().ShouldBeTrue();
        result.GetProperty("pages").GetArrayLength().ShouldBe(3);
        stats.GetProperty("requests").GetInt32().ShouldBe(4);
    }

    // (e) empty page: Results.Count==0 ⇒ return false, but crawledToEnd was already set true (last page). So
    // newLinks empty, crawledToEnd TRUE, pages empty (the payload breaks before pushing the empty page). No downloads.
    [Fact]
    public async Task Empty_results_stop_with_crawled_to_end_true_and_no_pages()
    {
        var (result, stats) = await RunSearchAsync("caphome-empty", knownUrls: [], priorCrawlComplete: false);

        result.GetProperty("crawledToEnd").GetBoolean().ShouldBeTrue();
        result.GetProperty("newLinks").GetArrayLength().ShouldBe(0);
        result.GetProperty("pages").GetArrayLength().ShouldBe(0);
        stats.GetProperty("downloads").GetInt32().ShouldBe(0);
    }

    // (f) The shared record appears on BOTH pages: distinct(newLinks) keeps it ONCE,
    // but pages is NOT de-duplicated — the shared row is present in both per-page arrays. This is the de-dup collapse the
    // "no duplicate url" goldens (caphome-search/-multipage) cannot show, where distinct(...) is the identity.
    [Fact]
    public async Task Cross_page_duplicate_url_is_deduped_in_newLinks_but_kept_in_pages()
    {
        var (result, _) = await RunSearchAsync("caphome-dedup", knownUrls: [], priorCrawlComplete: false);

        NewLinkUrls(result).Count(u => string.Equals(u, _sharedDedupUrl, StringComparison.Ordinal)).ShouldBe(1); // deduped in newLinks
        NewLinkIds(result).ShouldBe(["d1-1", "d1-2", "shared", "d2-1"]);

        var pages = result.GetProperty("pages");
        pages.GetArrayLength().ShouldBe(2);
        var sharedRowsAcrossPages = pages.EnumerateArray()
            .Sum(page => page.EnumerateArray().Count(row => string.Equals(row.GetProperty("url").GetString(), _sharedDedupUrl, StringComparison.Ordinal)));
        sharedRowsAcrossPages.ShouldBe(2); // kept in BOTH pages' raw arrays
    }

    // ----- helpers -----------------------------------------------------------

    private async Task<(JsonElement Result, JsonElement Stats)> RunSearchAsync(string fixtureDir, string[] knownUrls, bool priorCrawlComplete)
    {
        var payload = await File.ReadAllTextAsync(Path.Combine(Runner.FixturesRoot, "Payloads", "search-full.json"));
        var body = new JsonObject
        {
            ["payload"] = JsonNode.Parse(payload),
            ["inputs"] = new JsonObject
            {
                ["backend"] = new JsonObject
                {
                    ["adapter"] = "fake",
                    ["options"] = new JsonObject { ["fixture"] = fixtureDir },
                },
                ["startDate"] = "01/01/2024",
                ["endDate"] = "01/31/2024",
                ["knownUrls"] = new JsonArray([.. knownUrls.Select(u => (JsonNode)u!)]),
                ["priorCrawlComplete"] = priorCrawlComplete,
            },
        };

        var scenario = await Host.Scenario(x =>
        {
            x.Post.Json(body).ToUrl("/runs");
            x.StatusCodeShouldBeOk();
        });
        var root = await scenario.ReadAsJsonAsync<JsonElement>();

        root.GetProperty("status").GetString().ShouldBe("succeeded");
        return (root.GetProperty("result").Clone(), root.GetProperty("stats").Clone());
    }

    private static List<string> NewLinkUrls(JsonElement result) =>
        [.. result.GetProperty("newLinks").EnumerateArray().Select(u => u.GetString()!)];

    private static List<string> NewLinkIds(JsonElement result) =>
        [.. NewLinkUrls(result).Select(u => u.Split("id=")[1])];

    private static void AssertMatchesGolden(string relativePath, JsonElement result)
    {
        using var golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(Runner.FixturesRoot, $"{relativePath}.json")));
        JsonAssert.DeepEquals(result, golden.RootElement).ShouldBeTrue();                 // structural (order-sensitive arrays)
        JsonAssert.Canonical(result).ShouldBe(JsonAssert.Canonical(golden.RootElement));  // byte compare (key order included)
    }
}
