using System.Diagnostics.CodeAnalysis;
using Microsoft.Playwright;

namespace Crawldad.Web.Infrastructure.Browser.Real;

/// <summary>The <c>"local"</c> adapter: launches a headless Chromium and hands back a <see cref="PlaywrightBrowserSession"/>.
/// Credential-free dev/test backend. The browser is launched once, lazily, and shared across runs; each run gets its
/// own context for isolation, so launch args only take effect on the very first launch.</summary>
internal sealed class LocalChromiumBackend(IPlaywrightProvider provider, IAssetCache cache, IThrottleGate throttle)
    : IBrowserBackend, IAsyncDisposable
{
    private readonly SemaphoreSlim _launchGate = new(1, 1);
    private IBrowser? _browser;

    public async Task<IBrowserSession> ConnectAsync(BackendBinding binding, SessionPolicy policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(policy);

        var browser = await EnsureBrowserAsync(policy, ct);
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { BypassCSP = policy.BypassCsp });
        context.SetDefaultTimeout(policy.DefaultTimeoutMs);

        var region = binding.Options?.GetValueOrDefault("region") as string ?? "local";
        return new PlaywrightBrowserSession(context, ownedBrowser: null, policy, cache, throttle, region);
    }

    [SuppressMessage("Reliability", "CA1508:Avoid dead conditional code",
        Justification = "Double-checked locking: another caller can launch the browser between the lock-free fast path and acquiring the gate, so the re-check is not dead — the dataflow analyzer cannot model the concurrency.")]
    private async Task<IBrowser> EnsureBrowserAsync(SessionPolicy policy, CancellationToken ct)
    {
        if (_browser is not null)
        {
            return _browser;
        }

        await _launchGate.WaitAsync(ct);
        try
        {
            if (_browser is null)
            {
                var playwright = await provider.GetAsync(ct);
                _browser = await playwright.Chromium.LaunchAsync(
                    new BrowserTypeLaunchOptions { Headless = true, Args = policy.LaunchArgs });
            }

            return _browser;
        }
        finally
        {
            _launchGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _launchGate.Dispose();
    }
}
