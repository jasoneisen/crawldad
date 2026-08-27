using System.Globalization;
using Crawldad.Api.Features.Webhooks;
using Crawldad.Contracts.Webhooks;
using Crawldad.Tests.Support;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Crawldad.Tests.Unit;

/// <summary>The delivery handler in isolation (no host, no network): it resolves the endpoint, signs the body under a
/// fresh timestamp, POSTs via the sender seam, records the attempt into the delivery-history store, and — deterministically,
/// by the returned cascade — either stops (success, drop, or exhaustion) or schedules the next attempt with the
/// exponential-backoff delay.</summary>
public class DeliverWebhookHandlerTests
{
    private static readonly ResolvedWebhookEndpoint _endpoint = new("https://hooks.example.com/x", "whsec_0123456789abcdef", []);
    private static readonly Guid _runId = Guid.NewGuid();

    private static DeliverWebhook Message(int attempt, string body = "{\"id\":\"evt-1\"}") =>
        new("prod", "run.succeeded", "evt-1", body, attempt, _runId);

    private static IOptions<WebhookOptions> Options(int maxAttempts = 8, double baseSecs = 10, double maxSecs = 300) =>
        Microsoft.Extensions.Options.Options.Create(new WebhookOptions
        {
            Delivery = new WebhookDeliveryOptions
            {
                MaxAttempts = maxAttempts,
                BaseDelay = TimeSpan.FromSeconds(baseSecs),
                MaxDelay = TimeSpan.FromSeconds(maxSecs),
                Timeout = TimeSpan.FromSeconds(10),
            },
            DeliveryHistory = new WebhookDeliveryHistoryOptions { MaxPerEndpoint = 7 },
        });

    private static Task<OutgoingMessages> HandleAsync(DeliverWebhook message, IWebhookEndpointStore store, IWebhookSender sender, IWebhookDeliveryStore deliveries, IOptions<WebhookOptions>? options = null) =>
        DeliverWebhookHandler.Handle(message, null!, store, deliveries, sender, options ?? Options(), new FakeClock(), NullLogger<DeliverWebhook>.Instance, CancellationToken.None);

    [Fact]
    public async Task Drops_when_the_endpoint_was_deregistered()
    {
        var sender = new RecordingWebhookSender();
        var deliveries = new RecordingDeliveryStore();

        var outgoing = await HandleAsync(Message(1), new StubStore(null), sender, deliveries);

        outgoing.ShouldBeEmpty();
        sender.CallCount.ShouldBe(0);   // never even attempted
        deliveries.Records.ShouldBeEmpty(); // and nothing recorded — no attempt was made
    }

    [Fact]
    public async Task Delivers_and_signs_then_stops()
    {
        var sender = new RecordingWebhookSender();
        sender.AlwaysDeliver();
        var deliveries = new RecordingDeliveryStore();

        var outgoing = await HandleAsync(Message(1), new StubStore(_endpoint), sender, deliveries);

        outgoing.ShouldBeEmpty(); // accepted — no retry
        sender.CallCount.ShouldBe(1);
        var call = sender.Last!;
        call.Url.ShouldBe(_endpoint.Url);
        call.Headers["X-Crawldad-Event"].ShouldBe("run.succeeded");
        call.Headers["X-Crawldad-Delivery"].ShouldBe("evt-1");
        var timestamp = FakeClock.Fixed.ToUnixTimeSeconds();
        call.Headers["X-Crawldad-Timestamp"].ShouldBe(timestamp.ToString(CultureInfo.InvariantCulture));
        call.Headers["X-Crawldad-Signature"].ShouldBe(WebhookSignature.Compute(_endpoint.Secret, timestamp, call.Body));

        // The success is recorded, with the run id and the configured retention cap threaded through.
        var record = deliveries.Records.ShouldHaveSingleItem();
        record.EndpointName.ShouldBe("prod");
        record.RunId.ShouldBe(_runId);
        record.EventType.ShouldBe("run.succeeded");
        record.Attempt.ShouldBe(1);
        record.Delivered.ShouldBeTrue();
        record.StatusCode.ShouldBe(200);
        record.LatencyMs.ShouldBeGreaterThanOrEqualTo(0);
        deliveries.LastCap.ShouldBe(7);
    }

