using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Payloads;
using Crawldad.Web.Infrastructure.Security;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>
/// The managed-payload versioning surface (§14.1): revise/rename/archive mutations, the list/get/revision/diff queries,
/// and the credential-scrubbing invariant that the stored script — the payload's persisted artifact — never carries a
/// credential (§12). Revision == stream version (every mutation advances it; only a revise changes the script hash).
/// </summary>
[Collection(IntegrationCollection.Name)]
public class PayloadVersioningTests(AppFixture fixture)
{
    private const string _v1 = """{ "crawldad": "1", "name": "demo", "config": { "backend": "input.backend" }, "steps": [], "result": "'v1'" }""";
    private const string _v2 = """{ "crawldad": "1", "name": "demo", "config": { "backend": "input.backend" }, "steps": [], "result": "'v2'" }""";
    private const string _leaky =
        """{ "crawldad": "1", "name": "leaky", "config": { "backend": "input.backend" }, "steps": [ { "goto": { "url": "https://portal.example/a?token=LEAKCANARY_pl_9876543210&x=1" } } ], "result": "'ok'" }""";

    private IAlbaHost Host => fixture.Host;

    private async Task<JsonElement> ReadPost(string url, JsonNode body, int status)
    {
        var result = await Host.Scenario(x =>
        {
            x.Post.Json(body).ToUrl(url);
            x.StatusCodeShouldBe(status);
        });
        return await result.ReadAsJsonAsync<JsonElement>();
    }

    private async Task StatusPost(string url, JsonNode body, int status) =>
        await Host.Scenario(x =>
        {
            x.Post.Json(body).ToUrl(url);
            x.StatusCodeShouldBe(status);
        });

    private async Task<JsonElement> ReadGet(string url)
    {
        var result = await Host.Scenario(x =>
        {
            x.Get.Url(url);
            x.StatusCodeShouldBe(200);
        });
        return await result.ReadAsJsonAsync<JsonElement>();
    }

    private async Task StatusGet(string url, int status) =>
        await Host.Scenario(x =>
        {
            x.Get.Url(url);
            x.StatusCodeShouldBe(status);
        });

    private static JsonObject Payload(string json) => new() { ["payload"] = JsonNode.Parse(json) };

    private async Task<Guid> DraftAsync(string json) =>
        (await ReadPost("/payloads", Payload(json), 200)).GetProperty("payloadId").GetGuid();

    // ----- revise ------------------------------------------------------------

    [Fact]
    public async Task Revise_appends_a_new_revision_with_a_note()
    {
        var id = await DraftAsync(_v1);
        var body = new JsonObject { ["payload"] = JsonNode.Parse(_v2), ["note"] = "tweaked the result" };

        var head = await ReadPost($"/payloads/{id}/revise", body, 200);
        head.GetProperty("revision").GetInt32().ShouldBe(2);
        head.GetProperty("status").GetString().ShouldBe("active");
        var newHash = head.GetProperty("scriptHash").GetString();

        // The head DTO and a re-fetch agree; the stored revise event carries the scrubbed note.
        (await ReadGet($"/payloads/{id}")).GetProperty("scriptHash").GetString().ShouldBe(newHash);

        var store = Host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(TestTenants.PrimaryId);
        var events = await session.Events.FetchStreamAsync(id);
        events.Select(e => e.EventType).ShouldBe([typeof(PayloadDrafted), typeof(PayloadRevised)]);
        ((PayloadRevised)events[1].Data).Note.ShouldBe("tweaked the result");
    }

    [Fact]
    public async Task Revising_an_unknown_payload_is_a_404() =>
        await StatusPost($"/payloads/{Guid.NewGuid()}/revise", Payload(_v2), 404);

