using System.Diagnostics.CodeAnalysis;
using System.IO;
using Crawldad.Web.Infrastructure.Browser;
using Crawldad.Web.Infrastructure.Browser.Real;
using Microsoft.Playwright;

namespace Crawldad.Tests.Support;

/// <summary>The parity backend for the <c>"local"</c> adapter: runs real headless Chromium (one shared instance, a
/// fresh per-connect context for run isolation) so payloads exercise the identical <c>POST /runs</c> path as production,
/// serving pages from a <see cref="FixtureSite"/>. Throttling is deliberately skipped so the parity suite stays fast.</summary>
[SuppressMessage("Reliability", "CA2213:Disposable fields should be disposed",
    Justification = "The browser is disposed in DisposeAsync via its own DisposeAsync; the analyzer does not model async disposal of the nullable field.")]
internal sealed class FixtureChromiumBackend(IPlaywrightProvider provider, string fixturesRoot) : IBrowserBackend, IAsyncDisposable
{
    private readonly SemaphoreSlim _launchGate = new(1, 1);
    private IBrowser? _browser;

    public async Task<IBrowserSession> ConnectAsync(BackendBinding binding, SessionPolicy policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(policy);

        var fixture = binding.Options?.GetValueOrDefault("fixture") as string
            ?? throw new InvalidOperationException("the parity 'local' backend requires Options[\"fixture\"]");

        var browser = await EnsureBrowserAsync(policy, ct);
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { BypassCSP = policy.BypassCsp });
        context.SetDefaultTimeout(policy.DefaultTimeoutMs);
        return new FixtureBrowserSession(context, new FixtureSite(Path.Combine(fixturesRoot, fixture)));
    }

    [SuppressMessage("Reliability", "CA1508:Avoid dead conditional code",
        Justification = "Double-checked locking under the launch gate; the re-check is not dead — a concurrent connect can launch between the fast path and the gate.")]
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

                // Pin the canonical origin to loopback so even Chromium's speculative TCP/TLS preconnect (fired for a
                // canonical URL before route fulfillment intercepts the request) never leaves the machine — the
                // fixture harness is zero-third-party-traffic by construction, not just by fulfillment.
                var args = policy.LaunchArgs
                    .Append("--host-resolver-rules=MAP aca-prod.accela.com 127.0.0.1")
                    .ToArray();
                _browser = await playwright.Chromium.LaunchAsync(
                    new BrowserTypeLaunchOptions { Headless = true, Args = args });
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

/// <summary>One parity run's session: every page is served by <paramref name="site"/> via <c>page.RouteAsync</c>; off-origin
/// requests are aborted (default-deny — no request can reach the live site). Pages wrap in <see cref="PlaywrightPageHandle"/>.</summary>
/// <param name="context">Closed on dispose; the browser is shared and outlives it.</param>
internal sealed class FixtureBrowserSession(IBrowserContext context, FixtureSite site) : IBrowserSession
{
    public string Region => "local";

    /// <summary>Always 0 — the fixture route does not model the cross-run asset cache.</summary>
    public int CacheHits => 0;

    public async Task<IPageHandle> NewPageAsync(CancellationToken ct)
    {
        var page = await context.NewPageAsync();
        await page.RouteAsync("**/*", HandleRouteAsync);
        return new PlaywrightPageHandle(page);
    }

    private async Task HandleRouteAsync(IRoute route)
    {
        var request = route.Request;
        if (request.Url.StartsWith(site.DownloadBase, StringComparison.Ordinal))
        {
            var dl = site.DownloadResponse(request.Url); // fulfil the same-origin download in-process (bytes + Content-Disposition: attachment)
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = dl.Status,
                ContentType = dl.ContentType,
                Headers = dl.Headers,
                BodyBytes = dl.Body,
            });
            return;
        }

        if (!request.Url.StartsWith(FixtureSite.Origin, StringComparison.Ordinal))
        {
            await route.AbortAsync(); // default-deny: only the canonical fixture origin is served (the download base lives under it)
            return;
        }

        var response = site.Respond(request.Method, request.Url, request.PostData);
        var options = new RouteFulfillOptions
        {
            Status = response.Status,
            ContentType = response.ContentType,
            BodyBytes = response.Body,
        };
        if (response.Headers is not null)
        {
            options.Headers = response.Headers;
        }

        await route.FulfillAsync(options);
    }

    public async ValueTask DisposeAsync() => await context.CloseAsync();
}
