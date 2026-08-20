namespace Crawldad.Tests.Integration;

/// <summary>Zero-live-traffic wiring proof for the live canary: runs the canary's identical code path — read
/// <c>scrape-full.json</c>, <c>POST /runs</c>, assert success, validate <c>RecordScrapedV1</c> shape — against the
/// in-process fixture site instead of live Chromium. Only difference from <see cref="LiveCanaryTests"/> is the origin.</summary>
[Collection(RealChromiumParityCollection.Name)]
public sealed class CanaryWiringTests(ParityAppFixture fixture)
{
    private static string Link(string cap) =>
        $"https://aca-prod.accela.com/LJCMG/Cap/CapDetail.aspx?Module=Enforcement&capID={cap}&agencyCode=LJCMG";

    [Fact]
    public async Task Canary_shape_gate_accepts_a_known_record_through_the_local_adapter()
    {
        var link = Link("24ENF-00001");
        var root = await LiveCanary.RunScrapeAsync(
            fixture.Host, LiveCanary.Backend("local", fixture: "record-01-full-suburban"), link, "2025-06-15");

        root.GetProperty("status").GetString().ShouldBe("succeeded");
        LiveCanary.AssertValidRecordScrapedV1(root.GetProperty("result"), link);
    }
}
