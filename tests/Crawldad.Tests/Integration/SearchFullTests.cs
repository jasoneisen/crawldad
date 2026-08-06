using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Tests.Support;

namespace Crawldad.Tests.Integration;

/// <summary>
/// The Phase 2 WP4 multi-page gate: drives <c>POST /runs</c> with the FULL Appendix B.1
/// <c>SearchEnforcementRecords</c> payload over a 3-page fixture and asserts the shaped <c>result</c>
/// (<c>newLinks</c>/<c>crawledToEnd</c>/<c>pages</c>) is <b>byte-identical</b> to a golden hand-derived by executing
/// <c>HistoricalCrawler.goToNextPageCallback</c> (:85-104) + the <c>LJCMGClient</c> do/while (:121-167). The four cases
/// pin the tension-#1 early-termination nuances: the <c>hitKnown ? crawledToEnd : !hasMorePages</c> break exactly
/// negates the callback's <c>hitKnown ? !crawledToEnd : hasMorePages</c> return.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class SearchFullTests(AppFixture fixture)
{
    private const string _knownMidPage2 = "https://aca-prod.accela.com/LJCMG/Cap/CapDetail.aspx?id=p2-3";

    private IAlbaHost Host => fixture.Host;

    // (a) knownUrls=[], priorCrawlComplete=false → every row, pages 1-3 in order; crawledToEnd flips true on the last
    // page (:87 !HasMorePages). 3 pages visited ⇒ goto + search + 2 pagination = 4 requests.
    [Fact]
    public async Task Full_crawl_collects_all_rows_in_order_and_reaches_the_end()
    {
        var (result, stats) = await RunSearchAsync("caphome-multipage", knownUrls: [], priorCrawlComplete: false);

        AssertMatchesGolden("caphome-multipage/golden-a-full", result);
        result.GetProperty("crawledToEnd").GetBoolean().ShouldBeTrue();
        NewLinkIds(result).ShouldBe(["p1-1", "p1-2", "p1-3", "p1-4", "p2-1", "p2-2", "p2-3", "p2-4", "p2-5", "p3-1", "p3-2", "p3-3", "p3-4"]);
        result.GetProperty("pages").GetArrayLength().ShouldBe(3);
        stats.GetProperty("requests").GetInt32().ShouldBe(4);
    }

    // (b) known mid-page-2 + priorCrawlComplete=true → hitKnown, crawledToEnd already true ⇒ break (return !crawledToEnd
    // = false, :95). page-1 urls + page-2 urls BEFORE the known one; the known url is NOT added and the rest of page 2
    // is skipped; page 3 is NEVER visited (pages length 2, only goto + search + 1 pagination = 3 requests).
    [Fact]
    public async Task Early_stop_when_prior_crawl_complete_does_not_visit_page_three()
    {
        var (result, stats) = await RunSearchAsync("caphome-multipage", knownUrls: [_knownMidPage2], priorCrawlComplete: true);

        AssertMatchesGolden("caphome-multipage/golden-b-early-stop", result);
        result.GetProperty("crawledToEnd").GetBoolean().ShouldBeTrue();
        NewLinkIds(result).ShouldBe(["p1-1", "p1-2", "p1-3", "p1-4", "p2-1", "p2-2"]);
        NewLinkIds(result).ShouldNotContain("p2-3"); // the known url itself is never added
        result.GetProperty("pages").GetArrayLength().ShouldBe(2); // page 3 never visited
        stats.GetProperty("requests").GetInt32().ShouldBe(3);
    }

    // (c) THE !crawledToEnd nuance: same known url, priorCrawlComplete=FALSE → hitKnown but crawledToEnd false ⇒ CONTINUE
    // (return !crawledToEnd = true). The inner scan still breaks at the known url, so the REST of page 2 (p2-4/p2-5) is
    // skipped, yet the crawl advances THROUGH page 3; crawledToEnd flips true on the last page. All 3 pages visited.
    [Fact]
    public async Task Continue_past_known_url_when_prior_crawl_incomplete_then_reaches_the_end()
    {
        var (result, stats) = await RunSearchAsync("caphome-multipage", knownUrls: [_knownMidPage2], priorCrawlComplete: false);

        AssertMatchesGolden("caphome-multipage/golden-c-continue", result);
        result.GetProperty("crawledToEnd").GetBoolean().ShouldBeTrue();
        // page 1 (all) + page 2 BEFORE the known url (rest of page 2 skipped) + page 3 (all) — the known url absent.
        NewLinkIds(result).ShouldBe(["p1-1", "p1-2", "p1-3", "p1-4", "p2-1", "p2-2", "p3-1", "p3-2", "p3-3", "p3-4"]);
        result.GetProperty("pages").GetArrayLength().ShouldBe(3);
        stats.GetProperty("requests").GetInt32().ShouldBe(4);
    }

    // (d) empty page: Results.Count==0 ⇒ return false (:89), but crawledToEnd was already set true (:87, last page). So
    // newLinks empty, crawledToEnd TRUE, pages empty (the payload breaks before pushing the empty page). goto + search = 2.
    [Fact]
    public async Task Empty_results_stop_with_crawled_to_end_true_and_no_pages()
    {
        var (result, stats) = await RunSearchAsync("caphome-empty", knownUrls: [], priorCrawlComplete: false);

        AssertMatchesGolden("caphome-empty/golden", result);
        result.GetProperty("crawledToEnd").GetBoolean().ShouldBeTrue();
        result.GetProperty("newLinks").GetArrayLength().ShouldBe(0);
        result.GetProperty("pages").GetArrayLength().ShouldBe(0);
        stats.GetProperty("requests").GetInt32().ShouldBe(2);
        stats.GetProperty("downloads").GetInt32().ShouldBe(0);
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

    private static List<string> NewLinkIds(JsonElement result) =>
        [.. result.GetProperty("newLinks").EnumerateArray().Select(u => u.GetString()!.Split("id=")[1])];

    private static void AssertMatchesGolden(string relativePath, JsonElement result)
    {
        using var golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(Runner.FixturesRoot, $"{relativePath}.json")));
        JsonAssert.DeepEquals(result, golden.RootElement).ShouldBeTrue();                 // structural
        JsonAssert.Canonical(result).ShouldBe(JsonAssert.Canonical(golden.RootElement));  // byte compare
    }
}
