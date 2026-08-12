using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Tests.Support;
using Marten;

namespace Crawldad.Tests.Integration;

/// <summary>The tenant fixture record/replay acceptance gate (issue #74): record a search+detail session into a named,
/// tenant-scoped set through the API, replay a payload against it deterministically with zero live traffic, and
/// golden-compare the result — the exact flow an external CI job runs to gate a payload revision. Also pins the CRUD
/// surface, tenant isolation, and the classified-divergence semantics. Driven entirely through HTTP with X-Api-Key auth.</summary>
[Collection(FixtureApiCollection.Name)]
public class FixtureRecordReplayTests(FixtureApiFixture fixture) : IAsyncLifetime
{
    // The record run connects to the shipped record-search-detail "site" fixture (the stand-in for a tenant's real site).
    private const string _siteFixture = "record-search-detail";

    private IAlbaHost Host => fixture.Host;

    public Task InitializeAsync() => Host.ResetAllMartenDataAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ----- the end-to-end acceptance flow -----------------------------------

    [Fact]
    public async Task Records_a_session_then_replays_a_payload_to_its_golden()
    {
        var payload = await SearchDetailPayloadAsync();
        var golden = await GoldenAsync();

        // 1. Record the session into a named tenant set (connecting to the "site" fake). The record run's own result is
        //    the extracted data, and it equals the golden — the recording ran the real session.
        var record = await RecordAsync("accela-search", payload, SiteBinding());
        record.GetProperty("status").GetString().ShouldBe("succeeded");
        var summary = record.GetProperty("fixture");
        summary.GetProperty("name").GetString().ShouldBe("accela-search");
        summary.GetProperty("pageCount").GetInt32().ShouldBe(3);
        summary.GetProperty("transitionCount").GetInt32().ShouldBe(2);
        JsonAssert.Canonical(record.GetProperty("result")).ShouldBe(JsonAssert.Canonical(golden));

        // 2. The set is listed and its manifest inspectable (states + transitions, page HTML referenced only by hash).
        var listed = await GetJsonAsync("/fixtures");
        listed.GetProperty("fixtures").EnumerateArray().Select(f => f.GetProperty("name").GetString()).ShouldContain("accela-search");

        var detail = await GetJsonAsync("/fixtures/accela-search");
        detail.GetProperty("manifest").GetProperty("states").EnumerateObject().Count().ShouldBe(3);
        detail.GetProperty("manifest").GetProperty("transitions").GetArrayLength().ShouldBe(2);

        // 3. Replay the payload revision against the tenant set — zero live traffic — and golden-compare the result.
        var replay = await ReplayAsync(payload, "accela-search");
        replay.GetProperty("status").GetString().ShouldBe("succeeded");
        JsonAssert.Canonical(replay.GetProperty("result")).ShouldBe(JsonAssert.Canonical(golden));
    }

    [Fact]
    public async Task Lists_and_deletes_recorded_sets()
    {
        var payload = await SearchDetailPayloadAsync();
        await RecordAsync("set-one", payload, SiteBinding());
        await RecordAsync("set-two", payload, SiteBinding());

        (await GetJsonAsync("/fixtures")).GetProperty("fixtures").GetArrayLength().ShouldBe(2);

        await Host.Scenario(x =>
        {
            x.Delete.Url("/fixtures/set-one");
            x.StatusCodeShouldBe(204);
        });

        (await GetJsonAsync("/fixtures")).GetProperty("fixtures").GetArrayLength().ShouldBe(1);
        await ExpectStatusAsync("GET", "/fixtures/set-one", HttpStatusCode.NotFound);
        await ExpectStatusAsync("DELETE", "/fixtures/set-one", HttpStatusCode.NotFound); // a second delete is a plain not-found
    }

    [Fact]
    public async Task An_invalid_set_name_is_a_400()
    {
        var payload = await SearchDetailPayloadAsync();
        await ExpectStatusAsync("POST", "/fixtures/Bad_Name/record", HttpStatusCode.BadRequest, RecordBody(payload, SiteBinding()));
    }

