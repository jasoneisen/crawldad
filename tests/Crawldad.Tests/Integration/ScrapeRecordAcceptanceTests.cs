using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Runs;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>
/// The Phase 3 MVP acceptance gate: for a corpus of golden enforcement records, drives the FULL
/// <c>ScrapeEnforcementRecord</c> payload (Appendix B.2, <c>scrape-full.json</c>) through <c>POST /runs</c> against the
/// record/replay fake and asserts the shaped <c>result</c> is <b>byte-identical</b> to a hand-derived golden
/// (<c>RecordScrapedV1</c> shape). The corpus spans the branch variety the plan names: 3/4/5-<c>&lt;br&gt;</c>
/// addresses, 0/1/many owners, violations present/absent, related-record trees, single/multi-page attachments, an
/// attachment-cap warning, empty-region edges, and two terminal cases (a CapDetail-guard redirect and an unknown owner
/// heading) that fail terminally and are NOT retried. Goldens are hand-derived from the C# reference
/// (<c>LJCMGClient.cs:177-725</c>); see each fixture's FIXTURE_NOTES.md. No Chromium, no live traffic.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class ScrapeRecordAcceptanceTests(AppFixture fixture)
{
    private IAlbaHost Host => fixture.Host;

    private static string Link(string cap) =>
        $"https://aca-prod.accela.com/LJCMG/Cap/CapDetail.aspx?Module=Enforcement&capID={cap}&agencyCode=LJCMG";

    // N = 10 golden records: each equals its RecordScrapedV1-shaped golden field-for-field, list-order included.
    [Theory]
    [InlineData("record-01-full-suburban", "24ENF-00001", "2025-06-15")]   // rich whole-program: 3-br, 1 owner, violation, parcel, processing, 1 attachment, related tree
    [InlineData("record-02-four-line-owner", "24ENF-00002", "2025-05-20")] // 4-br address, "1)"-prefixed 4-line owner, projectName == ""
    [InlineData("record-03-five-line-harbor", "24ENF-00003", "2025-05-25")]// 5-br address, no status, projectName == recordType, 1 attachment
    [InlineData("record-04-no-owners", "24ENF-00004", "2025-04-01")]       // 0 owners, "No records found." attachments row
    [InlineData("record-05-many-owners", "24ENF-00005", "2025-06-01")]     // many owners, two locations, violation
    [InlineData("record-06-multipage-attach", "24ENF-00006", "2025-05-05")]// two attachment pages, a download on each
    [InlineData("record-07-related-tree", "24ENF-00007", "2025-03-01")]    // non-trivial related tree, projectName from Highlight
    [InlineData("record-08-attach-cap", "24ENF-00008", "2025-02-10")]      // attachment 50-page cap -> complete record
    [InlineData("record-11-empty-regions", "24ENF-00011", null)]          // all lists empty, no publishDate
    [InlineData("record-12-owner-empty-block", "24ENF-00012", "2025-07-10")] // owner empty-block skip, header-only attachments grid
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

        // The Highlight row sets the record's parent, and REC-D resolves to REC-C (not REC-A) — the sibling-overwrite walk.
        result.GetProperty("parentRecordNumber").GetString().ShouldBe("REC-A");
        var related = result.GetProperty("relatedRecords");
        related[3].GetProperty("recordNumber").GetString().ShouldBe("REC-D");
        related[3].GetProperty("parentRecordNumber").GetString().ShouldBe("REC-C");

        // The garbled-width row (indent fallback) and the unknown-class row each log one error.
        run.Logs.ShouldBe([
            ("error", $"Could not determine indentation of related record: {Link("24ENF-00007")}"),
            ("error", $"Could not determine class of related record: {Link("24ENF-00007")}"),
        ]);
    }

    [Fact]
    public async Task Attachment_cap_emits_a_warning_and_still_returns_a_complete_record()
    {
        var run = await RunScrapeAsync("record-08-attach-cap", "24ENF-00008", "2025-02-10");

        run.Root.GetProperty("status").GetString().ShouldBe("succeeded"); // the record still SUCCEEDS
        AssertMatchesGolden("record-08-attach-cap", run.Root.GetProperty("result")); // complete record (attachments == [])
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

        // Exactly one attempt: no RunAttemptFailed between RunStarted and RunFailed (a terminal fail is not retried).
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
                ["adapter"] = "fake",
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

        var scenario = await Host.Scenario(x =>
        {
            x.Post.Json(body).ToUrl("/runs");
            x.StatusCodeShouldBeOk();
        });
        var root = (await scenario.ReadAsJsonAsync<JsonElement>()).Clone();

        var runId = root.GetProperty("runId").GetGuid();
        var store = Host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();
        var events = await session.Events.FetchStreamAsync(runId);
        var logs = events.Select(e => e.Data).OfType<LogEmitted>().Select(l => (l.Level, l.Message)).ToList();
        var eventTypes = events.Select(e => e.EventType).ToList();

        return new ScrapeRun(root, runId, logs, eventTypes);
    }

    private static void AssertMatchesGolden(string fixtureDir, JsonElement result)
    {
        using var golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(Runner.FixturesRoot, fixtureDir, "golden.json")));
        JsonAssert.DeepEquals(result, golden.RootElement).ShouldBeTrue();                // structural (order-sensitive arrays)
        JsonAssert.Canonical(result).ShouldBe(JsonAssert.Canonical(golden.RootElement));  // byte compare (key order included)
    }
}
