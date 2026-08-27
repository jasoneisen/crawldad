using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Api.Features.Runs;
using Crawldad.Tests.Support;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>Run pinning + drift: a run executes either an inline payload or a pinned managed payload revision, and
/// reports drift (pinned-vs-head). The two demo revisions differ ONLY in <c>result</c> (<c>'v1'</c> vs <c>'v2'</c>)
/// so pinning is proven by observably different output as the head moves.</summary>
[Collection(IntegrationCollection.Name)]
public class RunPinningTests(AppFixture fixture)
{
    private const string _v1 = """{ "crawldad": "1", "name": "drift-demo", "config": { "backend": "input.backend" }, "steps": [], "result": "'v1'" }""";
    private const string _v2 = """{ "crawldad": "1", "name": "drift-demo", "config": { "backend": "input.backend" }, "steps": [], "result": "'v2'" }""";

    private IAlbaHost Host => fixture.Host;

    private static JsonObject FakeInputs() => new()
    {
        ["backend"] = new JsonObject { ["adapter"] = "fake", ["options"] = new JsonObject { ["fixture"] = "caphome-search" } },
    };

    private async Task<JsonElement> PostAsync(string url, JsonObject body, int expectedStatus)
    {
        var result = await Host.Scenario(x =>
        {
            x.Post.Json(body).ToUrl(url);
            x.StatusCodeShouldBe(expectedStatus);
        });
        return await result.ReadAsJsonAsync<JsonElement>();
    }

    private async Task<Guid> DraftAsync(string payloadJson) =>
        (await PostAsync("/payloads", new JsonObject { ["payload"] = JsonNode.Parse(payloadJson) }, 200)).GetProperty("payloadId").GetGuid();

    private async Task ReviseAsync(Guid id, string payloadJson) =>
        await PostAsync($"/payloads/{id}/revise", new JsonObject { ["payload"] = JsonNode.Parse(payloadJson) }, 200);

    private async Task<JsonElement> RunPinnedAsync(Guid id, int? revision, int expectedStatus = 200)
    {
        var body = new JsonObject { ["payloadId"] = id, ["inputs"] = FakeInputs() };
        if (revision is not null)
        {
            body["revision"] = revision;
        }

        return await PostAsync("/runs", body, expectedStatus);
    }

    private async Task<JsonElement> DriftAsync(Guid runId)
    {
        var result = await Host.Scenario(x =>
        {
            x.Get.Url($"/runs/{runId}/drift");
            x.StatusCodeShouldBe(200);
        });
        return await result.ReadAsJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Pinning_a_revision_executes_that_revisions_script_and_drift_tracks_the_head()
    {
        var id = await DraftAsync(_v1);

        // Run pinned at head (revision omitted ⇒ head = 1): executes rev-1's script.
        var run1 = await RunPinnedAsync(id, revision: null);
        run1.GetProperty("status").GetString().ShouldBe("succeeded");
        run1.GetProperty("result").GetString().ShouldBe("v1");
        var run1Id = run1.GetProperty("runId").GetGuid();

        // RunStarted pinned the exact payload + revision + hash.
        var store = Host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(TestTenants.PrimaryId);
        var started = (RunStarted)(await session.Events.FetchStreamAsync(run1Id))[0].Data;
        started.PayloadId.ShouldBe(id);
        started.PayloadRevision.ShouldBe(1);
        started.ScriptHash.ShouldNotBeNullOrWhiteSpace();

        // Pinned at head, head unmoved ⇒ NO drift.
        var beforeRevise = await DriftAsync(run1Id);
        beforeRevise.GetProperty("payloadId").GetGuid().ShouldBe(id);
        beforeRevise.GetProperty("pinnedRevision").GetInt32().ShouldBe(1);
        beforeRevise.GetProperty("headRevision").GetInt32().ShouldBe(1);
        beforeRevise.GetProperty("drifted").GetBoolean().ShouldBeFalse();

        // Move the head: revision 2 returns 'v2'.
        await ReviseAsync(id, _v2);

        // The historical run now reports drift (pinned 1 vs head 2), with differing hashes.
        var afterRevise = await DriftAsync(run1Id);
        afterRevise.GetProperty("pinnedRevision").GetInt32().ShouldBe(1);
        afterRevise.GetProperty("headRevision").GetInt32().ShouldBe(2);
        afterRevise.GetProperty("drifted").GetBoolean().ShouldBeTrue();
        afterRevise.GetProperty("pinnedScriptHash").GetString()
            .ShouldNotBe(afterRevise.GetProperty("headScriptHash").GetString());

        // Re-running EXPLICITLY at revision 1 (after the head moved) still executes rev-1's script.
        (await RunPinnedAsync(id, revision: 1)).GetProperty("result").GetString().ShouldBe("v1");

        // Pinning the head (now 2) and revision 2 both execute rev-2's script — observably different from rev 1.
        (await RunPinnedAsync(id, revision: null)).GetProperty("result").GetString().ShouldBe("v2");
        (await RunPinnedAsync(id, revision: 2)).GetProperty("result").GetString().ShouldBe("v2");
    }

    [Fact]
    public async Task Pinning_an_unknown_payload_is_a_400()
    {
        var body = new JsonObject { ["payloadId"] = Guid.NewGuid(), ["inputs"] = FakeInputs() };
        var problem = await PostAsync("/runs", body, 400);
        problem.GetProperty("code").GetString().ShouldBe("unknown_payload");
    }

    [Fact]
    public async Task Pinning_an_unknown_revision_is_a_400()
    {
        var id = await DraftAsync(_v1);
        var problem = await RunPinnedAsync(id, revision: 99, expectedStatus: 400);
        problem.GetProperty("code").GetString().ShouldBe("unknown_revision");
    }

    [Fact]
    public async Task Pinning_an_archived_payload_is_a_400()
    {
        var id = await DraftAsync(_v1);
        await PostAsync($"/payloads/{id}/archive", new JsonObject(), 200);

        var problem = await RunPinnedAsync(id, revision: null, expectedStatus: 400);
        problem.GetProperty("code").GetString().ShouldBe("payload_archived");
    }

    [Fact]
    public async Task Supplying_both_payload_and_payloadId_is_a_400() =>
        await PostAsync("/runs", new JsonObject
        {
            ["payload"] = JsonNode.Parse(_v1),
            ["payloadId"] = Guid.NewGuid(),
            ["inputs"] = FakeInputs(),
        }, 400);

    [Fact]
    public async Task Supplying_neither_payload_nor_payloadId_is_a_400() =>
        await PostAsync("/runs", new JsonObject { ["inputs"] = FakeInputs() }, 400);

    [Fact]
    public async Task An_inline_run_never_drifts()
    {
        var inline = await PostAsync("/runs", new JsonObject { ["payload"] = JsonNode.Parse(_v1), ["inputs"] = FakeInputs() }, 200);
        inline.GetProperty("result").GetString().ShouldBe("v1");

        var drift = await DriftAsync(inline.GetProperty("runId").GetGuid());
        drift.GetProperty("drifted").GetBoolean().ShouldBeFalse();
        drift.TryGetProperty("payloadId", out var payloadId).ShouldBeTrue();
        payloadId.ValueKind.ShouldBe(JsonValueKind.Null);
        drift.GetProperty("pinnedScriptHash").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Drift_for_an_unknown_run_is_a_404() =>
        await Host.Scenario(x =>
        {
            x.Get.Url($"/runs/{Guid.NewGuid()}/drift");
            x.StatusCodeShouldBe(404);
        });
}
