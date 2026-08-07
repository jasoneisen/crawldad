using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Payloads;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>
/// The validation-at-save gate (Deliverables 1-4): drives <c>POST /payloads</c> over real HTTP. The two canonical
/// payloads (B.1/B.2) save clean and persist an event-sourced <see cref="Payload"/>; schema and semantic violations
/// are 400s carrying the structured error list; a malformed body is a 400 ProblemDetails.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class PayloadEndpointTests(AppFixture fixture)
{
    private IAlbaHost Host => fixture.Host;

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(Runner.FixturesRoot, "Payloads", name));

    private static JsonObject Body(string payloadJson) => new() { ["payload"] = JsonNode.Parse(payloadJson) };

    private async Task<JsonElement> PostAsync(JsonObject body, int expectedStatus)
    {
        var result = await Host.Scenario(x =>
        {
            x.Post.Json(body).ToUrl("/payloads");
            x.StatusCodeShouldBe(expectedStatus);
        });
        return await result.ReadAsJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Search_payload_B1_saves_and_persists_a_drafted_payload()
    {
        var root = await PostAsync(Body(Fixture("search-full.json")), 200);

        root.GetProperty("name").GetString().ShouldBe("ljcmg.enforcement.search");
        root.GetProperty("revision").GetInt32().ShouldBe(1);
        root.GetProperty("status").GetString().ShouldBe("active");
        root.GetProperty("scriptHash").GetString().ShouldNotBeNullOrWhiteSpace();

        var payloadId = root.GetProperty("payloadId").GetGuid();
        var store = Host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();

        var events = await session.Events.FetchStreamAsync(payloadId);
        events.Select(e => e.EventType).ShouldBe([typeof(PayloadDrafted)]);
        var drafted = (PayloadDrafted)events[0].Data;
        drafted.Name.ShouldBe("ljcmg.enforcement.search");
        drafted.ScriptHash.ShouldBe(root.GetProperty("scriptHash").GetString());

        var payload = await session.LoadAsync<Payload>(payloadId);
        payload.ShouldNotBeNull();
        payload.Head.Revision.ShouldBe(1);
        payload.Name.ShouldBe("ljcmg.enforcement.search");
    }

    [Fact]
    public async Task Scrape_payload_B2_saves_clean()
    {
        var root = await PostAsync(Body(Fixture("scrape-full.json")), 200);
        root.GetProperty("name").GetString().ShouldBe("ljcmg.enforcement.scrape");
        root.GetProperty("revision").GetInt32().ShouldBe(1);
    }

    private static JsonObject Steps(string steps) =>
        Body($$"""{ "crawldad": "1", "name": "t", "config": { "backend": "input.backend" }, "vars": {}, "steps": {{steps}}, "result": "null" }""");

    private async Task<JsonElement> RejectAsync(JsonObject body)
    {
        var root = await PostAsync(body, 400);
        var errors = root.GetProperty("errors");
        errors.GetArrayLength().ShouldBeGreaterThan(0);
        return errors;
    }

    [Fact]
    public async Task An_unknown_node_head_is_a_schema_400() =>
        await RejectAsync(Steps("""[ { "frobnicate": {} } ]"""));

    [Fact]
    public async Task A_loop_without_max_iterations_is_a_schema_400() =>
        await RejectAsync(Steps("""[ { "loop": { "for": { "var": "i", "from": "0", "to": "1" }, "do": [] } } ]"""));

    [Fact]
    public async Task An_undefined_variable_use_is_a_semantic_400()
    {
        var errors = await RejectAsync(Steps("""[ { "set": { "var": "x", "value": "undefinedThing + 1" } } ]"""));
        errors.EnumerateArray().Select(e => e.GetProperty("code").GetString()).ShouldContain("undefined_reference");
    }

    [Fact]
    public async Task An_unknown_builtin_is_a_semantic_400()
    {
        var errors = await RejectAsync(Steps("""[ { "set": { "var": "x", "value": "bogusFn(1)" } } ]"""));
        errors.EnumerateArray().Select(e => e.GetProperty("code").GetString()).ShouldContain("unknown_function");
    }

    [Fact]
    public async Task A_non_object_payload_is_a_400() =>
        await PostAsync(new JsonObject { ["payload"] = "not an object" }, 400);
}
