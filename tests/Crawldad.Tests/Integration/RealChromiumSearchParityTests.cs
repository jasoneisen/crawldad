using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Tests.Support;

namespace Crawldad.Tests.Integration;

/// <summary>Drives the full <c>SearchEnforcementRecords</c> payload (<c>search-full.json</c>) through <c>POST /runs</c>
/// against real headless Chromium (the <c>"local"</c> adapter) and asserts the result is byte-identical to the same
/// golden <see cref="SearchAcceptanceTests"/> uses, with only <c>backend.adapter</c> changed. Zero live third-party traffic.</summary>
[Collection(RealChromiumCollection.Name)]
[Trait("Category", RealChromiumCollection.Category)]
public class RealChromiumSearchParityTests(ParityAppFixture fixture)
{
    private const string _knownMidPage2 = "https://aca-prod.accela.com/LJCMG/Cap/CapDetail.aspx?id=p2-3";
    private const string _sharedDedupUrl = "https://aca-prod.accela.com/LJCMG/Cap/CapDetail.aspx?id=shared";

    private IAlbaHost Host => fixture.Host;

    // M = 6 golden searches: each equals its {newLinks, crawledToEnd, pages} golden byte-for-byte through REAL Chromium,
    // and reports the exact request count (goto + one waitForRequest per search/pagination postback).
    [Theory]
    [InlineData("caphome-search", "golden-full", null, false, 2)]
    [InlineData("caphome-multipage", "golden-a-full", null, false, 4)]
    [InlineData("caphome-multipage", "golden-b-early-stop", _knownMidPage2, true, 3)]
    [InlineData("caphome-multipage", "golden-c-continue", _knownMidPage2, false, 4)]
    [InlineData("caphome-empty", "golden", null, false, 2)]
    [InlineData("caphome-dedup", "golden", null, false, 3)]
    public async Task Search_output_equals_golden(string fixtureDir, string golden, string? knownLink, bool priorCrawlComplete, int expectedRequests)
    {
        var knownUrls = knownLink is null ? Array.Empty<string>() : [knownLink];
        var (result, stats) = await RunSearchAsync(fixtureDir, knownUrls, priorCrawlComplete);

        AssertMatchesGolden($"{fixtureDir}/{golden}", result);
        stats.GetProperty("requests").GetInt32().ShouldBe(expectedRequests);
    }

    [Fact]
    public async Task Single_page_missing_anchor_row_resolves_to_scheme_host_only()
    {
        var (result, _) = await RunSearchAsync("caphome-search", knownUrls: [], priorCrawlComplete: false);

        result.GetProperty("crawledToEnd").GetBoolean().ShouldBeTrue();
        result.GetProperty("pages").GetArrayLength().ShouldBe(1);
        var newLinks = NewLinkUrls(result);
        newLinks.Count.ShouldBe(10);
        newLinks.ShouldContain("https://aca-prod.accela.com");
    }

    [Fact]
    public async Task Early_stop_when_prior_crawl_complete_does_not_visit_page_three()
    {
        var (result, stats) = await RunSearchAsync("caphome-multipage", knownUrls: [_knownMidPage2], priorCrawlComplete: true);

        NewLinkIds(result).ShouldBe(["p1-1", "p1-2", "p1-3", "p1-4", "p2-1", "p2-2"]);
        NewLinkIds(result).ShouldNotContain("p2-3");
        result.GetProperty("pages").GetArrayLength().ShouldBe(2);
        stats.GetProperty("requests").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task Continue_past_known_url_when_prior_crawl_incomplete_then_reaches_the_end()
    {
        var (result, stats) = await RunSearchAsync("caphome-multipage", knownUrls: [_knownMidPage2], priorCrawlComplete: false);

        NewLinkIds(result).ShouldBe(["p1-1", "p1-2", "p1-3", "p1-4", "p2-1", "p2-2", "p3-1", "p3-2", "p3-3", "p3-4"]);
        result.GetProperty("crawledToEnd").GetBoolean().ShouldBeTrue();
        result.GetProperty("pages").GetArrayLength().ShouldBe(3);
        stats.GetProperty("requests").GetInt32().ShouldBe(4);
    }

    [Fact]
    public async Task Empty_results_stop_with_crawled_to_end_true_and_no_pages()
    {
        var (result, stats) = await RunSearchAsync("caphome-empty", knownUrls: [], priorCrawlComplete: false);

        result.GetProperty("crawledToEnd").GetBoolean().ShouldBeTrue();
        result.GetProperty("newLinks").GetArrayLength().ShouldBe(0);
        result.GetProperty("pages").GetArrayLength().ShouldBe(0);
        stats.GetProperty("downloads").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Cross_page_duplicate_url_is_deduped_in_newLinks_but_kept_in_pages()
    {
        var (result, _) = await RunSearchAsync("caphome-dedup", knownUrls: [], priorCrawlComplete: false);

        NewLinkUrls(result).Count(u => string.Equals(u, _sharedDedupUrl, StringComparison.Ordinal)).ShouldBe(1);
        NewLinkIds(result).ShouldBe(["d1-1", "d1-2", "shared", "d2-1"]);

        var pages = result.GetProperty("pages");
        pages.GetArrayLength().ShouldBe(2);
        var sharedRowsAcrossPages = pages.EnumerateArray()
            .Sum(page => page.EnumerateArray().Count(row => string.Equals(row.GetProperty("url").GetString(), _sharedDedupUrl, StringComparison.Ordinal)));
        sharedRowsAcrossPages.ShouldBe(2);
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
                    ["adapter"] = "local",
                    ["options"] = new JsonObject { ["fixture"] = fixtureDir },
                },
                ["startDate"] = "01/01/2024",
                ["endDate"] = "01/31/2024",
                ["knownUrls"] = new JsonArray([.. knownUrls.Select(u => (JsonNode)u!)]),
                ["priorCrawlComplete"] = priorCrawlComplete,
            },
        };

        // Drive the synchronous run, tolerating the sync-cap auto-upgrade under full-suite parallel load: a real-Chromium
        // search that outruns the 120 s window returns 202 and is polled to the identical terminal result + stats.
        var root = await DurableHost.PostRunToTerminalAsync(Host, body);

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
        JsonAssert.DeepEquals(result, golden.RootElement).ShouldBeTrue();
        JsonAssert.Canonical(result).ShouldBe(JsonAssert.Canonical(golden.RootElement));
    }
}