    // ----- credential redaction ---------------------------------------------

    [Fact]
    public async Task A_credential_bearing_goto_url_is_scrubbed_before_persist_and_readback()
    {
        // A goto URL carrying a ?token= param records against the fake site (falls back to the form), then the manifest
        // GET must never echo the raw token — it is redacted exactly as the Navigated timeline event would be.
        const string secret = "SUPERSECRETTOKEN123";
        const string payload = """
        { "crawldad": "1", "name": "tokened", "inputs": { "backend": { "type": "backend", "required": true } },
          "config": { "backend": "input.backend" },
          "steps": [
            { "goto": { "url": "https://county.example/search?token=SUPERSECRETTOKEN123" } },
            { "waitForRequest": { "urlPrefix": "https://county.example/search", "method": "POST",
                "trigger": [ { "click": { "selector": "#searchBtn" } } ] } },
            { "waitFor": { "selector": "#loading", "state": "hidden" } },
            { "click": { "selector": "#detailLink" } }
          ],
          "result": "'ok'" }
        """;

        var record = await RecordAsync("tokened", JsonNode.Parse(payload)!, SiteBinding());
        record.GetProperty("status").GetString().ShouldBe("succeeded");

        var detail = await GetJsonAsync("/fixtures/tokened");
        var manifestText = detail.GetProperty("manifest").GetRawText();
        manifestText.ShouldNotContain(secret);                 // no raw token anywhere in the persisted/returned manifest
        manifestText.ShouldContain("token=[redacted]");        // redacted exactly like the Navigated event

        var initial = detail.GetProperty("manifest").GetProperty("initialState").GetString()!;
        detail.GetProperty("manifest").GetProperty("states").GetProperty(initial)
            .GetProperty("gotoUrl").GetString().ShouldBe("https://county.example/search?token=[redacted]");
    }

    // ----- classified divergence + unrecordable operations ------------------

    [Fact]
    public async Task A_replay_to_an_unrecorded_url_fails_state_miss()
    {
        await RecordAsync("accela-search", await SearchDetailPayloadAsync(), SiteBinding());

        const string diverging = """
        { "crawldad": "1", "name": "diverge", "inputs": { "backend": { "type": "backend", "required": true } },
          "config": { "backend": "input.backend" },
          "steps": [ { "goto": { "url": "https://county.example/unknown" } } ],
          "result": "'x'" }
        """;

        var run = await ReplayAsync(JsonNode.Parse(diverging)!, "accela-search");
        run.GetProperty("status").GetString().ShouldBe("failed");
        var failure = run.GetProperty("failure");
        failure.GetProperty("code").GetString().ShouldBe("fixture_state_miss");
        failure.GetProperty("message").GetString()!.ShouldContain("https://county.example/unknown");
    }

    [Fact]
    public async Task A_replay_that_clicks_off_the_recorded_path_fails_transition_miss()
    {
        await RecordAsync("accela-search", await SearchDetailPayloadAsync(), SiteBinding());

        // goto lands on the recorded form, but clicking the query input has no recorded transition — a divergence.
        const string diverging = """
        { "crawldad": "1", "name": "diverge", "inputs": { "backend": { "type": "backend", "required": true } },
          "config": { "backend": "input.backend" },
          "steps": [
            { "goto": { "url": "https://county.example/search" } },
            { "click": { "selector": "#query" } }
          ],
          "result": "'x'" }
        """;

        var run = await ReplayAsync(JsonNode.Parse(diverging)!, "accela-search");
        run.GetProperty("status").GetString().ShouldBe("failed");
        run.GetProperty("failure").GetProperty("code").GetString().ShouldBe("fixture_transition_miss");
    }

