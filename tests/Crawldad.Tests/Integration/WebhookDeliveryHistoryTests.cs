using System.Text.Json;
using Alba;
using Crawldad.Api.Features.Runs;
using Crawldad.Api.Features.Webhooks;
using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;

namespace Crawldad.Tests.Integration;

/// <summary>The webhook delivery-history surface (issue #119): each delivery attempt is persisted from the real delivery
/// handler, capped per endpoint, exposed at GET /webhooks/{name}/deliveries (newest first), and summarised as lastDelivery
/// on the webhook listing. Runs on the webhooks-API host (the recording sender in place of the network).</summary>
[Collection(WebhookApiCollection.Name)]
public sealed class WebhookDeliveryHistoryTests(WebhookApiFixture fixture) : IAsyncLifetime
{
    private static readonly CancellationToken _ct = CancellationToken.None;
    private IAlbaHost Host => fixture.Host;
    private RecordingWebhookSender Sender => fixture.Sender;
    private IDocumentStore Store => Host.Services.GetRequiredService<IDocumentStore>();

    public Task InitializeAsync() => Host.ResetAllMartenDataAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SubscribeAsync(string name) =>
        await Host.Services.GetRequiredService<IWebhookEndpointStore>()
            .RegisterAsync(TestTenants.PrimaryId, name, $"https://hooks.example.com/{name}", "whsec_history_0123456789", [], _ct);

    private async Task<Guid> SeedTerminalRunAsync()
    {
        var runId = Guid.NewGuid();
        await using var session = Store.LightweightSession(TestTenants.PrimaryId);
        session.Events.StartStream<Run>(runId,
            new RunStarted("demo", "h", FakeClock.Fixed, [], null, null),
            new RunSucceeded(new RunStats(5, 1, 1, 0, 0, 0), FakeClock.Fixed.AddSeconds(1)));
        await session.SaveChangesAsync(_ct);
        return runId;
    }

    private async Task PublishAsync(object message)
    {
        await using var scope = Host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().PublishAsync(message, new DeliveryOptions { TenantId = TestTenants.PrimaryId });
    }

    private async Task<JsonElement> GetAsync(string url)
    {
        var result = await Host.Scenario(x =>
        {
            x.Get.Url(url);
            x.StatusCodeShouldBeOk();
        });
        return (await result.ReadAsJsonAsync<JsonElement>())!;
    }

    private int CallsTo(string name) => Sender.Calls.Count(c => c.Url.EndsWith("/" + name, StringComparison.Ordinal));

