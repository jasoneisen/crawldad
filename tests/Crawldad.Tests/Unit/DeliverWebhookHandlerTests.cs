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
/// fresh timestamp, POSTs via the sender seam, and — deterministically, by the returned cascade — either stops (success,
/// drop, or exhaustion) or schedules the next attempt with the exponential-backoff delay.</summary>
public class DeliverWebhookHandlerTests
{
    private static readonly ResolvedWebhookEndpoint _endpoint = new("https://hooks.example.com/x", "whsec_0123456789abcdef", []);

    private static DeliverWebhook Message(int attempt, string body = "{\"id\":\"evt-1\"}") =>
        new("prod", "run.succeeded", "evt-1", body, attempt);

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
        });

    private static Task<OutgoingMessages> HandleAsync(DeliverWebhook message, IWebhookEndpointStore store, IWebhookSender sender, IOptions<WebhookOptions>? options = null) =>
        DeliverWebhookHandler.Handle(message, null!, store, sender, options ?? Options(), new FakeClock(), NullLogger<DeliverWebhook>.Instance, CancellationToken.None);

    [Fact]
    public async Task Drops_when_the_endpoint_was_deregistered()
    {
        var sender = new RecordingWebhookSender();

        var outgoing = await HandleAsync(Message(1), new StubStore(null), sender);

        outgoing.ShouldBeEmpty();
        sender.CallCount.ShouldBe(0); // never even attempted
    }

    [Fact]
    public async Task Delivers_and_signs_then_stops()
    {
        var sender = new RecordingWebhookSender();
        sender.AlwaysDeliver();

        var outgoing = await HandleAsync(Message(1), new StubStore(_endpoint), sender);

        outgoing.ShouldBeEmpty(); // accepted — no retry
        sender.CallCount.ShouldBe(1);
        var call = sender.Last!;
        call.Url.ShouldBe(_endpoint.Url);
        call.Headers["X-Crawldad-Event"].ShouldBe("run.succeeded");
        call.Headers["X-Crawldad-Delivery"].ShouldBe("evt-1");
        var timestamp = FakeClock.Fixed.ToUnixTimeSeconds();
        call.Headers["X-Crawldad-Timestamp"].ShouldBe(timestamp.ToString(CultureInfo.InvariantCulture));
        call.Headers["X-Crawldad-Signature"].ShouldBe(WebhookSignature.Compute(_endpoint.Secret, timestamp, call.Body));
    }

    [Fact]
    public async Task Schedules_a_backed_off_retry_on_failure()
    {
        var sender = new RecordingWebhookSender();
        sender.AlwaysFail(503);

        var outgoing = await HandleAsync(Message(2), new StubStore(_endpoint), sender, Options(maxAttempts: 5, baseSecs: 2, maxSecs: 60));

        var retry = outgoing.OfType<DeliveryMessage<DeliverWebhook>>().Single();
        retry.Message.Attempt.ShouldBe(3);
        retry.Options.ScheduleDelay.ShouldBe(TimeSpan.FromSeconds(4)); // base 2s * 2^(2-1)
    }

    [Fact]
    public async Task Gives_up_after_the_attempt_cap()
    {
        var sender = new RecordingWebhookSender();
        sender.AlwaysFail(500);

        var outgoing = await HandleAsync(Message(3), new StubStore(_endpoint), sender, Options(maxAttempts: 3));

        outgoing.ShouldBeEmpty(); // attempt 3 == cap → abandoned, no further retry
        sender.CallCount.ShouldBe(1);
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