    [Fact]
    public async Task Replaying_an_unknown_set_fails_backend_unavailable()
    {
        const string payload = """
        { "crawldad": "1", "name": "probe", "inputs": { "backend": { "type": "backend", "required": true } },
          "config": { "backend": "input.backend" },
          "steps": [ { "goto": { "url": "https://county.example/search" } } ],
          "result": "'x'" }
        """;

        var run = await ReplayAsync(JsonNode.Parse(payload)!, "ghost-set");
        run.GetProperty("status").GetString().ShouldBe("failed");
        run.GetProperty("failure").GetProperty("code").GetString().ShouldBe("backend_unavailable");
    }

    [Fact]
    public async Task Replaying_without_a_fixture_set_option_fails_backend_unavailable()
    {
        const string payload = """
        { "crawldad": "1", "name": "probe", "inputs": { "backend": { "type": "backend", "required": true } },
          "config": { "backend": "input.backend" },
          "steps": [ { "goto": { "url": "https://county.example/search" } } ],
          "result": "'x'" }
        """;

        // A fixture backend binding with no options at all names nothing to replay — backend_unavailable at connect.
        var body = new JsonObject
        {
            ["payload"] = JsonNode.Parse(payload),
            ["inputs"] = new JsonObject { ["backend"] = new JsonObject { ["adapter"] = "fixture" } },
        };
        var result = await Host.Scenario(x =>
        {
            x.Post.Json(body).ToUrl("/runs");
            x.StatusCodeShouldBeOk();
        });
        var run = (await result.ReadAsJsonAsync<JsonElement>()).Clone();
        run.GetProperty("status").GetString().ShouldBe("failed");
        run.GetProperty("failure").GetProperty("code").GetString().ShouldBe("backend_unavailable");
    }

