using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Crawldad.Api.Infrastructure.Security;
using Microsoft.Playwright;

namespace Crawldad.Api.Infrastructure.Browser.Real;

/// <summary>The <c>"browserbase"</c> adapter: connects over CDP. Credential modes: <b>connectUrl</b> (the secret is
/// the whole CDP URL) or <b>apiKey</b> (default; POSTs <c>/v1/sessions</c> for a connectUrl). The returned connectUrl
/// embeds a per-session signingKey JWT — itself a live secret, never logged — so the connect is a scrubbing boundary.</summary>
internal sealed class BrowserbaseBackend(
    IPlaywrightProvider provider,
    IConnectCredentialResolver resolver,
    IRunSecretScope secretScope,
    IHttpClientFactory httpClientFactory,
    IAssetCache cache,
    IThrottleGate throttle,
    string apiBaseUrl) : IBrowserBackend
{
    /// <summary>The production Browserbase API base URL.</summary>
    internal const string DefaultApiBaseUrl = "https://api.browserbase.com";

    /// <summary>The <c>backendOptions["mode"]</c> value selecting connectUrl mode; anything else (or absent) is apiKey mode.</summary>
    internal const string ConnectUrlMode = "connectUrl";

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The connect is a credential-scrubbing boundary: the connectUrl carries a per-session signingKey JWT (live-verified 2026-08-08), so ANY fault (a PlaywrightException can echo the CDP URL) must become a secret-free BrowserConnectException. Cancellation and the already-scrubbed BrowserConnectException are excluded by the filter.")]
    public async Task<IBrowserSession> ConnectAsync(BackendBinding binding, SessionPolicy policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(policy);

        IBrowser browser;
        IBrowserContext context;
        string region;
        try
        {
            ct.ThrowIfCancellationRequested();
            var secret = await ResolveSecretAsync(binding, ct);
            secretScope.Register(secret); // register before connect so a failure's logs/events scrub the apiKey/connectUrl too
            var options = binding.Options;
            string connectUrl;
            if (options is not null && string.Equals(options.GetValueOrDefault("mode") as string, ConnectUrlMode, StringComparison.Ordinal))
            {
                connectUrl = secret; // the secret IS the connect URL (embeds the apiKey — treated as a secret everywhere)
                region = options.GetValueOrDefault("region") as string ?? "unknown";
            }
            else
            {
                (connectUrl, region) = await CreateSessionAsync(secret, options, ct);
            }

            secretScope.Register(connectUrl); // the connectUrl carries a per-session signingKey JWT — a secret in its own right

            var playwright = await provider.GetAsync(ct);
            browser = await playwright.Chromium.ConnectOverCDPAsync(connectUrl);
            context = browser.Contexts[0]; // connectOverCDP always exposes the browser's default context
            context.SetDefaultTimeout(policy.DefaultTimeoutMs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not BrowserConnectException)
        {
            // Flatten to a secret-free message, but classify the raw fault first so a bounded connectRetry can tell a
            // transient network/5xx/CDP-handshake blip from an auth-shaped rejection (a 4xx from the session API, an
            // absent credential). The raw fault — which can embed the CDP URL — is inspected here, never surfaced.
            throw new BrowserConnectException("failed to establish a 'browserbase' backend session", ConnectFaultClassifier.IsTransient(ex));
        }

        return new PlaywrightBrowserSession(context, browser, policy, cache, throttle, region);
    }

    private async Task<string> ResolveSecretAsync(BackendBinding binding, CancellationToken ct)
    {
        var credentialRef = binding.CredentialRef
            ?? throw new BrowserConnectException("the 'browserbase' backend requires a credentialRef (an apiKey or a connectUrl)");
        return await resolver.ResolveConnectAsync(credentialRef, binding.Tenant!, ct); // tenant-scoped: registered browsers then config
    }

    // apiKey mode: create the session ourselves (POST /v1/sessions), then connect to the returned connectUrl. The
    // response also carries the region we record for cache locality.
    private async Task<(string ConnectUrl, string Region)> CreateSessionAsync(
        string apiKey, IReadOnlyDictionary<string, object?>? options, CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, apiBaseUrl.TrimEnd('/') + "/v1/sessions");
        request.Headers.Add("X-BB-API-Key", apiKey);
        request.Content = JsonContent.Create(new SessionCreateBody(options?.GetValueOrDefault("projectId") as string));

        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var session = await response.Content.ReadFromJsonAsync<BrowserbaseSession>(ct)
            ?? throw new BrowserConnectException("browserbase session create returned an empty body");
        return (session.ConnectUrl, session.Region ?? "unknown");
    }

    private sealed record SessionCreateBody([property: JsonPropertyName("projectId")] string? ProjectId);

    private sealed record BrowserbaseSession(
        [property: JsonPropertyName("connectUrl")] string ConnectUrl,
        [property: JsonPropertyName("region")] string? Region,
        [property: JsonPropertyName("expiresAt")] string? ExpiresAt);
}