    [Fact]
    public async Task Schedules_a_backed_off_retry_on_failure()
    {
        var sender = new RecordingWebhookSender();
        sender.AlwaysFail(503);
        var deliveries = new RecordingDeliveryStore();

        var outgoing = await HandleAsync(Message(2), new StubStore(_endpoint), sender, deliveries, Options(maxAttempts: 5, baseSecs: 2, maxSecs: 60));

        var retry = outgoing.OfType<DeliveryMessage<DeliverWebhook>>().Single();
        retry.Message.Attempt.ShouldBe(3);
        retry.Options.ScheduleDelay.ShouldBe(TimeSpan.FromSeconds(4)); // base 2s * 2^(2-1)

        // The failed attempt is recorded (a will-retry non-delivery), so the log shows the whole retry ladder.
        var record = deliveries.Records.ShouldHaveSingleItem();
        record.Attempt.ShouldBe(2);
        record.Delivered.ShouldBeFalse();
        record.StatusCode.ShouldBe(503);
    }

    [Fact]
    public async Task Gives_up_after_the_attempt_cap()
    {
        var sender = new RecordingWebhookSender();
        sender.AlwaysFail(500);
        var deliveries = new RecordingDeliveryStore();

        var outgoing = await HandleAsync(Message(3), new StubStore(_endpoint), sender, deliveries, Options(maxAttempts: 3));

        outgoing.ShouldBeEmpty(); // attempt 3 == cap → abandoned, no further retry
        sender.CallCount.ShouldBe(1);

        // The final, abandoned attempt is still recorded.
        var record = deliveries.Records.ShouldHaveSingleItem();
        record.Attempt.ShouldBe(3);
        record.Delivered.ShouldBeFalse();
        record.StatusCode.ShouldBe(500);
    }

    [Fact]
    public async Task Records_a_transport_failure_with_no_status()
    {
        var sender = new RecordingWebhookSender();
        sender.AlwaysFail(null); // a connection failure / timeout — no HTTP status
        var deliveries = new RecordingDeliveryStore();

        await HandleAsync(Message(1), new StubStore(_endpoint), sender, deliveries, Options(maxAttempts: 3));

        var record = deliveries.Records.ShouldHaveSingleItem();
        record.Delivered.ShouldBeFalse();
        record.StatusCode.ShouldBeNull(); // a transport fault produced no response
    }

    // Captures every recorded delivery without touching a session, so the handler's recording is unit-testable with no host.
    private sealed class RecordingDeliveryStore : IWebhookDeliveryStore
    {
        public List<WebhookDelivery> Records { get; } = [];

        public int? LastCap { get; private set; }

        public Task RecordAsync(IDocumentSession session, WebhookDelivery record, int maxPerEndpoint, CancellationToken ct)
        {
            Records.Add(record);
            LastCap = maxPerEndpoint;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WebhookDeliveryItem>> RecentAsync(IQuerySession session, string endpointName, int limit, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, WebhookDeliverySummary>> LatestPerEndpointAsync(IQuerySession session, CancellationToken ct) => throw new NotSupportedException();
    }

    // Resolves to a fixed endpoint (or null); the CRUD methods are never reached on the delivery path.
    private sealed class StubStore(ResolvedWebhookEndpoint? resolved) : IWebhookEndpointStore
    {
        public Task<ResolvedWebhookEndpoint?> ResolveAsync(IQuerySession session, string name, CancellationToken ct) => Task.FromResult(resolved);

        public Task<WebhookSummary> RegisterAsync(string tenant, string name, string url, string secret, IReadOnlyList<string> events, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<WebhookSummary>> ListAsync(string tenant, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<WebhookSummary>> ListAsync(IQuerySession session, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string tenant, string name, CancellationToken ct) => throw new NotSupportedException();
    }
}
