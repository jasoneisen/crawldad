using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Api.Features.Runs;
using Crawldad.Tests.Support;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>Drives the full <c>ScrapeEnforcementRecord</c> payload through <c>POST /runs</c> against real headless
/// Chromium (the <c>"local"</c> adapter) and asserts byte-identical results to <see cref="ScrapeRecordAcceptanceTests"/>'s
/// goldens — proving <c>fake ≡ real</c> with only <c>backend.adapter</c> changed. Zero live third-party traffic.</summary>
[Collection(RealChromiumParityCollection.Name)]
public class RealChromiumScrapeParityTests(ParityAppFixture fixture)
{
    private IAlbaHost Host => fixture.Host;

    private static string Link(string cap) =>
        $"https://aca-prod.accela.com/LJCMG/Cap/CapDetail.aspx?Module=Enforcement&capID={cap}&agencyCode=LJCMG";

    // N = 10 golden records: each equals its RecordScrapedV1-shaped golden field-for-field through REAL Chromium.
    [Theory]
    [InlineData("record-01-full-suburban", "24ENF-00001", "2025-06-15")]
    [InlineData("record-02-four-line-owner", "24ENF-00002", "2025-05-20")]
    [InlineData("record-03-five-line-harbor", "24ENF-00003", "2025-05-25")]
    [InlineData("record-04-no-owners", "24ENF-00004", "2025-04-01")]
    [InlineData("record-05-many-owners", "24ENF-00005", "2025-06-01")]
    [InlineData("record-06-multipage-attach", "24ENF-00006", "2025-05-05")]
    [InlineData("record-07-related-tree", "24ENF-00007", "2025-03-01")]
    [InlineData("record-08-attach-cap", "24ENF-00008", "2025-02-10")]
    [InlineData("record-11-empty-regions", "24ENF-00011", null)]
    [InlineData("record-12-owner-empty-block", "24ENF-00012", "2025-07-10")]
    public async Task Scrape_record_output_equals_golden(string fixtureDir, string cap, string? publishDate)
    {
        var run = await RunScrapeAsync(fixtureDir, cap, publishDate);
        run.Root.GetProperty("status").GetString().ShouldBe("succeeded");
        AssertMatchesGolden(fixtureDir, run.Root.GetProperty("result"));
    }

    [Fact]
    public async Task Many_owners_emits_the_multiple_owners_warning()
    {
        var run = await RunScrapeAsync("record-05-many-owners", "24ENF-00005", "2025-06-01");
        run.Logs.ShouldContain(("warning", $"MULTIPLE OWNERS: {Link("24ENF-00005")}"));
        run.Root.GetProperty("result").GetProperty("owners").GetArrayLength().ShouldBe(3);
    }

    [Fact]
    public async Task Related_tree_resolves_multi_level_parents_and_logs_the_malformed_rows()
    {
        var run = await RunScrapeAsync("record-07-related-tree", "24ENF-00007", "2025-03-01");
        var result = run.Root.GetProperty("result");

        result.GetProperty("parentRecordNumber").GetString().ShouldBe("REC-A");
        var related = result.GetProperty("relatedRecords");
        related[3].GetProperty("recordNumber").GetString().ShouldBe("REC-D");
        related[3].GetProperty("parentRecordNumber").GetString().ShouldBe("REC-C");

        run.Logs.ShouldBe([
            ("error", $"Could not determine indentation of related record: {Link("24ENF-00007")}"),
            ("error", $"Could not determine class of related record: {Link("24ENF-00007")}"),
        ]);
    }

    [Fact]
    public async Task Attachment_cap_emits_a_warning_and_still_returns_a_complete_record()
    {
        var run = await RunScrapeAsync("record-08-attach-cap", "24ENF-00008", "2025-02-10");

        run.Root.GetProperty("status").GetString().ShouldBe("succeeded");
        AssertMatchesGolden("record-08-attach-cap", run.Root.GetProperty("result"));
        run.Logs.ShouldContain(("warning", $"Attachment pagination hit safety cap (50 pages) for {Link("24ENF-00008")}"));
        run.Root.GetProperty("result").GetProperty("attachments").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Guard_redirect_is_terminal_and_not_retried()
    {
        var run = await RunScrapeAsync("record-09-guard-redirect", "24ENF-00009", null);

        run.Root.GetProperty("status").GetString().ShouldBe("failed");
        var failure = run.Root.GetProperty("failure");
        failure.GetProperty("class").GetString().ShouldBe("terminal");
        failure.GetProperty("code").GetString().ShouldBe("record_not_accessible");
        run.EventTypes.ShouldBe([typeof(RunStarted), typeof(RunFailed)]);
    }

    [Fact]
    public async Task Unknown_owner_heading_is_terminal_and_not_retried()
    {
        var run = await RunScrapeAsync("record-10-unknown-heading", "24ENF-00010", "2025-01-15");

        run.Root.GetProperty("status").GetString().ShouldBe("failed");
        var failure = run.Root.GetProperty("failure");
        failure.GetProperty("class").GetString().ShouldBe("terminal");
        failure.GetProperty("code").GetString().ShouldBe("unknown_heading");
        failure.GetProperty("message").GetString().ShouldBe($"UNKNOWN HEADING: Contact Information AT {Link("24ENF-00010")}");
        run.EventTypes.ShouldBe([typeof(RunStarted), typeof(RunFailed)]);
    }

    // ----- helpers -----------------------------------------------------------

    private sealed record ScrapeRun(
        JsonElement Root, Guid RunId, IReadOnlyList<(string Level, string Message)> Logs, IReadOnlyList<Type> EventTypes);

    private async Task<ScrapeRun> RunScrapeAsync(string fixtureDir, string cap, string? publishDate)
    {
        var payload = await File.ReadAllTextAsync(Path.Combine(Runner.FixturesRoot, "Payloads", "scrape-full.json"));
        var inputs = new JsonObject
        {
            ["backend"] = new JsonObject
            {
                ["adapter"] = "local",
                ["options"] = new JsonObject { ["fixture"] = fixtureDir },
            },
            ["link"] = Link(cap),
            ["attachmentStore"] = new JsonObject { ["kind"] = "fake", ["name"] = "attachmentStore" },
        };
        if (publishDate is not null)
        {
            inputs["publishDate"] = publishDate;
        }

        var body = new JsonObject { ["payload"] = JsonNode.Parse(payload), ["inputs"] = inputs };

        // Drive the synchronous run, tolerating the sync-cap auto-upgrade (defense-in-depth): the terminal result and the
        // persisted event stream are byte-identical whether the scrape finished inline (200) or after the upgrade (202),
        // so the golden equality and the Logs/EventTypes assertions built from the stream below hold either way.
        var root = await DurableHost.PostRunToTerminalAsync(Host, body);

        var runId = root.GetProperty("runId").GetGuid();
        var store = Host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(TestTenants.PrimaryId);
        var events = await session.Events.FetchStreamAsync(runId);
        var logs = events.Select(e => e.Data).OfType<LogEmitted>().Select(l => (l.Level, l.Message)).ToList();
        var eventTypes = events.Select(e => e.EventType).ToList();

        return new ScrapeRun(root, runId, logs, eventTypes);
    }

    private static void AssertMatchesGolden(string fixtureDir, JsonElement result)
    {
        using var golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(Runner.FixturesRoot, fixtureDir, "golden.json")));
        JsonAssert.DeepEquals(result, golden.RootElement).ShouldBeTrue();
        JsonAssert.Canonical(result).ShouldBe(JsonAssert.Canonical(golden.RootElement));
    }
}
