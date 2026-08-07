namespace Crawldad.Tests.Integration;

/// <summary>
/// The <b>zero-live-traffic wiring proof</b> for the Phase 4 live canary: it runs the canary's IDENTICAL code path —
/// read <c>scrape-full.json</c> verbatim → <c>POST /runs</c> on the <c>"local"</c> adapter → assert <c>status:"succeeded"</c>
/// → validate the <c>RecordScrapedV1</c> SHAPE (<see cref="LiveCanary.RunScrapeAsync"/> + <see cref="LiveCanary.AssertValidRecordScrapedV1"/>)
/// — but against the in-process <b>local fixture site</b> over real headless Chromium (the WP2 <see cref="ParityAppFixture"/>,
/// whose <c>"local"</c> adapter is the fixture-backed <see cref="Support.FixtureChromiumBackend"/>). So the canary's
/// payload-driving and shape gate are known-good <b>short of the live hit</b>, with the live run remaining the operator's
/// gated manual/nightly action. The only difference from <see cref="LiveCanaryTests"/> is the origin the adapter talks to.
/// </summary>
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
