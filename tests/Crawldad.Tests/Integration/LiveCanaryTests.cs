using System.Text.Json;
using Xunit.Abstractions;

namespace Crawldad.Tests.Integration;

/// <summary>The live canary: scrapes ONE real enforcement record from the live Accela portal and asserts the output
/// is a structurally valid <c>RecordScrapedV1</c> — the nightly/manual drift signal, never part of the fast loop.
/// Gated: skipped unless <c>CRAWLDAD_LIVE_CANARY=1</c> and <c>CRAWLDAD_CANARY_LINK</c> are set; see <see cref="LiveCanaryFactAttribute"/> and <see cref="CanaryWiringTests"/> (same path, zero live traffic).</summary>
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
