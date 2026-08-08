using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Runs;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>
/// The Phase 1 acceptance gate: drives the real <c>POST /runs</c> HTTP endpoint against the fake backend and asserts
/// the shaped output equals the golden, plus the failure paths and the persisted trace events.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class RunEndpointTests(AppFixture fixture)
{
    private IAlbaHost Host => fixture.Host;

    private static JsonObject Body(string payloadJson, JsonNode? inputs) =>
        new() { ["payload"] = JsonNode.Parse(payloadJson), ["inputs"] = inputs };

    private static JsonObject FakeBackendInput(string? startDate = null, string? endDate = null)
    {
        var inputs = new JsonObject
        {
            ["backend"] = new JsonObject
            {
                ["adapter"] = "fake",
                ["options"] = new JsonObject { ["fixture"] = "caphome-search" },
            },
        };
        if (startDate is not null)
        {
            inputs["startDate"] = startDate;
        }

        if (endDate is not null)
        {
            inputs["endDate"] = endDate;
        }

        return inputs;
    }

    private async Task<JsonElement> PostAsync(JsonObject body, int expectedStatus = 200)
    {
        var result = await Host.Scenario(x =>
        {
            x.Post.Json(body).ToUrl("/runs");
            x.StatusCodeShouldBe(expectedStatus);
        });

        return await result.ReadAsJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Fragment_runs_end_to_end_and_result_equals_golden()
    {
        var root = await PostAsync(Body(Runner.FragmentPayload(), FakeBackendInput("01/01/2024", "01/31/2024")));

        root.GetProperty("status").GetString().ShouldBe("succeeded");

        using var golden = JsonDocument.Parse(Runner.Golden());
        var result = root.GetProperty("result");
        JsonAssert.DeepEquals(result, golden.RootElement).ShouldBeTrue();                       // structural
        JsonAssert.Canonical(result).ShouldBe(JsonAssert.Canonical(golden.RootElement));        // byte compare

        var stats = root.GetProperty("stats");
        stats.GetProperty("requests").GetInt32().ShouldBe(2);       // goto + matched waitForRequest
        stats.GetProperty("steps").GetInt32().ShouldBe(37);
        stats.GetProperty("durationMs").GetInt64().ShouldBe(0);     // frozen TimeProvider seam
        stats.GetProperty("cacheHits").GetInt32().ShouldBe(0);
        stats.GetProperty("downloads").GetInt32().ShouldBe(0);

        var runId = root.GetProperty("runId").GetGuid();
        var store = Host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(TestTenants.PrimaryId);

        var events = await session.Events.FetchStreamAsync(runId);
        events.Select(e => e.EventType).ShouldBe([typeof(RunStarted), typeof(RunSucceeded)]);
        ((RunStarted)events[0].Data).PayloadName.ShouldBe("ljcmg.enforcement.search.fragment");
        ((RunStarted)events[0].Data).InputKeys.ShouldBe(["backend", "startDate", "endDate"]);

        var run = await session.LoadAsync<Run>(runId);
        run.ShouldNotBeNull();
        run.Status.ShouldBe(RunLifecycle.Succeeded);
        run.Id.ShouldBe(runId);
    }

    [Fact]
    public async Task Unknown_backend_adapter_is_a_terminal_failure()
    {
        var inputs = FakeBackendInput();
        inputs["backend"]!["adapter"] = "does-not-exist";

        var root = await PostAsync(Body(Runner.FragmentPayload(), inputs));

        root.GetProperty("status").GetString().ShouldBe("failed");
        root.TryGetProperty("result", out _).ShouldBeFalse();
        var failure = root.GetProperty("failure");
        failure.GetProperty("class").GetString().ShouldBe("terminal");
        failure.GetProperty("code").GetString().ShouldBe("unknown_backend_adapter");

        var runId = root.GetProperty("runId").GetGuid();
        var store = Host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(TestTenants.PrimaryId);
        var events = await session.Events.FetchStreamAsync(runId);
        events.Select(e => e.EventType).ShouldBe([typeof(RunStarted), typeof(RunFailed)]);
        (await session.LoadAsync<Run>(runId))!.Status.ShouldBe(RunLifecycle.Failed);
    }

    [Fact]
    public async Task Loop_without_max_iterations_is_a_terminal_failure()
    {
        const string Payload =
            """
            { "name": "t", "config": { "backend": "input.backend" }, "vars": {},
              "steps": [ { "loop": { "for": { "var": "i", "from": "0", "to": "1" }, "do": [] } } ],
              "result": "null" }
            """;

        var root = await PostAsync(Body(Payload, FakeBackendInput()));

        root.GetProperty("status").GetString().ShouldBe("failed");
        var failure = root.GetProperty("failure");
        failure.GetProperty("class").GetString().ShouldBe("terminal");
        failure.GetProperty("code").GetString().ShouldBe("missing_max_iterations");
        failure.GetProperty("atStep").GetProperty("index").GetInt32().ShouldBe(0);
        failure.GetProperty("atStep").GetProperty("kind").GetString().ShouldBe("loop");
    }

    [Fact]
    public async Task Missing_inputs_fails_with_invalid_backend_binding()
    {
        // No inputs at all: validator allows an absent inputs object; the run then fails because input.backend is null.
        var root = await PostAsync(new JsonObject { ["payload"] = JsonNode.Parse(Runner.FragmentPayload()) });

        root.GetProperty("status").GetString().ShouldBe("failed");
        root.GetProperty("failure").GetProperty("code").GetString().ShouldBe("invalid_backend_binding");
    }

    [Fact]
    public async Task Nameless_payload_persists_as_unnamed()
    {
        const string Payload =
            """
            { "config": { "backend": "input.backend" }, "vars": {}, "steps": [], "result": "'ok'" }
            """;

        var root = await PostAsync(Body(Payload, FakeBackendInput()));

        root.GetProperty("status").GetString().ShouldBe("succeeded");
        root.GetProperty("result").GetString().ShouldBe("ok");

        var runId = root.GetProperty("runId").GetGuid();
        var store = Host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(TestTenants.PrimaryId);
        var events = await session.Events.FetchStreamAsync(runId);
        ((RunStarted)events[0].Data).PayloadName.ShouldBe("unnamed");
    }

    [Fact]
    public async Task Non_object_payload_is_a_400() =>
        await PostAsync(new JsonObject { ["payload"] = "not an object", ["inputs"] = new JsonObject() }, expectedStatus: 400);

    [Fact]
    public async Task Non_object_inputs_is_a_400() =>
        await PostAsync(new JsonObject { ["payload"] = JsonNode.Parse(Runner.FragmentPayload()), ["inputs"] = 5 }, expectedStatus: 400);
}
