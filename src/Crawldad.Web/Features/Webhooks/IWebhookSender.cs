using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text;

namespace Crawldad.Web.Features.Webhooks;

/// <summary>The outcome of one delivery attempt: whether the receiver accepted it (a 2xx), and the HTTP status observed
/// (null when the request never got a response — a connection failure or timeout).</summary>
public sealed record WebhookSendResult(bool Delivered, int? StatusCode);

/// <summary>The outbound-HTTP seam for webhook delivery — the single point that touches the network, so the delivery
/// handler stays pure and the suite substitutes a recording double (no real network in tests).</summary>
public interface IWebhookSender
{
    /// <summary>POSTs <paramref name="body"/> as <c>application/json</c> to <paramref name="url"/> with the signature/metadata
    /// <paramref name="headers"/>, bounded by <paramref name="timeout"/>. A non-2xx status or a transport fault is a
    /// non-delivery (retried); a cancellation of <paramref name="ct"/> (host shutdown) propagates so the message is redelivered.</summary>
    Task<WebhookSendResult> SendAsync(string url, string body, IReadOnlyDictionary<string, string> headers, TimeSpan timeout, CancellationToken ct);
}

/// <summary>The real <see cref="IWebhookSender"/>: the SSRF-hardened named delivery client (<see cref="WebhookHttpClient"/>) —
/// resolve-and-pinned to a send-time-validated public address, redirects refused — POSTing the signed body. A non-2xx
/// response or any transport fault (connection refused, DNS failure, a rejected internal/rebinding target, or a
/// per-attempt timeout) is reported as a non-delivery so the handler can retry; a real cancellation of the caller's
/// token is left to propagate.</summary>
internal sealed class HttpWebhookSender(IHttpClientFactory httpClientFactory) : IWebhookSender
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Any transport fault (connection refused, DNS failure, protocol error, or a per-attempt timeout surfaced as TaskCanceledException) is a non-delivery to be retried; a genuine host-shutdown cancellation is excluded by the ct guard and left to propagate so the durable message is redelivered.")]
    public async Task<WebhookSendResult> SendAsync(string url, string body, IReadOnlyDictionary<string, string> headers, TimeSpan timeout, CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient(WebhookHttpClient.Name);
        client.Timeout = timeout;

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        try
        {
            using var response = await client.SendAsync(request, ct);
            return new WebhookSendResult(response.IsSuccessStatusCode, (int)response.StatusCode);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return new WebhookSendResult(false, null);
        }
    }
}
