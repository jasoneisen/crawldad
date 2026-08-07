using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Crawldad.Web.Infrastructure.Security;
using Microsoft.Playwright;

namespace Crawldad.Web.Infrastructure.Browser.Real;

/// <summary>
/// The <c>"browserless"</c> adapter (§9.1): connects <b>natively</b> (preferred over CDP) via
/// <c>chromium.connect</c> to <c>wss://production-{region}.browserless.io/chromium/playwright?token=…</c>. The region
/// comes from <c>backendOptions["region"]</c> (default <c>sfo</c>); the account <c>token</c> is resolved by reference
/// (§12) and carried in the query; any other <c>backendOptions</c> (e.g. <c>blockAds</c>, <c>proxy</c>) are appended as
/// query params Browserless understands. The connect is wrapped so that <b>no</b> connect URL or token can escape in an
/// exception message — a failure is a secret-free terminal <see cref="BrowserConnectException"/>.
/// </summary>
/// <param name="provider">The shared Playwright driver.</param>
/// <param name="secrets">Resolves the account token by reference at connect time.</param>
/// <param name="secretScope">The per-run secret registry the resolved token is registered into for exact-match scrubbing (§12).</param>
/// <param name="cache">The cross-run asset cache backing the route cache.</param>
/// <param name="throttle">The global request throttle.</param>
/// <param name="endpointTemplate">The ws endpoint template with a <c>{region}</c> placeholder (overridable for tests).</param>
internal sealed class BrowserlessBackend(
    IPlaywrightProvider provider,
    ISecretStore secrets,
    IRunSecretScope secretScope,
    IAssetCache cache,
    IThrottleGate throttle,
    string endpointTemplate) : IBrowserBackend
{
    /// <summary>The production ws endpoint template; the region substitutes into <c>{region}</c> (§9.1).</summary>
    internal const string DefaultEndpointTemplate = "wss://production-{region}.browserless.io/chromium/playwright";

    /// <summary>The region used when <c>backendOptions["region"]</c> is absent.</summary>
    internal const string DefaultRegion = "sfo";

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The connect is a credential-scrubbing boundary (§12): ANY provider fault (a PlaywrightException can embed the wss token URL in its message) must be converted to a secret-free BrowserConnectException. Cancellation and the already-scrubbed BrowserConnectException are excluded by the filter.")]
    public async Task<IBrowserSession> ConnectAsync(BackendBinding binding, SessionPolicy policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(policy);

        var region = binding.Options?.GetValueOrDefault("region") as string ?? DefaultRegion;
        IBrowser browser;
        IBrowserContext context;
        try
        {
            ct.ThrowIfCancellationRequested();
            var token = await ResolveTokenAsync(binding, ct);
            secretScope.Register(token); // register before connect so a failure's logs/events scrub the token too (§12)
            var endpoint = BuildEndpoint(endpointTemplate, region, token, binding.Options);
            var playwright = await provider.GetAsync(ct);
            browser = await playwright.Chromium.ConnectAsync(endpoint);
            context = await browser.NewContextAsync(new BrowserNewContextOptions { BypassCSP = policy.BypassCsp });
            context.SetDefaultTimeout(policy.DefaultTimeoutMs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not BrowserConnectException)
        {
            throw new BrowserConnectException("failed to establish a 'browserless' backend session");
        }

        return new PlaywrightBrowserSession(context, browser, policy, cache, throttle, region);
    }

    private async Task<string> ResolveTokenAsync(BackendBinding binding, CancellationToken ct)
    {
        var credentialRef = binding.CredentialRef
            ?? throw new BrowserConnectException("the 'browserless' backend requires a credentialRef (the account token)");
        return await secrets.ResolveAsync(credentialRef, ct);
    }

    /// <summary>Builds the native connect URL: the region substituted into the template, the token as the first query
    /// param, and every other (non-null, non-<c>region</c>) backend option appended as a query param (§9.1 passthrough).</summary>
    /// <param name="template">The endpoint template with a <c>{region}</c> placeholder.</param>
    /// <param name="region">The datacenter region.</param>
    /// <param name="token">The resolved account token.</param>
    /// <param name="options">The backend options passed through as query params.</param>
    /// <returns>The full <c>wss://…?token=…&amp;…</c> connect URL.</returns>
    internal static string BuildEndpoint(string template, string region, string token, IReadOnlyDictionary<string, object?>? options)
    {
        var baseUrl = template.Replace("{region}", region, StringComparison.Ordinal);
        var query = new List<string> { "token=" + Uri.EscapeDataString(token) };
        if (options is not null)
        {
            foreach (var option in options
                .Where(static o => !string.Equals(o.Key, "region", StringComparison.Ordinal) && o.Value is not null)
                .OrderBy(static o => o.Key, StringComparer.Ordinal))
            {
                query.Add(Uri.EscapeDataString(option.Key) + "=" + Uri.EscapeDataString(FormatValue(option.Value!)));
            }
        }

        return baseUrl + "?" + string.Join('&', query);
    }

    // Passthrough values arrive from JSON: booleans lower-cased (Browserless expects blockAds=true), strings verbatim,
    // everything else (numbers/objects) JSON-serialized.
    private static string FormatValue(object value) => value switch
    {
        bool b => b ? "true" : "false",
        string s => s,
        _ => JsonSerializer.Serialize(value),
    };
}
