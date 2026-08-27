using System.Text.Json;
using Alba;
using Crawldad.Api.Features.Runs;
using Crawldad.Api.Features.Webhooks;
using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>The fan-out handler in the real store, deterministically (no durable-queue timing): it folds a run's stream
/// into the ref-only envelope, filters to the endpoints subscribed to that terminal type, and cascades one delivery each.
/// Exercises every terminal shape (succeeded/failed/cancelled), both stream openers (started/queued), the pinned-payload
/// identity vs an inline run, and the no-subscribers / erased-run no-ops.</summary>
[Collection(WebhookApiCollection.Name)]
public sealed class WebhookFanoutTests(WebhookApiFixture fixture) : IAsyncLifetime
{
    private static readonly CancellationToken _ct = CancellationToken.None;
    private static readonly RunStats _stats = new(12, 3, 4, 1, 0, 2);
    private static readonly DateTimeOffset _finishedAt = FakeClock.Fixed.AddSeconds(5);

    private IAlbaHost Host => fixture.Host;
    private IDocumentStore Store => Host.Services.GetRequiredService<IDocumentStore>();
    private IWebhookEndpointStore Webhooks => Host.Services.GetRequiredService<IWebhookEndpointStore>();

    public Task InitializeAsync() => Host.ResetAllMartenDataAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedRunAsync(Guid runId, object opener, object terminal)
    {
        await using var session = Store.LightweightSession(TestTenants.PrimaryId);
        session.Events.StartStream<Run>(runId, opener, terminal);
        await session.SaveChangesAsync(_ct);
    }

    private async Task SubscribeAsync(string name, params string[] events) =>
        await Webhooks.RegisterAsync(TestTenants.PrimaryId, name, $"https://hooks.example.com/{name}", "whsec_value_0123456789", events, _ct);

    private async Task<IReadOnlyList<DeliverWebhook>> FanOutAsync(Guid runId)
    {
        await using var session = Store.LightweightSession(TestTenants.PrimaryId);
        var outgoing = await RunFinalizedHandler.Handle(new RunFinalized(runId), session, Webhooks, _ct);
        return [.. outgoing.OfType<DeliverWebhook>()];
    }

    [Fact]
    public async Task Fans_out_a_succeeded_run_to_matching_subscribers_only()
    {
        var runId = Guid.NewGuid();
        var payloadId = Guid.NewGuid();
        await SeedRunAsync(runId,
            new RunStarted("ljcmg.canary", "hash123", FakeClock.Fixed, [], payloadId, 4),
            new RunSucceeded(_stats, _finishedAt));
        await SubscribeAsync("all");                    // empty = all events
        await SubscribeAsync("failonly", "run.failed"); // must NOT receive a success

        var deliveries = await FanOutAsync(runId);

        deliveries.Count.ShouldBe(1);
        deliveries[0].EndpointName.ShouldBe("all");
        deliveries[0].EventType.ShouldBe("run.succeeded");

        var body = JsonDocument.Parse(deliveries[0].Body).RootElement;
        body.GetProperty("type").GetString().ShouldBe("run.succeeded");
        body.GetProperty("runId").GetGuid().ShouldBe(runId);
        body.GetProperty("status").GetString().ShouldBe("succeeded");
        body.GetProperty("payloadId").GetGuid().ShouldBe(payloadId);
        body.GetProperty("revision").GetInt32().ShouldBe(4);
        body.GetProperty("stats").GetProperty("selectorMisses").GetInt32().ShouldBe(2);
        body.GetProperty("finishedAt").GetDateTimeOffset().ShouldBe(_finishedAt);
        body.TryGetProperty("failure", out _).ShouldBeFalse(); // omitted for a success
        body.GetProperty("id").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_failed_run_carries_the_typed_failure()
    {
        var runId = Guid.NewGuid();
        var failure = new RunFailureDetail("terminal", "backend_unavailable", "boom", new RunStepRef(0, "config"));
        await SeedRunAsync(runId,
            new RunStarted("inline", "hash", FakeClock.Fixed, [], null, null),
            new RunFailed(failure, _stats, _finishedAt));
        await SubscribeAsync("all");

        var deliveries = await FanOutAsync(runId);

        deliveries.Count.ShouldBe(1);
        deliveries[0].EventType.ShouldBe("run.failed");
        var body = JsonDocument.Parse(deliveries[0].Body).RootElement;
        body.GetProperty("status").GetString().ShouldBe("failed");
        body.GetProperty("failure").GetProperty("code").GetString().ShouldBe("backend_unavailable");
        body.TryGetProperty("payloadId", out _).ShouldBeFalse(); // inline run — no pinned identity
        body.TryGetProperty("revision", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_cancelled_queued_run_takes_its_identity_from_the_queued_opener()
    {
        var runId = Guid.NewGuid();
        var payloadId = Guid.NewGuid();
        await SeedRunAsync(runId,
            new RunQueued("ljcmg.canary", "hash", FakeClock.Fixed, [], payloadId, 2),
            new RunCancelled(_stats, _finishedAt));
        await SubscribeAsync("all");

        var deliveries = await FanOutAsync(runId);

        deliveries.Count.ShouldBe(1);
        deliveries[0].EventType.ShouldBe("run.cancelled");
        var body = JsonDocument.Parse(deliveries[0].Body).RootElement;
        body.GetProperty("status").GetString().ShouldBe("cancelled");
        body.GetProperty("payloadId").GetGuid().ShouldBe(payloadId);
        body.GetProperty("revision").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task A_run_with_no_subscribers_yields_no_deliveries()
    {
        var runId = Guid.NewGuid();
        await SeedRunAsync(runId, new RunStarted("x", "h", FakeClock.Fixed, [], null, null), new RunSucceeded(_stats, _finishedAt));

        (await FanOutAsync(runId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unknown_or_erased_run_yields_no_deliveries()
    {
        await SubscribeAsync("all"); // subscribed, but the run stream does not exist

        (await FanOutAsync(Guid.NewGuid())).ShouldBeEmpty();
    }
}
