using System.Text.Json;
using Xunit.Abstractions;

namespace Crawldad.Tests.Integration;

/// <summary>
/// The Phase 4 <b>live canary</b> (CRAWLDAD_PLAN.md Phase 4, success criterion 3): scrapes ONE real enforcement record
/// from the <b>live Accela portal</b> and asserts the output is a structurally valid <c>RecordScrapedV1</c>. It drives
/// the canonical <c>scrape-full.json</c> verbatim through <c>POST /runs</c> on the real <c>"local"</c> adapter (real
/// headless Chromium, real network), so the §8.1 policy applies for real — the 2 s global throttle, the host/resource
/// blocklist, and the cross-run asset cache — at concurrency 1. This is the nightly/manual drift signal; it is
/// <b>never</b> part of the fast loop.
/// <para>
/// <b>Gated, and NOT run by the fast loop.</b> The single test self-skips unless <c>CRAWLDAD_LIVE_CANARY=1</c> and
/// <c>CRAWLDAD_CANARY_LINK</c> are set (<see cref="LiveCanaryFactAttribute"/>), and it carries the <c>Category=LiveCanary</c>
/// trait so CI filters it out explicitly too. To run it manually (one command):
/// <code>
/// CRAWLDAD_LIVE_CANARY=1 \
/// CRAWLDAD_CANARY_LINK='https://aca-prod.accela.com/LJCMG/Cap/CapDetail.aspx?Module=Enforcement&amp;capID=&lt;REC&gt;&amp;agencyCode=LJCMG' \
/// dotnet test --filter Category=LiveCanary
/// </code>
/// (optionally <c>CRAWLDAD_CANARY_PUBLISH_DATE=YYYY-MM-DD</c> and <c>CRAWLDAD_CANARY_REGION=&lt;tag&gt;</c>). The
/// <see cref="CanaryWiringTests"/> proves this exact code path against the local fixture site with zero live traffic.
/// </para>
/// </summary>
[Trait("Category", LiveCanary.Category)]
public sealed class LiveCanaryTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions _pretty = new() { WriteIndented = true };

    [LiveCanaryFact]
    public async Task Scrapes_one_live_record_into_a_structurally_valid_record()
    {
        var link = Environment.GetEnvironmentVariable(LiveCanary.LinkVar)!;
        var publishDate = Environment.GetEnvironmentVariable(LiveCanary.PublishDateVar);
        var region = Environment.GetEnvironmentVariable(LiveCanary.RegionVar);

        await using var host = await LiveCanary.BuildLiveHostAsync();
        var root = await LiveCanary.RunScrapeAsync(host, LiveCanary.Backend("local", region: region), link, publishDate);

        // Emit the full response so a nightly run captures the scraped record + stats (durationMs/requests/cacheHits/
        // downloads) for the drift log — and, on a terminal failure, the failure payload for diagnosis.
        var response = JsonSerializer.Serialize(root, _pretty);
        output.WriteLine(response);

        root.GetProperty("status").GetString()
            .ShouldBe("succeeded", $"Live canary drift — the run did not succeed. Full response:\n{response}");

        LiveCanary.AssertValidRecordScrapedV1(root.GetProperty("result"), link);
    }
}