    [Fact]
    public async Task Revising_with_an_invalid_script_is_a_400()
    {
        var id = await DraftAsync(_v1);
        var bad = """{ "crawldad": "1", "name": "demo", "config": { "backend": "input.backend" }, "steps": [ { "frobnicate": {} } ], "result": "'x'" }""";

        var problem = await ReadPost($"/payloads/{id}/revise", Payload(bad), 400);
        problem.GetProperty("errors").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Revising_an_archived_payload_is_a_400()
    {
        var id = await DraftAsync(_v1);
        await ReadPost($"/payloads/{id}/archive", new JsonObject(), 200);

        var problem = await ReadPost($"/payloads/{id}/revise", Payload(_v2), 400);
        problem.GetProperty("errors")[0].GetProperty("code").GetString().ShouldBe("payload_archived");
    }

    // ----- rename ------------------------------------------------------------

    [Fact]
    public async Task Rename_changes_the_name_and_advances_the_revision_without_changing_the_hash()
    {
        var id = await DraftAsync(_v1);
        var drafted = await ReadGet($"/payloads/{id}");
        var originalHash = drafted.GetProperty("scriptHash").GetString();

        var head = await ReadPost($"/payloads/{id}/rename", new JsonObject { ["name"] = "renamed" }, 200);
        head.GetProperty("name").GetString().ShouldBe("renamed");
        head.GetProperty("revision").GetInt32().ShouldBe(2);              // metadata revision advances the version
        head.GetProperty("scriptHash").GetString().ShouldBe(originalHash); // but the script hash is unchanged
    }

    [Fact]
    public async Task Renaming_an_unknown_payload_is_a_404() =>
        await StatusPost($"/payloads/{Guid.NewGuid()}/rename", new JsonObject { ["name"] = "x" }, 404);

    [Fact]
    public async Task Renaming_to_an_empty_name_is_a_400()
    {
        var id = await DraftAsync(_v1);
        await StatusPost($"/payloads/{id}/rename", new JsonObject { ["name"] = "" }, 400);
    }

    [Fact]
    public async Task Renaming_an_archived_payload_is_a_400()
    {
        var id = await DraftAsync(_v1);
        await ReadPost($"/payloads/{id}/archive", new JsonObject(), 200);
        await StatusPost($"/payloads/{id}/rename", new JsonObject { ["name"] = "x" }, 400);
    }

    // ----- archive -----------------------------------------------------------

    [Fact]
    public async Task Archive_marks_the_payload_archived()
    {
        var id = await DraftAsync(_v1);
        var head = await ReadPost($"/payloads/{id}/archive", new JsonObject(), 200);
        head.GetProperty("status").GetString().ShouldBe("archived");
        head.GetProperty("revision").GetInt32().ShouldBe(2);

        (await ReadGet($"/payloads/{id}")).GetProperty("status").GetString().ShouldBe("archived");
    }

    [Fact]
    public async Task Archiving_an_unknown_payload_is_a_404() =>
        await StatusPost($"/payloads/{Guid.NewGuid()}/archive", new JsonObject(), 404);

    [Fact]
    public async Task Archiving_an_already_archived_payload_is_a_400()
    {
        var id = await DraftAsync(_v1);
        await ReadPost($"/payloads/{id}/archive", new JsonObject(), 200);
        await StatusPost($"/payloads/{id}/archive", new JsonObject(), 400);
    }

    // ----- list / get / revision --------------------------------------------

    [Fact]
    public async Task List_reflects_draft_revise_rename_and_archive_through_the_summary_projection()
    {
        var a = await DraftAsync(_v1);
        var b = await DraftAsync(_v1);
        await ReadPost($"/payloads/{a}/revise", Payload(_v2), 200); // note omitted ⇒ null-note branch
        await ReadPost($"/payloads/{a}/rename", new JsonObject { ["name"] = "alpha" }, 200);
        await ReadPost($"/payloads/{b}/archive", new JsonObject(), 200);

        var payloads = (await ReadGet("/payloads")).GetProperty("payloads").EnumerateArray().ToList();

        var rowA = payloads.Single(p => p.GetProperty("payloadId").GetGuid() == a);
        rowA.GetProperty("name").GetString().ShouldBe("alpha");
        rowA.GetProperty("revision").GetInt32().ShouldBe(3); // draft + revise + rename
        rowA.GetProperty("status").GetString().ShouldBe("active");
        rowA.GetProperty("draftedAt").GetDateTimeOffset().ShouldBe(FakeClock.Fixed);

        var rowB = payloads.Single(p => p.GetProperty("payloadId").GetGuid() == b);
        rowB.GetProperty("revision").GetInt32().ShouldBe(2); // draft + archive
        rowB.GetProperty("status").GetString().ShouldBe("archived");
    }

    [Fact]
    public async Task Get_an_unknown_payload_is_a_404() =>
        await StatusGet($"/payloads/{Guid.NewGuid()}", 404);

    [Fact]
    public async Task Get_a_specific_revision_returns_that_revisions_script()
    {
        var id = await DraftAsync(_v1);
        await ReadPost($"/payloads/{id}/revise", Payload(_v2), 200);

        var rev1 = await ReadGet($"/payloads/{id}/revisions/1");
        rev1.GetProperty("revision").GetInt32().ShouldBe(1);
        rev1.GetProperty("script").GetProperty("result").GetString().ShouldBe("'v1'");

        var rev2 = await ReadGet($"/payloads/{id}/revisions/2");
        rev2.GetProperty("script").GetProperty("result").GetString().ShouldBe("'v2'");
    }

    [Fact]
    public async Task Get_a_revision_of_an_unknown_payload_is_a_404() =>
        await StatusGet($"/payloads/{Guid.NewGuid()}/revisions/1", 404);

    [Fact]
    public async Task Get_an_out_of_range_revision_is_a_404()
    {
        var id = await DraftAsync(_v1);
        await StatusGet($"/payloads/{id}/revisions/99", 404);
    }

    // ----- diff --------------------------------------------------------------

    [Fact]
    public async Task Diff_between_two_revisions_returns_both_scripts_and_a_minimal_change_set()
    {
        var id = await DraftAsync(_v1);
        await ReadPost($"/payloads/{id}/revise", Payload(_v2), 200);

        var diff = await ReadGet($"/payloads/{id}/diff/1/2");
        diff.GetProperty("fromRevision").GetInt32().ShouldBe(1);
        diff.GetProperty("toRevision").GetInt32().ShouldBe(2);
        diff.GetProperty("fromScript").GetProperty("result").GetString().ShouldBe("'v1'");
        diff.GetProperty("toScript").GetProperty("result").GetString().ShouldBe("'v2'");

        var changes = diff.GetProperty("changes").EnumerateArray().ToList();
        changes.Count.ShouldBe(1); // only the result expression changed
        changes[0].GetProperty("path").GetString().ShouldBe("/result");
        changes[0].GetProperty("kind").GetString().ShouldBe("changed");
        changes[0].GetProperty("from").GetString().ShouldBe("'v1'");
        changes[0].GetProperty("to").GetString().ShouldBe("'v2'");
    }

    [Fact]
    public async Task Diff_of_an_unknown_payload_is_a_404() =>
        await StatusGet($"/payloads/{Guid.NewGuid()}/diff/1/2", 404);

    [Fact]
    public async Task Diff_with_an_out_of_range_from_revision_is_a_404()
    {
        var id = await DraftAsync(_v1);
        await StatusGet($"/payloads/{id}/diff/99/1", 404);
    }

    [Fact]
    public async Task Diff_with_an_out_of_range_to_revision_is_a_404()
    {
        var id = await DraftAsync(_v1);
        await StatusGet($"/payloads/{id}/diff/1/99", 404);
    }

    // ----- scrubbing (§12) ---------------------------------------------------

    [Fact]
    public async Task A_credential_in_a_drafted_script_never_lands_in_any_stored_event_or_response()
    {
        const string Secret = "LEAKCANARY_pl_9876543210";
        var response = await ReadPost("/payloads", Payload(_leaky), 200);
        var id = response.GetProperty("payloadId").GetGuid();
        response.GetRawText().ShouldNotContain(Secret);

        // The stored event's script is redacted at the persistence boundary.
        var store = Host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(TestTenants.PrimaryId);
        var drafted = (PayloadDrafted)(await session.Events.FetchStreamAsync(id))[0].Data;
        drafted.Script.ShouldNotContain(Secret);
        drafted.Script.ShouldContain("token=" + CredentialScrubber.Redaction);

        // Every response that echoes the script echoes the already-scrubbed stored script.
        (await ReadGet($"/payloads/{id}/revisions/1")).GetRawText().ShouldNotContain(Secret);
        (await ReadGet("/payloads")).GetRawText().ShouldNotContain(Secret);
    }

    [Fact]
    public async Task A_credential_revised_into_a_script_is_scrubbed_in_the_stored_event_and_the_revision_and_diff_responses()
    {
        const string Secret = "LEAKCANARY_pl_9876543210";
        var id = await DraftAsync(_v1);

        // Revise IN the leaky script (its URL carries token=<secret>) with a note that also carries a token= param — the
        // revise path runs the SAME scrub-then-validate gate as a draft (§12).
        await ReadPost($"/payloads/{id}/revise", new JsonObject { ["payload"] = JsonNode.Parse(_leaky), ["note"] = "rotated token=" + Secret }, 200);

        // The stored PayloadRevised event redacts both the script and the note at the persistence boundary.
        var store = Host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(TestTenants.PrimaryId);
        var revised = (PayloadRevised)(await session.Events.FetchStreamAsync(id))[1].Data;
        revised.Script.ShouldNotContain(Secret);
        revised.Script.ShouldContain("token=" + CredentialScrubber.Redaction);
        revised.Note!.ShouldBe("rotated token=" + CredentialScrubber.Redaction);

        // Every response echoing the revised script echoes the already-scrubbed bytes: the specific revision AND the diff.
        (await ReadGet($"/payloads/{id}/revisions/2")).GetRawText().ShouldNotContain(Secret);
        (await ReadGet($"/payloads/{id}/diff/1/2")).GetRawText().ShouldNotContain(Secret);
    }
}
