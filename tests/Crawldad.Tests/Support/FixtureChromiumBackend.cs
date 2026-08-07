using System.Diagnostics.CodeAnalysis;
using System.IO;
using Crawldad.Web.Infrastructure.Browser;
using Crawldad.Web.Infrastructure.Browser.Real;
using Microsoft.Playwright;

namespace Crawldad.Tests.Support;

/// <summary>
/// The Phase 4 WP2 parity backend: registered as the <c>"local"</c> adapter in the parity host, so the acceptance
/// payloads bind to it exactly as they would the product local adapter (<c>inputs.backend = { adapter: "local" }</c>)
/// and run through the real <c>POST /runs</c> path and interpreter. It launches ONE shared headless Chromium with the
/// payload's §8.1 launch args (<c>--disable-web-security</c>) and, per connect, opens a fresh context (per-run
/// isolation, §12) whose pages are served by a <see cref="FixtureSite"/> for the fixture named in
/// <c>binding.Options["fixture"]</c> — the same fixture directory the record/replay fake reads.
/// <para>
/// It deliberately substitutes a fixture-fulfilling route for the product adapter's network route: the §8.1 route
/// policy (block/cache/throttle) is exercised honestly by <c>LocalBackendTests</c> (WP1); here the goal is
/// engine-output parity (fake ≡ real), and the canonical throttle (2 s per request) is skipped so the suite stays
/// tolerable — the legitimate test-time throttle override the plan calls for. The shared browser is reused across every
/// parity run (built once) and disposed with the host.
/// </para>
/// </summary>
/// <param name="provider">The shared Playwright driver (a host singleton).</param>
/// <param name="fixturesRoot">The fixtures root (the test output's copied corpus).</param>
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

/// <summary>
/// One parity run's session: a real Chromium <see cref="IBrowserContext"/> whose every page is served by
/// <paramref name="site"/> via a <c>page.RouteAsync</c> fulfilment handler. Everything on the canonical Accela origin is
/// answered from the fixture; anything else is aborted, so <b>no request can reach the live site</b> (default-deny).
/// Pages are wrapped in the product <see cref="PlaywrightPageHandle"/>, so the interpreter drives real Chromium through
/// the identical seam the product adapters use.
/// </summary>
/// <param name="context">The per-run context (closed on dispose; the browser is shared and outlives it).</param>
/// <param name="site">The fixture-site state machine answering this session's requests.</param>
internal sealed class FixtureBrowserSession(IBrowserContext context, FixtureSite site) : IBrowserSession
{
    /// <summary>The parity region tag — the local adapter's constant (§9.1).</summary>
    public string Region => "local";

    /// <summary>Always 0 — the fixture route does not model the cross-run asset cache (that is WP1's LocalBackendTests).</summary>
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
            await route.ContinueAsync(); // the loopback download listener answers this — a genuine, readable download
            return;
        }

        if (!request.Url.StartsWith(FixtureSite.Origin, StringComparison.Ordinal))
        {
            await route.AbortAsync(); // default-deny: only the canonical fixture origin and the loopback listener are served
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

    public async ValueTask DisposeAsync()
    {
        await context.CloseAsync();
        site.Dispose();
    }
}
