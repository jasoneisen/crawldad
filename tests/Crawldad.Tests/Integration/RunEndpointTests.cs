using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Runs;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>Drives the real <c>POST /runs</c> HTTP endpoint against the fake backend and asserts the shaped output
/// equals the golden, plus the failure paths and the persisted trace events.</summary>
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
    public async Task A_sync_run_under_the_window_writes_no_progress_row_so_get_is_404()
    {
        // A run finishing within the sync-upgrade window (the default 120s — trivially, for this run) stays fully
        // synchronous: it writes no async RunProgress read model, so GET /runs/{id} is 404 exactly as before the sync cap.
        var root = await PostAsync(Body(Runner.FragmentPayload(), FakeBackendInput("01/01/2024", "01/31/2024")));
        root.GetProperty("status").GetString().ShouldBe("succeeded");
        var runId = root.GetProperty("runId").GetGuid();

        await Host.Scenario(x =>
        {
            x.Get.Url($"/runs/{runId}");
            x.StatusCodeShouldBe(404);
        });
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
    public async Task Loop_with_typed_number_bounds_runs_end_to_end()
    {
        // from/to/step as typed JSON numbers drive the loop end-to-end (schema → semantic pass → interpreter), behaving
        // exactly as the Expr-string form — inclusive 0..4 by 2 accumulates [0, 2, 4].
        const string Payload =
            """
            { "name": "typed-loop", "config": { "backend": "input.backend" }, "vars": { "acc": [] },
              "steps": [ { "loop": { "maxIterations": 100, "for": { "var": "i", "from": 0, "to": 4, "inclusiveTo": true, "step": 2 },
                "do": [ { "push": { "into": "acc", "value": "i" } } ] } } ],
              "result": "acc" }
            """;

        var root = await PostAsync(Body(Payload, FakeBackendInput()));

        root.GetProperty("status").GetString().ShouldBe("succeeded");
        root.GetProperty("result").EnumerateArray().Select(e => e.GetInt64()).ShouldBe([0L, 2L, 4L]);
    }

    [Fact]
    public async Task Non_integral_loop_bound_is_a_terminal_failure_not_a_500()
    {
        // A non-integral loop.for bound is a classified terminal type_error, never an unhandled 500 — a failed run is
        // HTTP 200 with a failure body. POST /runs runs no save-time walker on an inline payload, so run-time
        // classification is what catches the typed 2.5 here.
        const string Payload =
            """
            { "name": "t", "config": { "backend": "input.backend" }, "vars": {},
              "steps": [ { "loop": { "maxIterations": 10, "for": { "var": "i", "from": 0, "to": 2.5 }, "do": [] } } ],
              "result": "null" }
            """;

        var root = await PostAsync(Body(Payload, FakeBackendInput())); // expectedStatus defaults to 200

        root.GetProperty("status").GetString().ShouldBe("failed");
        var failure = root.GetProperty("failure");
        failure.GetProperty("class").GetString().ShouldBe("terminal");
        failure.GetProperty("code").GetString().ShouldBe("type_error");
        failure.GetProperty("atStep").GetProperty("index").GetInt32().ShouldBe(0);
        failure.GetProperty("atStep").GetProperty("kind").GetString().ShouldBe("loop");
    }

    [Fact]
    public async Task Non_integral_locate_nth_is_a_terminal_failure_not_a_500()
    {
        // A non-integral locate.nth is a classified terminal type_error, never an unhandled 500 — a failed run is HTTP
        // 200 with a failure body. POST /runs runs no save-time walker on an inline payload, so run-time
        // RequireNthIndex is what catches the literal 2.5 here (at step 1).
        const string Payload =
            """
            { "name": "t", "config": { "backend": "input.backend" }, "vars": {},
              "steps": [ { "locate": { "var": "rows", "selector": "tr" } },
                         { "locate": { "var": "x", "from": "rows", "nth": "2.5" } } ],
              "result": "null" }
            """;

        var root = await PostAsync(Body(Payload, FakeBackendInput())); // expectedStatus defaults to 200

        root.GetProperty("status").GetString().ShouldBe("failed");
        var failure = root.GetProperty("failure");
        failure.GetProperty("class").GetString().ShouldBe("terminal");
        failure.GetProperty("code").GetString().ShouldBe("type_error");
        failure.GetProperty("atStep").GetProperty("index").GetInt32().ShouldBe(1);
        failure.GetProperty("atStep").GetProperty("kind").GetString().ShouldBe("locate");
    }

    [Fact]
    public async Task Non_bool_sel_first_is_a_terminal_failure_not_a_500()
    {
        // A non-bool `first` in a structured Sel reached via the EXPRESSION path (a DOM builtin's object-literal target)
        // is a classified terminal type_error, never an unhandled 500. The node path's `first` stays schema-checked at
        // save time; only the uncoerced expression-path value needs this run-time guard.
        const string Payload =
            """
            { "name": "t", "config": { "backend": "input.backend" }, "vars": {},
              "steps": [ { "set": { "var": "x", "value": "exists({ css: 'tr', first: 'x' })" } } ],
              "result": "null" }
            """;

        var root = await PostAsync(Body(Payload, FakeBackendInput())); // expectedStatus defaults to 200

        root.GetProperty("status").GetString().ShouldBe("failed");
        var failure = root.GetProperty("failure");
        failure.GetProperty("class").GetString().ShouldBe("terminal");
        failure.GetProperty("code").GetString().ShouldBe("type_error");
        failure.GetProperty("atStep").GetProperty("index").GetInt32().ShouldBe(0);
        failure.GetProperty("atStep").GetProperty("kind").GetString().ShouldBe("set");
    }

    [Fact]
    public async Task Frame_handle_as_a_dom_target_is_a_terminal_failure_not_a_500()
    {
        // A frame handle bound by `frame` then used as a DOM-read TARGET (exists(fr) instead of a selector's `in`) is
        // caught only at run time, since both nodes are schema-valid and the expression is dynamically typed — a
        // classified terminal type_error, never a failed request.
        const string Payload =
            """
            { "name": "t", "config": { "backend": "input.backend" }, "vars": {},
              "steps": [ { "frame": { "var": "fr", "selector": "#some-iframe" } },
                         { "set": { "var": "x", "value": "exists(fr)" } } ],
              "result": "null" }
            """;

        var root = await PostAsync(Body(Payload, FakeBackendInput())); // expectedStatus defaults to 200

        root.GetProperty("status").GetString().ShouldBe("failed");
        var failure = root.GetProperty("failure");
        failure.GetProperty("class").GetString().ShouldBe("terminal");
        failure.GetProperty("code").GetString().ShouldBe("type_error");
        failure.GetProperty("atStep").GetProperty("index").GetInt32().ShouldBe(1);
        failure.GetProperty("atStep").GetProperty("kind").GetString().ShouldBe("set");
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

    [Fact]
    public async Task Malformed_inline_node_field_is_a_terminal_failure_not_a_500()
    {
        // An inline payload skips schema validation, so a wrong-typed node field (a numeric goto.url) is caught only at
        // run time — a classified terminal malformed_node run failure (HTTP 200 with a failure body), never an unhandled 500.
        const string Payload =
            """
            { "name": "t", "config": { "backend": "input.backend" }, "vars": {},
              "steps": [ { "goto": { "url": 5 } } ],
              "result": "null" }
            """;

        var root = await PostAsync(Body(Payload, FakeBackendInput())); // expectedStatus defaults to 200

        root.GetProperty("status").GetString().ShouldBe("failed");
        var failure = root.GetProperty("failure");
        failure.GetProperty("class").GetString().ShouldBe("terminal");
        failure.GetProperty("code").GetString().ShouldBe("malformed_node");
        failure.GetProperty("atStep").GetProperty("index").GetInt32().ShouldBe(0);
        failure.GetProperty("atStep").GetProperty("kind").GetString().ShouldBe("goto");
    }

    [Fact]
    public async Task Missing_config_is_a_terminal_failure_not_a_500()
    {
        // A schema-invalid inline payload (no config object) is caught by the run-time structural pre-pass as a classified
        // terminal malformed_node — HTTP 200 with a failure body, never a raw GetProperty("config") 500.
        var root = await PostAsync(Body("""{ "name": "t", "steps": [], "result": "null" }""", FakeBackendInput()));

        root.GetProperty("status").GetString().ShouldBe("failed");
        root.GetProperty("failure").GetProperty("code").GetString().ShouldBe("malformed_node");
    }

    [Fact]
    public async Task Malformed_inline_inputs_declaration_is_a_terminal_failure_not_a_500()
    {
        // The payload's `inputs` block is read for secretRef detection in the interpreter CONSTRUCTOR, which runs in the
        // request thread on the synchronous path BEFORE RunAsync. A declaration that is a bare string (not a `{ type }`
        // object) must classify as malformed_node — HTTP 200 with a failure body — never a raw ctor TryGetProperty 500.
        const string Payload =
            """
            { "name": "t", "config": { "backend": "input.backend" },
              "inputs": { "token": "a bare string, not a declaration object" },
              "vars": {}, "steps": [], "result": "null" }
            """;

        var root = await PostAsync(Body(Payload, FakeBackendInput()));

        root.GetProperty("status").GetString().ShouldBe("failed");
        root.GetProperty("failure").GetProperty("code").GetString().ShouldBe("malformed_node");
    }

    [Fact]
    public async Task An_async_run_with_no_config_is_accepted_not_a_500() =>
        // The deadline is read from the payload in the request thread on the async path, BEFORE the interpreter runs; a
        // missing config must fall back to the default deadline (202 Accepted), never fault that read as a 500. The run
        // then fails as malformed_node on the durable surface.
        await PostAsync(
            new JsonObject { ["payload"] = JsonNode.Parse("""{ "name": "t", "steps": [], "result": "null" }"""), ["inputs"] = FakeBackendInput(), ["async"] = true },
            expectedStatus: 202);
}
