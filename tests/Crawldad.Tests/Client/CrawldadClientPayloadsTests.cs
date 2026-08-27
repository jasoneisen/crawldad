using System.Text.Json;
using Crawldad.Client;
using Crawldad.Contracts.Drift;
using Crawldad.Contracts.Payloads;

namespace Crawldad.Tests.Client;

/// <summary>Unit tests for the managed-payload surface over a stub handler: save (both overloads), the read side (list,
/// get, revision, diff, drift-status with and without a threshold), revise, rename, and archive.</summary>
public class CrawldadClientPayloadsTests
{
    private static JsonElement JsonElementOf(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static readonly JsonElement _payload = JsonElementOf("""{ "crawldad": "1", "name": "p" }""");

    [Fact]
    public async Task Save_posts_the_payload_and_returns_the_head()
    {
        var id = Guid.NewGuid();
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Json(new PayloadResponse(id, "p", 1, "hash", PayloadStatus.Active)));
        var client = ClientTestHarness.ClientFor(handler);

        var response = await client.SavePayloadAsync(_payload);

        response.PayloadId.ShouldBe(id);
        response.Revision.ShouldBe(1);
        response.Status.ShouldBe(PayloadStatus.Active);
        handler.Last.Method.ShouldBe(HttpMethod.Post);
        handler.Last.Path.ShouldBe("/payloads");
        using var body = JsonDocument.Parse(handler.Last.Body);
        body.RootElement.GetProperty("payload").GetProperty("name").GetString().ShouldBe("p");
    }

    [Fact]
    public async Task List_reads_the_summary_rows()
    {
        var item = new PayloadListItem(Guid.NewGuid(), "p", 2, "hash", PayloadStatus.Active, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Json(new PayloadListResponse([item])));
        var client = ClientTestHarness.ClientFor(handler);

        var list = await client.ListPayloadsAsync();

        list.Payloads.ShouldHaveSingleItem().Revision.ShouldBe(2);
        handler.Last.Path.ShouldBe("/payloads");
    }

    [Fact]
    public async Task Get_reads_the_state()
    {
        var id = Guid.NewGuid();
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Json(new PayloadResponse(id, "p", 4, "hash", PayloadStatus.Archived)));
        var client = ClientTestHarness.ClientFor(handler);

        (await client.GetPayloadAsync(id)).Status.ShouldBe(PayloadStatus.Archived);
        handler.Last.Path.ShouldBe($"/payloads/{id}");
    }

    [Fact]
    public async Task Get_revision_reads_the_stored_script()
    {
        var id = Guid.NewGuid();
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new PayloadRevisionResponse(id, 2, "hash", JsonElementOf("""{ "name": "p" }"""))));
        var client = ClientTestHarness.ClientFor(handler);

        var revision = await client.GetPayloadRevisionAsync(id, 2);

        revision.Revision.ShouldBe(2);
        revision.Script.GetProperty("name").GetString().ShouldBe("p");
        handler.Last.Path.ShouldBe($"/payloads/{id}/revisions/2");
    }

    [Fact]
    public async Task Diff_reads_both_scripts_and_the_changes()
    {
        var id = Guid.NewGuid();
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new PayloadDiffResponse(id, 1, 2, JsonElementOf("{}"), JsonElementOf("{}"), [])));
        var client = ClientTestHarness.ClientFor(handler);

        var diff = await client.DiffPayloadAsync(id, 1, 2);

        diff.FromRevision.ShouldBe(1);
        diff.ToRevision.ShouldBe(2);
        handler.Last.Path.ShouldBe($"/payloads/{id}/diff/1/2");
    }

    [Fact]
    public async Task Revise_from_a_request_posts_the_new_script()
    {
        var id = Guid.NewGuid();
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Json(new PayloadResponse(id, "p", 2, "hash2", PayloadStatus.Active)));
        var client = ClientTestHarness.ClientFor(handler);

        var response = await client.RevisePayloadAsync(id, new RevisePayloadRequest(_payload, "a note"));

        response.Revision.ShouldBe(2);
        handler.Last.Path.ShouldBe($"/payloads/{id}/revise");
        using var body = JsonDocument.Parse(handler.Last.Body);
        body.RootElement.GetProperty("note").GetString().ShouldBe("a note");
    }

    [Fact]
    public async Task Revise_convenience_overload_carries_the_note()
    {
        var id = Guid.NewGuid();
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Json(new PayloadResponse(id, "p", 3, "hash3", PayloadStatus.Active)));
        var client = ClientTestHarness.ClientFor(handler);

        await client.RevisePayloadAsync(id, _payload, note: "quick");

        using var body = JsonDocument.Parse(handler.Last.Body);
        body.RootElement.GetProperty("note").GetString().ShouldBe("quick");
    }

    [Fact]
    public async Task Rename_posts_the_new_name()
    {
        var id = Guid.NewGuid();
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Json(new PayloadResponse(id, "renamed", 2, "hash", PayloadStatus.Active)));
        var client = ClientTestHarness.ClientFor(handler);

        var response = await client.RenamePayloadAsync(id, "renamed");

        response.Name.ShouldBe("renamed");
        handler.Last.Path.ShouldBe($"/payloads/{id}/rename");
        using var body = JsonDocument.Parse(handler.Last.Body);
        body.RootElement.GetProperty("name").GetString().ShouldBe("renamed");
    }

    [Fact]
    public async Task Archive_posts_with_no_body()
    {
        var id = Guid.NewGuid();
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Json(new PayloadResponse(id, "p", 2, "hash", PayloadStatus.Archived)));
        var client = ClientTestHarness.ClientFor(handler);

        (await client.ArchivePayloadAsync(id)).Status.ShouldBe(PayloadStatus.Archived);
        handler.Last.Method.ShouldBe(HttpMethod.Post);
        handler.Last.Path.ShouldBe($"/payloads/{id}/archive");
        handler.Last.Body.ShouldBeEmpty();
    }

    [Fact]
    public async Task Drift_status_without_a_threshold_omits_the_query()
    {
        var id = Guid.NewGuid();
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new PayloadDriftStatus(id, "p", null, DriftState.Steady, false, 0, 0, 0, 0, null, null, [], null)));
        var client = ClientTestHarness.ClientFor(handler);

        (await client.GetPayloadDriftStatusAsync(id)).State.ShouldBe(DriftState.Steady);
        handler.Last.Path.ShouldBe($"/payloads/{id}/drift-status");
        handler.Last.Query.ShouldBeEmpty();
    }

    [Fact]
    public async Task Drift_status_with_a_threshold_sets_the_query()
    {
        var id = Guid.NewGuid();
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new PayloadDriftStatus(id, "p", 2, DriftState.Drifted, true, 5, 3, 4, 1, null, null, [], null)));
        var client = ClientTestHarness.ClientFor(handler);

        var status = await client.GetPayloadDriftStatusAsync(id, threshold: 1);

        status.Drifted.ShouldBeTrue();
        handler.Last.Query.ShouldBe("?threshold=1");
    }
}
