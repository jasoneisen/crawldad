using Crawldad.Tests.Support;
using Crawldad.Web.Features.Webhooks;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Unit;

/// <summary>The real HTTP sender against a loopback server (no mock): a 2xx is a delivery, a non-2xx or a connection
/// failure is a non-delivery to be retried, and a genuine cancellation propagates so the durable message is redelivered.</summary>
public class HttpWebhookSenderTests
{
    private static readonly IHttpClientFactory _http =
        new ServiceCollection().AddHttpClient().BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

    private static readonly IReadOnlyDictionary<string, string> _headers =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["X-Crawldad-Event"] = "run.succeeded" };

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task A_2xx_is_a_delivery()
    {
        using var site = new LocalSite().Map("/hook", "application/json", "ok", status: 200);
        var sender = new HttpWebhookSender(_http);

        var result = await sender.SendAsync(site.Url("/hook"), "{\"id\":\"e\"}", _headers, _timeout, CancellationToken.None);

        result.Delivered.ShouldBeTrue();
        result.StatusCode.ShouldBe(200);
        site.Hits("/hook").ShouldBe(1);
    }

    [Fact]
    public async Task A_non_2xx_is_not_a_delivery()
    {
        using var site = new LocalSite().Map("/hook", "application/json", "no", status: 500);
        var sender = new HttpWebhookSender(_http);

        var result = await sender.SendAsync(site.Url("/hook"), "{}", _headers, _timeout, CancellationToken.None);

        result.Delivered.ShouldBeFalse();
        result.StatusCode.ShouldBe(500);
    }

    [Fact]
    public async Task A_connection_failure_is_a_non_delivery_with_no_status()
    {
        var dead = $"http://127.0.0.1:{Net.FreePort()}/hook"; // nothing is listening there
        var sender = new HttpWebhookSender(_http);

        var result = await sender.SendAsync(dead, "{}", _headers, _timeout, CancellationToken.None);

        result.Delivered.ShouldBeFalse();
        result.StatusCode.ShouldBeNull();
    }

    [Fact]
    public async Task A_real_cancellation_propagates()
    {
        using var site = new LocalSite().Map("/hook", "application/json", "ok", status: 200);
        var sender = new HttpWebhookSender(_http);

        await Should.ThrowAsync<OperationCanceledException>(
            () => sender.SendAsync(site.Url("/hook"), "{}", _headers, _timeout, new CancellationToken(canceled: true)));
    }
}
