using System.Diagnostics.CodeAnalysis;
using Microsoft.Playwright;

namespace Crawldad.Web.Infrastructure.Browser.Real;

/// <summary>
/// The <c>"local"</c> adapter (§9): launches a local headless Chromium with the §8.1 launch args and opens a context
/// with the §8.1 context options, then hands back a <see cref="PlaywrightBrowserSession"/>. Credential-free — the
/// dev/test backend, and the one the Phase 4 fixture-site parity gate (WP2) drives.
/// <para>
/// One browser is launched lazily and shared across runs (mirroring <c>PlaywrightFactory</c>'s cached
/// <c>IBrowser</c>); each run gets its own <b>context</b> for isolation (§12), and the session closes only that
/// context. The browser is disposed with the adapter (a DI singleton disposed by the host). Launch args are therefore
/// honoured at first launch; since every run against this dev backend uses the same
/// <c>--disable-web-security</c> arg this is faithful, and WP2 relies on it.
/// </para>
/// </summary>
/// <param name="provider">The shared Playwright driver.</param>
/// <param name="cache">The cross-run asset cache backing the route cache.</param>
/// <param name="throttle">The global request throttle.</param>
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
