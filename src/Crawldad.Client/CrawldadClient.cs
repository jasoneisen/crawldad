using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Crawldad.Contracts.Payloads;
using Crawldad.Contracts.Runs;

namespace Crawldad.Client;

/// <summary>The official typed client for the Crawldad API. A thin, stateless layer over an injected
/// <see cref="HttpClient"/>: every method maps one endpoint, sends the tenant's API key as
/// <c>Authorization: Bearer</c>, deserializes the <c>Crawldad.Contracts</c> wire type, and translates the API's typed
/// rejection/problem bodies into <see cref="CrawldadException"/> subtypes (never a raw
/// <see cref="HttpRequestException"/> for an API-level rejection). Async-only, cancellation everywhere. Construct it via
/// <see cref="CrawldadClientServiceCollectionExtensions.AddCrawldadClient"/> or directly for tests.</summary>
public sealed partial class CrawldadClient
{
    private readonly HttpClient _http;
    private readonly ICrawldadCredential _credential;

    /// <summary>Creates a client over <paramref name="httpClient"/>. If the client has no
    /// <see cref="HttpClient.BaseAddress"/>, <see cref="CrawldadClientOptions.BaseUrl"/> is applied (normalized with a
    /// trailing slash); otherwise the existing base address is left untouched.</summary>
    /// <param name="httpClient">The transport. Owned by the caller / DI (the SDK never disposes it).</param>
    /// <param name="options">The base URL and the credential (an <see cref="CrawldadClientOptions.ApiKey"/>, or an explicit
    /// <see cref="CrawldadClientOptions.Credential"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="httpClient"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="options"/> supplies neither an API key nor a credential.</exception>
    public CrawldadClient(HttpClient httpClient, CrawldadClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);

        _http = httpClient;
        _credential = options.ResolveCredential();
        if (httpClient.BaseAddress is null && options.BaseUrl is not null)
        {
            httpClient.BaseAddress = CrawldadClientOptions.NormalizeBaseUrl(options.BaseUrl);
        }
    }

    // Builds an authenticated request for a path relative to the HttpClient's base address. The credential stamps its
    // headers per request via TryAddWithoutValidation (no shared default-header mutation, so the client is concurrency-safe);
    // it is async so a console token can be acquired/refreshed per request.
    private async ValueTask<HttpRequestMessage> BuildRequestAsync(HttpMethod method, string relativePath, CancellationToken ct, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, new Uri(relativePath, UriKind.Relative)) { Content = content };
        await _credential.ApplyAsync(request, ct).ConfigureAwait(false);
        return request;
    }

    // Serializes a request body to JSON content by its runtime type, using the shared wire conventions. The returned
    // content is owned by the HttpRequestMessage it is assigned to and disposed with it.
    private static JsonContent JsonBody(object body) => JsonContent.Create(body, body.GetType(), mediaType: null, CrawldadJson.Options);

    // GET returning a JSON DTO.
    private async Task<T> GetAsync<T>(string relativePath, CancellationToken ct)
    {
        using var request = await BuildRequestAsync(HttpMethod.Get, relativePath, ct);
        using var response = await _http.SendAsync(request, ct);
        return await ReadJsonAsync<T>(response, ct);
    }

    // A body-carrying mutation (POST/PUT) returning a JSON DTO.
    private async Task<T> SendJsonAsync<T>(HttpMethod method, string relativePath, object body, CancellationToken ct)
    {
        using var request = await BuildRequestAsync(method, relativePath, ct, JsonBody(body));
        using var response = await _http.SendAsync(request, ct);
        return await ReadJsonAsync<T>(response, ct);
    }

    // A bodyless mutation (POST with no body) returning a JSON DTO — e.g. archive/cancel.
    private async Task<T> PostAsync<T>(string relativePath, CancellationToken ct)
    {
        using var request = await BuildRequestAsync(HttpMethod.Post, relativePath, ct);
        using var response = await _http.SendAsync(request, ct);
        return await ReadJsonAsync<T>(response, ct);
    }

    // A mutation that returns no content (204) — DELETE. Maps a non-success to the typed exception.
    private async Task SendNoContentAsync(HttpMethod method, string relativePath, CancellationToken ct)
    {
        using var request = await BuildRequestAsync(method, relativePath, ct);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateErrorAsync(response, ct);
        }
    }

    // Reads a success JSON body, or throws the mapped typed exception on a non-success status.
    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateErrorAsync(response, ct);
        }

        var value = await response.Content.ReadFromJsonAsync<T>(CrawldadJson.Options, ct);
        return value ?? throw new CrawldadApiException((int)response.StatusCode, "The Crawldad API returned an empty response body.", null);
    }

    // Translates a non-success response into the most specific CrawldadException the body supports. The body is read
    // once, then classified by shape: a { code, message } run rejection, an { errors: [...] } payload-validation
    // problem, an { errors: { field: [...] } } validation problem, else a problem-details/opaque fallback.
    private static async Task<CrawldadException> CreateErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var body = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return new CrawldadUnauthorizedException(status, "Unauthorized — the request had no valid Crawldad API key.");
        }

        if (TryClassifyBody(status, body, out var typed))
        {
            return typed;
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new CrawldadNotFoundException(status, body.Length == 0 ? "The requested resource was not found." : body);
        }

        return new CrawldadApiException(status, DescribeProblem(body, status), body.Length == 0 ? null : body);
    }

    // Shape-classifies a JSON error body. Returns false (letting the caller fall back) for a non-JSON body, a
    // non-object, or an object matching none of the three typed shapes.
    private static bool TryClassifyBody(int status, string body, out CrawldadException typed)
    {
        typed = null!;
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(body);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return false;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (root.TryGetProperty("errors", out var errors))
        {
            if (errors.ValueKind == JsonValueKind.Array)
            {
                var problem = root.Deserialize<PayloadValidationProblem>(CrawldadJson.Options)!;
                typed = new CrawldadPayloadInvalidException(status, problem);
                return true;
            }

            if (errors.ValueKind == JsonValueKind.Object)
            {
                // A JSON object always deserializes to a (possibly empty) dictionary, never null.
                var map = errors.Deserialize<Dictionary<string, string[]>>(CrawldadJson.Options)!;
                typed = new CrawldadValidationException(status, CrawldadValidationException.Freeze(map));
                return true;
            }
        }

        if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
            && root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
        {
            typed = new CrawldadRunRejectedException(status, new RunRejection(code.GetString()!, message.GetString()!));
            return true;
        }

        return false;
    }

    // A human-readable description for the opaque fallback: an RFC 7807 detail/title when present, else a generic line.
    private static string DescribeProblem(string body, int status)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                    {
                        return detail.GetString()!;
                    }

                    if (root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                    {
                        return title.GetString()!;
                    }
                }
            }
            catch (JsonException)
            {
                // not JSON — fall through to the generic description
            }
        }

        return $"The Crawldad API request failed with status {status}.";
    }
}
