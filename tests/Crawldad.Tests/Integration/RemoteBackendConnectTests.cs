using System.Collections.Generic;
using Crawldad.Tests.Support;
using Crawldad.Web.Infrastructure.Browser;
using Crawldad.Web.Infrastructure.Browser.Real;
using Crawldad.Web.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>
/// The native (<c>"browserless"</c>) and CDP (<c>"browserbase"</c>) adapters, executed against loopback servers only —
/// a local Playwright <c>run-server</c> for native connect and a locally launched CDP endpoint for connectOverCDP, with
/// the Browserbase session-create call answered by a local stub. <b>No live third-party traffic.</b> Covers both
/// credential modes, the region tag, and — the security gate — that a connect failure surfaces a terminal
/// <see cref="BrowserConnectException"/> whose message leaks neither the token nor the apiKey-embedding connect URL.
/// </summary>
[Collection(RealChromiumCollection.Name)]
public sealed class RemoteBackendConnectTests(RealChromiumFixture fixture) : IDisposable
{
    private static readonly CancellationToken _ct = CancellationToken.None;
    private readonly ServiceProvider _services = new ServiceCollection().AddHttpClient().BuildServiceProvider();

    // No run scope is opened here (these tests call ConnectAsync directly, not via POST /runs), so Register is a no-op —
    // enough to satisfy the adapter constructor; the leak suite exercises the registering path through the endpoint.
    private readonly IRunSecretScope _scope = new AmbientRunSecretScope();

    private IHttpClientFactory Http => _services.GetRequiredService<IHttpClientFactory>();

    public void Dispose() => _services.Dispose();

    private static LocalSite PageSite() => new LocalSite()
        .Map("/x.html", "text/html", "<html><body><h1 id='h'>connected</h1></body></html>");

    // ----- browserless (native connect) --------------------------------------

    [Fact]
    public async Task Browserless_connects_natively_and_drives_a_page()
    {
        using var runServer = await RealChromiumFixture.StartRunServerAsync();
        using var site = PageSite();
        var backend = new BrowserlessBackend(
            fixture.Provider, new FixedSecretStore("token-value"), _scope, new InMemoryAssetCache(),
            new ThrottleGate(TimeProvider.System), runServer.WsBase);
        var binding = new BackendBinding("browserless", "cred-ref",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["region"] = "lon" });

        await using var session = await backend.ConnectAsync(binding, SessionPolicy.Default, _ct);
        session.Region.ShouldBe("lon"); // region option flows through to the region tag

