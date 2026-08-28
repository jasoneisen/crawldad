using System.Net;
using System.Text;
using Crawldad.Contracts.Tenancy;
using Crawldad.Portal.Tenancy;

namespace Crawldad.Tests.Portal;

/// <summary>The console-read → stored-key fallback (issue #119 PR4): a successful console request is passed straight
/// through; a <c>401</c>/<c>403</c> retries ONCE with the tenant's stored key alone — no console token, no selectors (never
/// both credentials in one request) — and a write body survives the retry. This is the dual-run safety net.</summary>
public class ConsoleReadFallbackHandlerTests
{
    private const string _key = "tenant-key-0123456789";

    private static HttpClient ClientOver(HttpMessageHandler inner) => new(new ConsoleReadFallbackHandler(inner, _key));

    private static HttpRequestMessage ConsoleRequest(HttpMethod method, string url, string? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer console-token");
        request.Headers.TryAddWithoutValidation(ConsoleAuthHeaders.ConsoleUser, "u@x.test");
        request.Headers.TryAddWithoutValidation(ConsoleAuthHeaders.Workspace, "w1");
        request.Headers.TryAddWithoutValidation("Accept", "application/json"); // a non-console header the retry must carry over
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return request;
    }

    [Fact]
    public async Task A_successful_console_read_is_passed_through_untouched()
    {
        var inner = new ScriptedHandler(HttpStatusCode.OK);
        using var client = ClientOver(inner);

        var response = await client.SendAsync(ConsoleRequest(HttpMethod.Get, "https://api.crawldad.test/runs"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.Seen.Count.ShouldBe(1);
        inner.Seen[0].Auth.ShouldBe("Bearer console-token");
        inner.Seen[0].HasSelectors.ShouldBeTrue();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_rejected_console_request_retries_once_with_the_key_alone(HttpStatusCode rejection)
    {
        var inner = new ScriptedHandler(rejection, HttpStatusCode.OK);
        using var client = ClientOver(inner);

        var response = await client.SendAsync(ConsoleRequest(HttpMethod.Get, "https://api.crawldad.test/webhooks"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK); // the fallback (stored-key) response is returned
        inner.Seen.Count.ShouldBe(2);
        inner.Seen[1].Auth.ShouldBe($"Bearer {_key}"); // retried with the stored key...
        inner.Seen[1].HasSelectors.ShouldBeFalse();     // ...and WITHOUT the console selectors (never both credentials)
    }

    [Fact]
    public async Task A_write_body_survives_the_key_retry()
    {
        var inner = new ScriptedHandler(HttpStatusCode.Unauthorized, HttpStatusCode.OK);
        using var client = ClientOver(inner);

        await client.SendAsync(ConsoleRequest(HttpMethod.Post, "https://api.crawldad.test/payloads", "{\"name\":\"x\"}"));

        inner.Seen.Count.ShouldBe(2);
        inner.Seen[1].Body.ShouldBe("{\"name\":\"x\"}"); // the buffered body is re-sent on the retry
    }

    private sealed class ScriptedHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        public List<(string? Auth, bool HasSelectors, string Body)> Seen { get; } = [];
        private int _next;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Seen.Add((
                request.Headers.Authorization?.ToString(),
                request.Headers.Contains(ConsoleAuthHeaders.ConsoleUser) || request.Headers.Contains(ConsoleAuthHeaders.Workspace),
                body));
            return new HttpResponseMessage(statuses[Math.Min(_next++, statuses.Length - 1)]);
        }
    }
}
