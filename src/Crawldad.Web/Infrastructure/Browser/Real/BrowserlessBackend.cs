using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Crawldad.Web.Infrastructure.Security;
using Microsoft.Playwright;

namespace Crawldad.Web.Infrastructure.Browser.Real;

/// <summary>The <c>"browserless"</c> adapter: connects natively via <c>chromium.connect</c> to a per-region
/// <c>wss://…?token=…</c> endpoint, the token resolved by reference. The connect is a credential-scrubbing boundary:
/// no connect URL or token can escape an exception message — any fault becomes a secret-free <see cref="BrowserConnectException"/>.</summary>
internal sealed class BrowserlessBackend(
    IPlaywrightProvider provider,
    IConnectCredentialResolver resolver,
    IRunSecretScope secretScope,
    IAssetCache cache,
    IThrottleGate throttle,
    string endpointTemplate) : IBrowserBackend
{
    /// <summary>The production ws endpoint template; the region substitutes into <c>{region}</c>.</summary>
    internal const string DefaultEndpointTemplate = "wss://production-{region}.browserless.io/chromium/playwright";

    /// <summary>The region used when <c>backendOptions["region"]</c> is absent.</summary>
    internal const string DefaultRegion = "sfo";

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The connect is a credential-scrubbing boundary: ANY provider fault (a PlaywrightException can embed the wss token URL in its message) must be converted to a secret-free BrowserConnectException. Cancellation and the already-scrubbed BrowserConnectException are excluded by the filter.")]
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
            secretScope.Register(token); // register before connect so a failure's logs/events scrub the token too
            var endpoint = BuildEndpoint(endpointTemplate, region, token, binding.Options);
            var playwright = await provider.GetAsync(ct);
            browser = await playwright.Chromium.ConnectAsync(endpoint);
            context = await browser.NewContextAsync(new BrowserNewContextOptions { BypassCSP = policy.BypassCsp });
            context.SetDefaultTimeout(policy.DefaultTimeoutMs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not BrowserConnectException)
        {
            // Flatten to a secret-free message, but classify the raw fault first so a bounded connectRetry can tell a
            // transient tunnel/handshake blip from an auth-shaped rejection (the raw fault is inspected, never surfaced).
            throw new BrowserConnectException("failed to establish a 'browserless' backend session", ConnectFaultClassifier.IsTransient(ex));
        }

        return new PlaywrightBrowserSession(context, browser, policy, cache, throttle, region);
    }

    private async Task<string> ResolveTokenAsync(BackendBinding binding, CancellationToken ct)
    {
        var credentialRef = binding.CredentialRef
            ?? throw new BrowserConnectException("the 'browserless' backend requires a credentialRef (the account token)");
        return await resolver.ResolveConnectAsync(credentialRef, binding.Tenant!, ct); // tenant-scoped: registered browsers then config
    }

    /// <summary>Builds the native connect URL: the region substituted into the template, the token as the first query
    /// param, and every other (non-null, non-<c>region</c>) backend option appended as a query param.</summary>
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
