using Alba;
using Crawldad.Tests.Support;
using Crawldad.Web;
using Crawldad.Web.Features.Webhooks;
using Crawldad.Web.Infrastructure.Browser;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>Shared host wiring for the webhooks suite: the test defaults plus a fast delivery cadence (so retries fire
/// within a poll window) and the recording sender substituted for the real HTTP one, so no delivery touches the network.
/// Split into a reusable <see cref="Apply"/> so the queue-trigger tests can build their own cap-1 gated host on the same
/// wiring.</summary>
internal static class WebhookTesting
{
    /// <summary>Applies the shared webhook test settings (fast, bounded delivery) on top of the standard test defaults.</summary>
    public static void Apply(IWebHostBuilder builder, string schema)
    {
        builder.UseCrawldadTestDefaults(schema);
        builder.UseSetting("Crawldad:Webhooks:Delivery:BaseDelay", "00:00:00.100");
        builder.UseSetting("Crawldad:Webhooks:Delivery:MaxDelay", "00:00:00.500");
        builder.UseSetting("Crawldad:Webhooks:Delivery:MaxAttempts", "3");
        builder.UseSetting("Crawldad:Webhooks:Delivery:Timeout", "00:00:02");
    }

    /// <summary>Builds a cap-1 host whose <c>fake</c> backend is the given gated backend, with the recording sender wired
    /// in — the shape the queue-cancel / queue-timeout trigger tests need (a slot held by a blocked run, the next queued).</summary>
    public static async Task<IAlbaHost> BuildGatedHostAsync(string schema, GateHolder holder, RecordingWebhookSender sender, params (string Key, string Value)[] extra)
    {
        var host = (await AlbaHost.For<Program>(builder =>
        {
            Apply(builder, schema);
            builder.UseSetting("Crawldad:Limits:MaxConcurrentRunsPerTenant", "1");
            foreach (var (key, value) in extra)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(new FakeClock());
                services.AddKeyedSingleton<IBrowserBackend>("fake", (_, _) => new GatedFakeBackend(Runner.FixturesRoot, holder));
                services.AddSingleton<IWebhookSender>(sender);
            });
        })).AuthenticatedAsPrimaryTenant();

        await host.ResetAllMartenDataAsync();
        return host;
    }

    /// <summary>Polls <paramref name="done"/> until true or the timeout elapses (the durable delivery pipeline is async).</summary>
    public static async Task PollAsync(Func<bool> done, string because, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (DateTime.UtcNow < deadline)
        {
            if (done())
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException(because);
    }
}

/// <summary>A shared Alba host for the webhooks-API suite (its own Marten schema): a frozen clock (deterministic
/// registration timestamps + signatures) and the recording webhook sender in place of the real HTTP one.</summary>
public sealed class WebhookApiFixture : IAsyncLifetime
{
    /// <summary>The recording sender every delivery lands in; sequential tests re-arm it via <c>Behave</c>.</summary>
    internal RecordingWebhookSender Sender { get; } = new();

    public IAlbaHost Host { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Host = (await AlbaHost.For<Program>(builder =>
        {
            WebhookTesting.Apply(builder, "crawldad_webhooks");
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(new FakeClock());
                services.AddSingleton<IWebhookSender>(Sender);
            });
        })).AuthenticatedAsPrimaryTenant();
        await Host.ResetAllMartenDataAsync();
    }

    public Task DisposeAsync() => Host.DisposeAsync().AsTask();
}

/// <summary>The webhooks-API collection — one shared host, sequential tests, each resetting Marten data first.</summary>
[CollectionDefinition(Name)]
public sealed class WebhookApiCollection : ICollectionFixture<WebhookApiFixture>
{
    public const string Name = "webhook-api";
}