    // Waits until at least `atLeast` delivery rows are committed for the endpoint — the delivery record commits in the
    // handler's transaction, slightly after the recording sender saw the call, so poll the store, not just the call count.
    private async Task WaitForDeliveriesAsync(string name, int atLeast)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            await using (var read = Store.QuerySession(TestTenants.PrimaryId))
            {
                if (await read.Query<WebhookDelivery>().CountAsync(d => d.EndpointName == name, _ct) >= atLeast)
                {
                    return;
                }
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"fewer than {atLeast} deliveries recorded for {name}");
    }

    [Fact]
    public async Task Persists_a_delivery_and_surfaces_it_on_the_endpoint_and_the_listing()
    {
        Sender.AlwaysDeliver();
        var runId = await SeedTerminalRunAsync();
        await SubscribeAsync("prod");
        await PublishAsync(new RunFinalized(runId));

        await WebhookTesting.PollAsync(() => CallsTo("prod") >= 1, "no delivery arrived");
        await WaitForDeliveriesAsync("prod", 1);

        var deliveries = (await GetAsync("/webhooks/prod/deliveries")).GetProperty("deliveries");
        var row = deliveries[0];
        row.GetProperty("runId").GetGuid().ShouldBe(runId);
        row.GetProperty("eventType").GetString().ShouldBe("run.succeeded");
        row.GetProperty("attempt").GetInt32().ShouldBe(1);
        row.GetProperty("delivered").GetBoolean().ShouldBeTrue();
        row.GetProperty("statusCode").GetInt32().ShouldBe(200);
        row.GetProperty("latencyMs").GetInt64().ShouldBeGreaterThanOrEqualTo(0);

        // The webhook listing carries the same outcome as lastDelivery.
        var listed = (await GetAsync("/webhooks")).GetProperty("webhooks")[0];
        var last = listed.GetProperty("lastDelivery");
        last.GetProperty("runId").GetGuid().ShouldBe(runId);
        last.GetProperty("delivered").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Records_every_attempt_of_a_retried_delivery()
    {
        Sender.FailThenDeliver(1); // attempt 1 fails (503), attempt 2 delivers
        var runId = await SeedTerminalRunAsync();
        await SubscribeAsync("retry");
        await PublishAsync(new RunFinalized(runId));

        await WebhookTesting.PollAsync(() => CallsTo("retry") >= 2, "the retry never delivered");
        await WaitForDeliveriesAsync("retry", 2);

        var deliveries = (await GetAsync("/webhooks/retry/deliveries")).GetProperty("deliveries").EnumerateArray().ToList();
        deliveries.Count.ShouldBe(2);
        deliveries.ShouldContain(d => d.GetProperty("attempt").GetInt32() == 1 && !d.GetProperty("delivered").GetBoolean() && d.GetProperty("statusCode").GetInt32() == 503);
        deliveries.ShouldContain(d => d.GetProperty("attempt").GetInt32() == 2 && d.GetProperty("delivered").GetBoolean());
    }

    [Fact]
    public async Task A_transport_failure_records_no_status_code()
    {
        Sender.AlwaysFail(null); // a connection failure — no HTTP status; the fixture caps attempts at 3
        var runId = await SeedTerminalRunAsync();
        await SubscribeAsync("down");
        await PublishAsync(new RunFinalized(runId));

        await WebhookTesting.PollAsync(() => CallsTo("down") >= 3, "did not reach the attempt cap");
        await WaitForDeliveriesAsync("down", 1);

        var row = (await GetAsync("/webhooks/down/deliveries")).GetProperty("deliveries")[0];
        row.GetProperty("delivered").GetBoolean().ShouldBeFalse();
        row.TryGetProperty("statusCode", out _).ShouldBeFalse(); // a transport fault omits the status
    }

    [Fact]
    public async Task Deliveries_are_404_for_an_unknown_endpoint()
    {
        await Host.Scenario(x =>
        {
            x.Get.Url("/webhooks/ghost/deliveries");
            x.StatusCodeShouldBe(StatusCodes.Status404NotFound);
        });
    }

    [Fact]
    public async Task Deliveries_honour_the_limit_query()
    {
        await SubscribeAsync("limited");
        var store = Host.Services.GetRequiredService<IWebhookDeliveryStore>();
        for (var i = 0; i < 3; i++)
        {
            await using var session = Store.LightweightSession(TestTenants.PrimaryId);
            await store.RecordAsync(session, new WebhookDelivery
            {
                Id = Guid.NewGuid(),
                EndpointName = "limited",
                RunId = Guid.NewGuid(),
                EventType = "run.succeeded",
                Attempt = 1,
                Delivered = true,
                StatusCode = 200,
                LatencyMs = 1,
                At = FakeClock.Fixed.AddMinutes(i),
            }, maxPerEndpoint: 50, _ct);
            await session.SaveChangesAsync(_ct);
        }

        (await GetAsync("/webhooks/limited/deliveries?limit=2")).GetProperty("deliveries").GetArrayLength().ShouldBe(2); // narrowed
        (await GetAsync("/webhooks/limited/deliveries")).GetProperty("deliveries").GetArrayLength().ShouldBe(3);         // default = the cap
    }

    [Fact]
    public async Task The_delivery_log_is_capped_per_endpoint_keeping_the_newest()
    {
        var store = Host.Services.GetRequiredService<IWebhookDeliveryStore>();

        // Record six attempts for one endpoint with a cap of three, committing each (each real delivery is its own
        // handler transaction), with strictly increasing timestamps so "newest" is well-defined.
        for (var i = 0; i < 6; i++)
        {
            await using var session = Store.LightweightSession(TestTenants.PrimaryId);
            await store.RecordAsync(session, new WebhookDelivery
            {
                Id = Guid.NewGuid(),
                EndpointName = "capped",
                RunId = Guid.NewGuid(),
                EventType = "run.succeeded",
                Attempt = i + 1,
                Delivered = true,
                StatusCode = 200,
                LatencyMs = 3,
                At = FakeClock.Fixed.AddMinutes(i),
            }, maxPerEndpoint: 3, _ct);
            await session.SaveChangesAsync(_ct);
        }

        await using var read = Store.QuerySession(TestTenants.PrimaryId);
        var rows = await read.Query<WebhookDelivery>().Where(d => d.EndpointName == "capped").ToListAsync(_ct);
        rows.Count.ShouldBe(3); // capped
        rows.Select(r => r.Attempt).OrderBy(a => a).ShouldBe([4, 5, 6]); // the three newest kept
    }
}
