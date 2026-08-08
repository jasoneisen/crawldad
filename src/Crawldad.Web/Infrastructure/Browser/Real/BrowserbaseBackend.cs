using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Crawldad.Web.Infrastructure.Security;
using Microsoft.Playwright;

namespace Crawldad.Web.Infrastructure.Browser.Real;

/// <summary>
/// The <c>"browserbase"</c> adapter (§9.1): connects over <b>CDP</b> (Browserbase is CDP-only for Playwright) via
/// <c>chromium.connectOverCDP</c>. Two credential modes:
/// <list type="bullet">
///   <item><b>connectUrl</b> — the resolved secret <em>is</em> the whole CDP URL; connect to it directly.</item>
///   <item><b>apiKey</b> (default) — the resolved secret is the account API key; we <c>POST /v1/sessions</c> with an
///   <c>X-BB-API-Key</c> header and connect to the returned <c>connectUrl</c>, recording the response <c>region</c>.</item>
/// </list>
/// <b>Ship-blocker fact (§3.5/§9, live-primary re-verified 2026-08-08):</b> the returned <c>connectUrl</c> has the form
/// <c>wss://connect.&lt;region&gt;.browserbase.com/?signingKey=&lt;JWT&gt;</c> (e.g. <c>connect.usw2.browserbase.com</c>) —
/// a per-session JWT, <b>not</b> the account apiKey (which travels only in the <c>X-BB-API-Key</c> header). The whole URL
/// is still a live-session secret: never logged, never placed in an exception message — the connect is a scrubbing
/// boundary that converts any fault into a secret-free terminal <see cref="BrowserConnectException"/>.
/// </summary>
/// <param name="provider">The shared Playwright driver.</param>
/// <param name="secrets">Resolves the apiKey or connectUrl by reference at connect time.</param>
/// <param name="secretScope">The per-run secret registry the resolved secret and the apiKey-embedding connectUrl are registered into for exact-match scrubbing (§12).</param>
/// <param name="httpClientFactory">Creates the client for the apiKey-mode session-create call.</param>
/// <param name="cache">The cross-run asset cache backing the route cache.</param>
/// <param name="throttle">The global request throttle.</param>
/// <param name="apiBaseUrl">The Browserbase API base URL (overridable for tests).</param>
internal sealed class BrowserbaseBackend(
    IPlaywrightProvider provider,
    ISecretStore secrets,
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
        Justification = "The connect is a credential-scrubbing boundary (§12): the connectUrl carries a per-session signingKey JWT (live-verified 2026-08-08), so ANY fault (a PlaywrightException can echo the CDP URL) must become a secret-free BrowserConnectException. Cancellation and the already-scrubbed BrowserConnectException are excluded by the filter.")]
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
            secretScope.Register(secret); // register before connect so a failure's logs/events scrub the apiKey/connectUrl too (§12)
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

            secretScope.Register(connectUrl); // the connectUrl carries a per-session signingKey JWT (§3.5, live-verified 2026-08-08) — a secret in its own right

            var playwright = await provider.GetAsync(ct);
            browser = await playwright.Chromium.ConnectOverCDPAsync(connectUrl);
            context = browser.Contexts[0]; // connectOverCDP always exposes the browser's default context
            context.SetDefaultTimeout(policy.DefaultTimeoutMs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not BrowserConnectException)
        {
            throw new BrowserConnectException("failed to establish a 'browserbase' backend session");
        }

        return new PlaywrightBrowserSession(context, browser, policy, cache, throttle, region);
    }

    private async Task<string> ResolveSecretAsync(BackendBinding binding, CancellationToken ct)
    {
        var credentialRef = binding.CredentialRef
            ?? throw new BrowserConnectException("the 'browserbase' backend requires a credentialRef (an apiKey or a connectUrl)");
        return await secrets.ResolveAsync(credentialRef, ct);
    }

    // apiKey mode: create the session ourselves (POST /v1/sessions), then connect to the returned connectUrl. The
    // response also carries the region we record for cache locality (§9.1).
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
