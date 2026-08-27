using System.Globalization;
using System.Text.Json;
using Alba;
using Crawldad.Api.Features.Runs;
using Crawldad.Api.Features.Webhooks;
using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;

namespace Crawldad.Tests.Integration;

/// <summary>Durable delivery through the real Wolverine pipeline (with the recording sender in place of the network): a
/// terminal run delivers a signed body, a failing receiver is retried with backoff, a persistently-failing one is
/// abandoned at the attempt cap, a delivery for a deregistered endpoint is dropped, and an async run reaching terminal
/// fires the notification via the executor's trigger.</summary>
[Collection(WebhookApiCollection.Name)]
public sealed class WebhookDeliveryTests(WebhookApiFixture fixture) : IAsyncLifetime
{
    private static readonly CancellationToken _ct = CancellationToken.None;
    private IAlbaHost Host => fixture.Host;
    private RecordingWebhookSender Sender => fixture.Sender;

    public Task InitializeAsync() => Host.ResetAllMartenDataAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SubscribeAsync(string name, string secret) =>
        await Host.Services.GetRequiredService<IWebhookEndpointStore>()
            .RegisterAsync(TestTenants.PrimaryId, name, $"https://hooks.example.com/{name}", secret, [], _ct);

    private async Task PublishAsync(object message)
    {
        await using var scope = Host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().PublishAsync(message, new DeliveryOptions { TenantId = TestTenants.PrimaryId });
    }

    private int CallsTo(string name) => Sender.Calls.Count(c => c.Url.EndsWith("/" + name, StringComparison.Ordinal));

    [Fact]
    public async Task Delivers_a_signed_terminal_event_through_the_pipeline()
    {
        Sender.AlwaysDeliver();
        var runId = Guid.NewGuid();
        const string secret = "whsec_pipeline_0123456789";
        var store = Host.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession(TestTenants.PrimaryId))
        {
            session.Events.StartStream<Run>(runId,
                new RunStarted("demo", "h", FakeClock.Fixed, [], null, null),
                new RunSucceeded(new RunStats(5, 1, 1, 0, 0, 0), FakeClock.Fixed.AddSeconds(1)));
            await session.SaveChangesAsync(_ct);
        }

        await SubscribeAsync("prod", secret);
        await PublishAsync(new RunFinalized(runId));

        await WebhookTesting.PollAsync(() => CallsTo("prod") >= 1, "no delivery arrived");

        var call = Sender.Calls.First(c => c.Url.EndsWith("/prod", StringComparison.Ordinal));
        var timestamp = long.Parse(call.Headers["X-Crawldad-Timestamp"], CultureInfo.InvariantCulture);
        call.Headers["X-Crawldad-Signature"].ShouldBe(WebhookSignature.Compute(secret, timestamp, call.Body)); // verifies the documented recipe
        call.Headers["X-Crawldad-Event"].ShouldBe("run.succeeded");
        var body = JsonDocument.Parse(call.Body).RootElement;
        body.GetProperty("runId").GetGuid().ShouldBe(runId);
        body.GetProperty("type").GetString().ShouldBe("run.succeeded");
    }

    [Fact]
    public async Task Retries_with_backoff_then_succeeds()
    {
        Sender.FailThenDeliver(1); // fail the first attempt, accept the retry
        await SubscribeAsync("retry-hook", "whsec_retry_0123456789");

        await PublishAsync(new DeliverWebhook("retry-hook", "run.succeeded", "evt-r", "{\"id\":\"evt-r\"}", 1, Guid.NewGuid()));

        await WebhookTesting.PollAsync(() => CallsTo("retry-hook") >= 2, "the retry never delivered");
        await Task.Delay(400); // let any further (unexpected) retry fire
        CallsTo("retry-hook").ShouldBe(2); // one failure + one successful retry, then it stops
    }

    [Fact]
    public async Task Abandons_delivery_after_the_attempt_cap()
    {
        Sender.AlwaysFail(500); // the fixture caps attempts at 3
        await SubscribeAsync("giveup-hook", "whsec_giveup_0123456789");

        await PublishAsync(new DeliverWebhook("giveup-hook", "run.failed", "evt-g", "{}", 1, Guid.NewGuid()));

        await WebhookTesting.PollAsync(() => CallsTo("giveup-hook") >= 3, "did not reach the attempt cap");
        await Task.Delay(500); // no 4th attempt should fire
        CallsTo("giveup-hook").ShouldBe(3);
    }

    [Fact]
    public async Task Drops_a_delivery_for_a_deregistered_endpoint()
    {
        Sender.AlwaysDeliver();

        await PublishAsync(new DeliverWebhook("ghost-hook", "run.succeeded", "evt-x", "{}", 1, Guid.NewGuid()));

        await Task.Delay(600); // give the pipeline time to resolve (and not send)
        CallsTo("ghost-hook").ShouldBe(0);
    }

    [Fact]
    public async Task An_async_run_reaching_terminal_delivers_via_the_executor_trigger()
    {
        Sender.AlwaysDeliver();
        await SubscribeAsync("run-hook", "whsec_run_0123456789");

        // A no-backend async run: it is admitted, runs, and fails fast (invalid_backend_binding) through the executor —
        // exercising the ExecuteRunHandler trigger that publishes RunFinalized.
        var accepted = await Host.Scenario(x =>
        {
            x.Post.Json(new
            {
                payload = JsonDocument.Parse("""{ "crawldad": "1", "config": { "backend": "input.backend", "deadlineMs": 30000 }, "steps": [], "result": "'x'" }""").RootElement,
                async = true,
            }).ToUrl("/runs");
            x.StatusCodeShouldBe(202);
        });
        var runId = (await accepted.ReadAsJsonAsync<JsonElement>()).GetProperty("runId").GetGuid();
        await DurableHost.PollUntilTerminalAsync(Host, runId, DurableHost.PollTimeout);

        await WebhookTesting.PollAsync(() => CallsTo("run-hook") >= 1, "the terminal run did not deliver a webhook");
        var body = JsonDocument.Parse(Sender.Calls.First(c => c.Url.EndsWith("/run-hook", StringComparison.Ordinal)).Body).RootElement;
        body.GetProperty("runId").GetGuid().ShouldBe(runId);
        body.GetProperty("type").GetString().ShouldBe("run.failed");
    }
}