        var page = await session.NewPageAsync(_ct);
        await page.GotoAsync(site.Url("/x.html"), null, null, _ct);
        (await page.Locator("#h").TextContentAsync(_ct)).ShouldBe("connected");
    }

    [Fact]
    public async Task Browserless_honours_a_cancelled_token()
    {
        var backend = new BrowserlessBackend(
            fixture.Provider, new FixedSecretStore("t"), _scope, new InMemoryAssetCache(),
            new ThrottleGate(TimeProvider.System), BrowserlessBackend.DefaultEndpointTemplate);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => backend.ConnectAsync(new BackendBinding("browserless", "cred-ref"), SessionPolicy.Default, cts.Token));
    }

    [Fact]
    public async Task Browserless_connect_failure_is_a_scrubbed_terminal()
    {
        var backend = new BrowserlessBackend(
            fixture.Provider, new FixedSecretStore("SECRET_TOKEN_XYZ"), _scope, new InMemoryAssetCache(),
            new ThrottleGate(TimeProvider.System), "ws://127.0.0.1:1/chromium/playwright");

        var ex = await Should.ThrowAsync<BrowserConnectException>(
            () => backend.ConnectAsync(new BackendBinding("browserless", "cred-ref"), SessionPolicy.Default, _ct));

        ex.Message.ShouldNotContain("SECRET_TOKEN_XYZ"); // the token never surfaces
        ex.Message.ShouldNotContain("127.0.0.1");        // nor the connect URL
    }

    [Fact]
    public async Task Browserless_requires_a_credential_ref()
    {
        var backend = new BrowserlessBackend(
            fixture.Provider, new FixedSecretStore("t"), _scope, new InMemoryAssetCache(),
            new ThrottleGate(TimeProvider.System), BrowserlessBackend.DefaultEndpointTemplate);

        await Should.ThrowAsync<BrowserConnectException>(
            () => backend.ConnectAsync(new BackendBinding("browserless"), SessionPolicy.Default, _ct));
    }

    // ----- browserbase (CDP) -------------------------------------------------

    private BrowserbaseBackend Browserbase(string secret, string apiBaseUrl) => new(
        fixture.Provider, new FixedSecretStore(secret), _scope, Http, new InMemoryAssetCache(),
        new ThrottleGate(TimeProvider.System), apiBaseUrl);

    [Fact]
    public async Task Browserbase_connectUrl_mode_connects_over_cdp()
    {
        await using var cdp = await fixture.LaunchCdpChromiumAsync();
        using var site = PageSite();
        var backend = Browserbase(cdp.Endpoint, BrowserbaseBackend.DefaultApiBaseUrl);
        var binding = new BackendBinding("browserbase", "cred-ref", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["mode"] = BrowserbaseBackend.ConnectUrlMode,
            ["region"] = "eu-central-1",
        });

        await using var session = await backend.ConnectAsync(binding, SessionPolicy.Default, _ct);
        session.Region.ShouldBe("eu-central-1"); // from options

        var page = await session.NewPageAsync(_ct);
        await page.GotoAsync(site.Url("/x.html"), null, null, _ct);
        (await page.Locator("#h").TextContentAsync(_ct)).ShouldBe("connected");
    }

    [Fact]
    public async Task Browserbase_connectUrl_mode_defaults_region_to_unknown()
    {
        await using var cdp = await fixture.LaunchCdpChromiumAsync();
        var backend = Browserbase(cdp.Endpoint, BrowserbaseBackend.DefaultApiBaseUrl);
        var binding = new BackendBinding("browserbase", "cred-ref", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["mode"] = BrowserbaseBackend.ConnectUrlMode,
        });

        await using var session = await backend.ConnectAsync(binding, SessionPolicy.Default, _ct);
        session.Region.ShouldBe("unknown");
    }

    [Fact]
    public async Task Browserbase_apiKey_mode_creates_a_session_then_connects()
    {
        await using var cdp = await fixture.LaunchCdpChromiumAsync();
        using var api = new LocalSite().Map("/v1/sessions", "application/json",
            $$"""{"connectUrl":"{{cdp.Endpoint}}","region":"us-east-1","expiresAt":"2099-01-01T00:00:00Z"}""");

        var backend = Browserbase("bb_live_apikey", api.BaseUrl.TrimEnd('/'));
        // projectId present ⇒ covers the option-carrying request body branch.
        var binding = new BackendBinding("browserbase", "cred-ref", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["projectId"] = "proj_123",
        });

        await using var session = await backend.ConnectAsync(binding, SessionPolicy.Default, _ct);
        session.Region.ShouldBe("us-east-1"); // from the session-create response
        api.Hits("/v1/sessions").ShouldBe(1);
    }

    [Fact]
    public async Task Browserbase_apiKey_mode_defaults_region_when_the_response_omits_it()
    {
        await using var cdp = await fixture.LaunchCdpChromiumAsync();
        using var api = new LocalSite().Map("/v1/sessions", "application/json",
            $$"""{"connectUrl":"{{cdp.Endpoint}}","expiresAt":"2099-01-01T00:00:00Z"}""");

        var backend = Browserbase("bb_live_apikey", api.BaseUrl.TrimEnd('/'));
        await using var session = await backend.ConnectAsync(new BackendBinding("browserbase", "cred-ref"), SessionPolicy.Default, _ct);
        session.Region.ShouldBe("unknown");
    }

    [Fact]
    public async Task Browserbase_apiKey_mode_empty_body_is_a_scrubbed_terminal()
    {
        using var api = new LocalSite().Map("/v1/sessions", "application/json", "null"); // JSON null ⇒ empty session
        var backend = Browserbase("bb_live_apikey", api.BaseUrl.TrimEnd('/'));

        await Should.ThrowAsync<BrowserConnectException>(
            () => backend.ConnectAsync(new BackendBinding("browserbase", "cred-ref"), SessionPolicy.Default, _ct));
    }

    [Fact]
    public async Task Browserbase_connect_failure_does_not_leak_the_apiKey_embedding_connect_url()
    {
        // The ship-blocker: connectUrl mode's secret embeds the account apiKey. A failed connect must scrub it.
        const string Leaky = "ws://127.0.0.1:1/?apiKey=bb_live_LEAKME&sessionId=ses_x";
        var backend = Browserbase(Leaky, BrowserbaseBackend.DefaultApiBaseUrl);
        var binding = new BackendBinding("browserbase", "cred-ref", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["mode"] = BrowserbaseBackend.ConnectUrlMode,
        });

        var ex = await Should.ThrowAsync<BrowserConnectException>(
            () => backend.ConnectAsync(binding, SessionPolicy.Default, _ct));

        ex.Message.ShouldNotContain("bb_live_LEAKME");
        ex.Message.ShouldNotContain("apiKey");
    }

    [Fact]
    public async Task Browserbase_requires_a_credential_ref()
    {
        var backend = Browserbase("x", BrowserbaseBackend.DefaultApiBaseUrl);
        await Should.ThrowAsync<BrowserConnectException>(
            () => backend.ConnectAsync(new BackendBinding("browserbase"), SessionPolicy.Default, _ct));
    }

    [Fact]
    public async Task Browserbase_honours_a_cancelled_token()
    {
        var backend = Browserbase("x", BrowserbaseBackend.DefaultApiBaseUrl);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => backend.ConnectAsync(new BackendBinding("browserbase", "cred-ref"), SessionPolicy.Default, cts.Token));
    }
}
