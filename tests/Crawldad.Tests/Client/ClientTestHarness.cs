using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Crawldad.Client;

namespace Crawldad.Tests.Client;

/// <summary>A snapshot of one outgoing request, captured before the client disposes it — so a test can assert on the
/// method, path, auth header, and serialized body after the call returns.</summary>
internal sealed record CapturedRequest(
    HttpMethod Method,
    string Path,
    string Query,
    string? Authorization,
    string? Accept,
    string? LastEventId,
    string Body);

/// <summary>A stub <see cref="HttpMessageHandler"/> that records each request and returns a scripted response, so every
/// client method + error mapping is exercised with no socket. The responder receives the captured request so it can
/// branch on path/method.</summary>
internal sealed class StubHttpMessageHandler(Func<CapturedRequest, HttpResponseMessage> responder) : HttpMessageHandler
{
    private readonly List<CapturedRequest> _requests = [];

    /// <summary>Every request the handler saw, in order.</summary>
    public IReadOnlyList<CapturedRequest> Requests => _requests;

    /// <summary>The single (or last) request — the common case for a one-call test.</summary>
    public CapturedRequest Last => _requests[^1];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        // RequestUri is relative until HttpClient combines it with BaseAddress; by SendAsync it is absolute.
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var captured = new CapturedRequest(
            request.Method,
            uri.AbsolutePath,
            uri.Query,
            request.Headers.TryGetValues("Authorization", out var auth) ? string.Join(",", auth) : null,
            request.Headers.TryGetValues("Accept", out var accept) ? string.Join(",", accept) : null,
            request.Headers.TryGetValues("Last-Event-ID", out var lastId) ? string.Join(",", lastId) : null,
            body);
        _requests.Add(captured);
        return responder(captured);
    }
}

/// <summary>Builds a <see cref="CrawldadClient"/> over a stub handler and constructs canned responses, all with the
/// client's own wire conventions so bodies round-trip exactly as the real API's would.</summary>
internal static class ClientTestHarness
{
    /// <summary>The synthetic API key every stub-backed test presents (never a real credential).</summary>
    public const string ApiKey = "test-key-0123456789abcdef";

    /// <summary>The base address the stub client uses.</summary>
    public static Uri BaseUrl { get; } = new("https://api.crawldad.test/");

    /// <summary>Builds a client whose transport is <paramref name="handler"/>.</summary>
    public static CrawldadClient ClientFor(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = BaseUrl }, new CrawldadClientOptions { BaseUrl = BaseUrl, ApiKey = ApiKey });

    /// <summary>A handler that returns the same response for every request.</summary>
    public static StubHttpMessageHandler Always(Func<HttpResponseMessage> response) => new(_ => response());

    /// <summary>A 200 (or other 2xx) response carrying <paramref name="value"/> serialized with the wire conventions.</summary>
    public static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(value, CrawldadJson.Options), Encoding.UTF8, "application/json") };

    /// <summary>A response carrying a raw JSON string (for hand-shaped error bodies).</summary>
    public static HttpResponseMessage JsonRaw(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>A response carrying plain text.</summary>
    public static HttpResponseMessage Text(HttpStatusCode status, string text) =>
        new(status) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };

    /// <summary>An empty-bodied response (e.g. 204, or a bare 404/401).</summary>
    public static HttpResponseMessage Empty(HttpStatusCode status) => new(status);

    /// <summary>A <c>text/event-stream</c> response carrying <paramref name="sse"/> as its body.</summary>
    public static HttpResponseMessage EventStream(string sse)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return response;
    }
}
