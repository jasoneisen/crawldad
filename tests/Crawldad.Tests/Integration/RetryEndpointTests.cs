using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Runs;
using Crawldad.Web.Infrastructure.Browser;
using Crawldad.Web.Infrastructure.Browser.Fake;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>
/// The retry/resilience layer end-to-end through <c>POST /runs</c> (§8.3): timeout retried into success with
/// <c>RunAttemptFailed</c> events persisted, the §3.6 page-crash reopen-and-rebind, retryable exhaustion, and a
/// terminal guard that is never retried. Delays run at 0ms through the fixture's frozen clock, so the suite stays fast.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class RetryEndpointTests(AppFixture fixture)
{
    private IAlbaHost Host => fixture.Host;

    private static string RetryPayload(string retry) =>
        $$"""
        { "name": "retry.demo", "config": { "backend": "input.backend", "retry": {{retry}} },
          "steps": [ { "goto": { "url": "https://fixture.test/form" } }, { "click": { "selector": "#go" } } ],
          "result": "exists('#done')" }
        """;

    private static JsonObject Body(string payloadJson, string fixtureName) => new()
    {
        ["payload"] = JsonNode.Parse(payloadJson),
        ["inputs"] = new JsonObject
        {
            ["backend"] = new JsonObject
            {
                ["adapter"] = "fake",
                ["options"] = new JsonObject { ["fixture"] = fixtureName },
            },
        },
    };

    private async Task<JsonElement> PostAsync(JsonObject body)
    {
        var result = await Host.Scenario(x =>
        {
            x.Post.Json(body).ToUrl("/runs");
            x.StatusCodeShouldBe(200);
        });
        return await result.ReadAsJsonAsync<JsonElement>();
    }

    private async Task<IReadOnlyList<Type>> StreamEventTypesAsync(Guid runId)
    {
        var store = Host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(TestTenants.PrimaryId);
        var events = await session.Events.FetchStreamAsync(runId);
        return [.. events.Select(e => e.EventType)];
    }

    [Fact]
    public async Task Injected_timeout_is_retried_into_success_with_attempt_events_persisted()
    {
        var root = await PostAsync(Body(
            RetryPayload("""{ "maxAttempts": 5, "delayMs": 0, "backoff": "constant", "retryOn": ["timeout","pageCrashed"], "onPageCrashed": "reopenPage" }"""),
            "inject-timeout"));

        root.GetProperty("status").GetString().ShouldBe("succeeded");
        root.GetProperty("result").GetBoolean().ShouldBeTrue();

        var runId = root.GetProperty("runId").GetGuid();
        (await StreamEventTypesAsync(runId)).ShouldBe(
            [typeof(RunStarted), typeof(RunAttemptFailed), typeof(RunAttemptFailed), typeof(RunSucceeded)]);
    }

    [Fact]
    public async Task Injected_page_crash_reopens_and_rebinds_on_the_same_session()
    {
        var root = await PostAsync(Body(
            RetryPayload("""{ "maxAttempts": 3, "delayMs": 0, "retryOn": ["timeout","pageCrashed"], "onPageCrashed": "reopenPage" }"""),
            "inject-crash"));

        root.GetProperty("status").GetString().ShouldBe("succeeded");

        var runId = root.GetProperty("runId").GetGuid();
        (await StreamEventTypesAsync(runId)).ShouldBe([typeof(RunStarted), typeof(RunAttemptFailed), typeof(RunSucceeded)]);

        // The keyed fake is a singleton; right after this (sequential) run its LastSession is ours.
        var backend = (FakeBrowserBackend)Host.Services.GetRequiredKeyedService<IBrowserBackend>("fake");
        var session = backend.LastSession!;
        session.Pages.Count.ShouldBe(2);                 // original + one reopen on the SAME session (no reconnect)
        session.Pages[0].CloseAttempted.ShouldBeTrue();  // the crashed page was closed
    }

    [Fact]
    public async Task Unconditional_timeout_exhausts_to_a_retryable_exhausted_failure()
    {
        var root = await PostAsync(Body(
            RetryPayload("""{ "maxAttempts": 2, "delayMs": 0, "retryOn": ["timeout","pageCrashed"] }"""),
            "inject-timeout"));

        root.GetProperty("status").GetString().ShouldBe("failed");
        var failure = root.GetProperty("failure");
        failure.GetProperty("class").GetString().ShouldBe("retryable-exhausted");
        failure.GetProperty("code").GetString().ShouldBe("timeout");
    }

    [Fact]
    public async Task A_terminal_guard_is_not_retried()
    {
        var root = await PostAsync(Body(
            """
            { "name": "guard.demo", "config": { "backend": "input.backend", "retry": { "maxAttempts": 5, "delayMs": 0, "retryOn": ["timeout","pageCrashed"] } },
              "steps": [ { "goto": { "url": "https://fixture.test/form" } },
                         { "guard": { "cond": "false", "elseFail": { "class": "terminal", "code": "record_not_accessible", "message": "gone" } } } ],
              "result": "null" }
            """,
            "inject-timeout"));

        root.GetProperty("status").GetString().ShouldBe("failed");
        root.GetProperty("failure").GetProperty("class").GetString().ShouldBe("terminal");

        var runId = root.GetProperty("runId").GetGuid();
        // No RunAttemptFailed between RunStarted and RunFailed ⇒ exactly one attempt.
        (await StreamEventTypesAsync(runId)).ShouldBe([typeof(RunStarted), typeof(RunFailed)]);
    }
}
