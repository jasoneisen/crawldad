using System.Net;
using System.Text.Json;
using Crawldad.Client;
using Crawldad.Contracts.Browsers;
using Crawldad.Contracts.Fixtures;
using Crawldad.Contracts.Runs;
using Crawldad.Contracts.Webhooks;

namespace Crawldad.Tests.Client;

/// <summary>Unit tests for the webhook, fixture, and browser registration surfaces over a stub handler: register (PUT),
/// list (GET), unregister (DELETE 204), fixture record (POST, with a failed-record-run still a 200), and the request
/// shapes (verb, path, escaped name segment, normalized body).</summary>
public class CrawldadClientRegistrationsTests
{
    private static readonly RunStats _stats = new(0, 0, 0, 0, 0, 0);

    private static JsonElement JsonElementOf(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    // ----- webhooks -----

    [Fact]
    public async Task Register_webhook_puts_the_registration_and_returns_the_summary()
    {
        var summary = new WebhookSummary("prod", "https://hooks.test/x", ["run.succeeded"], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Json(summary));
        var client = ClientTestHarness.ClientFor(handler);

        var response = await client.RegisterWebhookAsync(
            "prod", new RegisterWebhookRequest("https://hooks.test/x", "secret0123456789", ["run.succeeded"]));

        response.Name.ShouldBe("prod");
        handler.Last.Method.ShouldBe(HttpMethod.Put);
        handler.Last.Path.ShouldBe("/webhooks/prod");
        using var body = JsonDocument.Parse(handler.Last.Body);
        body.RootElement.GetProperty("url").GetString().ShouldBe("https://hooks.test/x");
        body.RootElement.GetProperty("secret").GetString().ShouldBe("secret0123456789");
    }

    [Fact]
    public async Task List_webhooks_reads_the_registrations()
    {
        var summary = new WebhookSummary("prod", "https://hooks.test/x", [], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Json(new WebhookListResponse([summary])));
        var client = ClientTestHarness.ClientFor(handler);

        (await client.ListWebhooksAsync()).Webhooks.ShouldHaveSingleItem().Name.ShouldBe("prod");
        handler.Last.Path.ShouldBe("/webhooks");
    }

    [Fact]
    public async Task Unregister_webhook_deletes_and_returns_on_204()
    {
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Empty(HttpStatusCode.NoContent));
        var client = ClientTestHarness.ClientFor(handler);

        await client.UnregisterWebhookAsync("prod");

        handler.Last.Method.ShouldBe(HttpMethod.Delete);
        handler.Last.Path.ShouldBe("/webhooks/prod");
    }

    // ----- fixtures -----

    [Fact]
    public async Task Record_fixture_posts_and_returns_the_recorded_summary_on_success()
    {
        var runId = Guid.NewGuid();
        var fixture = new FixtureSummary("set", 3, 2, 4096, runId, DateTimeOffset.UnixEpoch);
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new RecordFixtureResponse(runId, RunStatus.Succeeded, fixture, JsonElementOf("null"), null, _stats)));
        var client = ClientTestHarness.ClientFor(handler);

        var response = await client.RecordFixtureAsync("set", new RecordFixtureRequest(JsonElementOf("""{ "name": "p" }"""), JsonElementOf("{}")));

        response.Status.ShouldBe(RunStatus.Succeeded);
        response.Fixture!.PageCount.ShouldBe(3);
        handler.Last.Method.ShouldBe(HttpMethod.Post);
        handler.Last.Path.ShouldBe("/fixtures/set/record");
    }

    [Fact]
    public async Task Record_fixture_surfaces_a_failed_record_run_as_a_200_response()
    {
        var runId = Guid.NewGuid();
        var failure = new RunFailureDetail("terminal", "divergence", "could not record", new RunStepRef(0, "record"));
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new RecordFixtureResponse(runId, RunStatus.Failed, null, null, failure, _stats)));
        var client = ClientTestHarness.ClientFor(handler);

        var response = await client.RecordFixtureAsync("set", new RecordFixtureRequest(JsonElementOf("{}"), JsonElementOf("{}")));

        response.Status.ShouldBe(RunStatus.Failed);
        response.Fixture.ShouldBeNull();
        response.Failure!.Code.ShouldBe("divergence");
    }

    [Fact]
    public async Task Get_fixture_reads_the_manifest()
    {
        var summary = new FixtureSummary("set", 1, 0, 100, Guid.NewGuid(), DateTimeOffset.UnixEpoch);
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new FixtureDetailResponse(summary, JsonElementOf("""{ "initial": "s0" }"""))));
        var client = ClientTestHarness.ClientFor(handler);

        var detail = await client.GetFixtureAsync("set");

        detail.Summary.Name.ShouldBe("set");
        detail.Manifest.GetProperty("initial").GetString().ShouldBe("s0");
        handler.Last.Path.ShouldBe("/fixtures/set");
    }

    [Fact]
    public async Task List_and_delete_fixtures()
    {
        var summary = new FixtureSummary("set", 1, 0, 100, Guid.NewGuid(), DateTimeOffset.UnixEpoch);
        var handler = new StubHttpMessageHandler(request =>
            request.Method == HttpMethod.Delete
                ? ClientTestHarness.Empty(HttpStatusCode.NoContent)
                : ClientTestHarness.Json(new FixtureListResponse([summary])));
        var client = ClientTestHarness.ClientFor(handler);

        (await client.ListFixturesAsync()).Fixtures.ShouldHaveSingleItem();
        await client.DeleteFixtureAsync("set");

        handler.Last.Method.ShouldBe(HttpMethod.Delete);
        handler.Last.Path.ShouldBe("/fixtures/set");
    }

    // ----- browsers -----

    [Fact]
    public async Task Register_browser_puts_the_credential_and_returns_metadata()
    {
        var summary = new BrowserSummary("prod", "browserbase", "apiKey", null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Json(summary));
        var client = ClientTestHarness.ClientFor(handler);

        var response = await client.RegisterBrowserAsync(
            "prod", new RegisterBrowserRequest("browserbase", "apiKey", "secret0123456789"));

        response.Adapter.ShouldBe("browserbase");
        handler.Last.Method.ShouldBe(HttpMethod.Put);
        handler.Last.Path.ShouldBe("/browsers/prod");
    }

    [Fact]
    public async Task List_and_unregister_browsers()
    {
        var summary = new BrowserSummary("prod", "browserless", "apiKey", null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var handler = new StubHttpMessageHandler(request =>
            request.Method == HttpMethod.Delete
                ? ClientTestHarness.Empty(HttpStatusCode.NoContent)
                : ClientTestHarness.Json(new BrowserListResponse([summary])));
        var client = ClientTestHarness.ClientFor(handler);

        (await client.ListBrowsersAsync()).Browsers.ShouldHaveSingleItem().Adapter.ShouldBe("browserless");
        await client.UnregisterBrowserAsync("prod");

        handler.Last.Method.ShouldBe(HttpMethod.Delete);
        handler.Last.Path.ShouldBe("/browsers/prod");
    }
}