    [Fact]
    public async Task Recording_with_no_inputs_fails_and_persists_no_set()
    {
        // A record body with no inputs at all: the run resolves no backend binding and fails (no set persisted).
        const string payload = """
        { "crawldad": "1", "name": "noinputs", "inputs": { "backend": { "type": "backend", "required": true } },
          "config": { "backend": "input.backend" },
          "steps": [ { "goto": { "url": "https://county.example/search" } } ],
          "result": "'x'" }
        """;

        var body = new JsonObject { ["payload"] = JsonNode.Parse(payload) };
        var result = await Host.Scenario(x =>
        {
            x.Post.Json(body).ToUrl("/fixtures/no-inputs/record");
            x.StatusCodeShouldBeOk();
        });
        var record = (await result.ReadAsJsonAsync<JsonElement>()).Clone();
        record.GetProperty("status").GetString().ShouldBe("failed");
        await ExpectStatusAsync("GET", "/fixtures/no-inputs", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Recording_a_session_that_never_navigates_is_unrecordable()
    {
        const string noGoto = """
        { "crawldad": "1", "name": "empty", "inputs": { "backend": { "type": "backend", "required": true } },
          "config": { "backend": "input.backend" },
          "steps": [ { "comment": "connects but never navigates" } ],
          "result": "'ok'" }
        """;

        var record = await RecordAsync("empty-set", JsonNode.Parse(noGoto)!, SiteBinding());
        record.GetProperty("status").GetString().ShouldBe("failed");
        record.GetProperty("failure").GetProperty("code").GetString().ShouldBe("fixture_unrecordable");
        await ExpectStatusAsync("GET", "/fixtures/empty-set", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Recording_a_download_is_unrecordable_and_persists_no_set()
    {
        const string withDownload = """
        { "crawldad": "1", "name": "dl", "inputs": { "backend": { "type": "backend", "required": true } },
          "config": { "backend": "input.backend" },
          "steps": [
            { "goto": { "url": "https://county.example/search" } },
            { "download": { "to": "{ kind: 'fake', name: 's' }", "var": "d",
                "trigger": [ { "click": { "selector": "#searchBtn" } } ] } }
          ],
          "result": "'x'" }
        """;

        var record = await RecordAsync("attempted", JsonNode.Parse(withDownload)!, SiteBinding());
        record.GetProperty("status").GetString().ShouldBe("failed");
        record.GetProperty("failure").GetProperty("code").GetString().ShouldBe("fixture_unrecordable");
        await ExpectStatusAsync("GET", "/fixtures/attempted", HttpStatusCode.NotFound); // nothing persisted
    }

    // ----- tenant isolation --------------------------------------------------

    [Fact]
    public async Task A_recorded_set_is_invisible_to_another_tenant()
    {
        await RecordAsync("owned", await SearchDetailPayloadAsync(), SiteBinding());

        // Tenant B (a valid, different key) cannot read, list, or replay tenant A's set — it is simply absent in B's partition.
        await ExpectStatusAsync("GET", "/fixtures/owned", HttpStatusCode.NotFound, apiKey: TestTenants.SecondaryKey);
        var bList = await GetJsonAsync("/fixtures", TestTenants.SecondaryKey);
        bList.GetProperty("fixtures").GetArrayLength().ShouldBe(0);

        var replay = await ReplayAsync(await SearchDetailPayloadAsync(), "owned", TestTenants.SecondaryKey);
        replay.GetProperty("status").GetString().ShouldBe("failed");
        replay.GetProperty("failure").GetProperty("code").GetString().ShouldBe("backend_unavailable");
    }

    // ----- helpers -----------------------------------------------------------

    private static async Task<JsonNode> SearchDetailPayloadAsync() =>
        JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Runner.FixturesRoot, "Payloads", "record-search-detail.json")))!;

    private static async Task<JsonElement> GoldenAsync()
    {
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Runner.FixturesRoot, _siteFixture, "golden.json")));
        return doc.RootElement.Clone();
    }

    // The record run's backend: the fake serving the shipped "site" fixture (a tenant records against their real backend).
    private static JsonObject SiteBinding() =>
        new() { ["adapter"] = "fake", ["options"] = new JsonObject { ["fixture"] = _siteFixture } };

    private static JsonObject RecordBody(JsonNode payload, JsonObject backend) =>
        new() { ["payload"] = payload.DeepClone(), ["inputs"] = new JsonObject { ["backend"] = backend } };

    private async Task<JsonElement> RecordAsync(string name, JsonNode payload, JsonObject backend, string apiKey = TestTenants.PrimaryKey)
    {
        var result = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(apiKey));
            x.Post.Json(RecordBody(payload, backend)).ToUrl($"/fixtures/{name}/record");
            x.StatusCodeShouldBeOk();
        });
        return (await result.ReadAsJsonAsync<JsonElement>()).Clone();
    }

    private async Task<JsonElement> ReplayAsync(JsonNode payload, string fixtureSet, string apiKey = TestTenants.PrimaryKey)
    {
        var body = new JsonObject
        {
            ["payload"] = payload.DeepClone(),
            ["inputs"] = new JsonObject
            {
                ["backend"] = new JsonObject
                {
                    ["adapter"] = "fixture",
                    ["options"] = new JsonObject { ["fixtureSet"] = fixtureSet },
                },
            },
        };

        var result = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(apiKey));
            x.Post.Json(body).ToUrl("/runs");
            x.StatusCodeShouldBeOk();
        });
        return (await result.ReadAsJsonAsync<JsonElement>()).Clone();
    }

    private async Task<JsonElement> GetJsonAsync(string url, string apiKey = TestTenants.PrimaryKey)
    {
        var result = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(apiKey));
            x.Get.Url(url);
            x.StatusCodeShouldBeOk();
        });
        return (await result.ReadAsJsonAsync<JsonElement>()).Clone();
    }

    private async Task ExpectStatusAsync(string method, string url, HttpStatusCode expected, JsonObject? body = null, string apiKey = TestTenants.PrimaryKey) =>
        await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(apiKey));
            switch (method)
            {
                case "GET":
                    x.Get.Url(url);
                    break;
                case "DELETE":
                    x.Delete.Url(url);
                    break;
                default:
                    x.Post.Json(body!).ToUrl(url);
                    break;
            }

            x.StatusCodeShouldBe((int)expected);
        });
}
