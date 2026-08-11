using System.Collections.Generic;
using Crawldad.Tests.Support;
using Crawldad.Web.Infrastructure.Browser;
using Crawldad.Web.Infrastructure.Browser.Real;
using Crawldad.Web.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>The native (<c>"browserless"</c>) and CDP (<c>"browserbase"</c>) adapters, exercised only against loopback
/// servers. Covers both credential modes, the region tag, and that a connect failure raises a
/// <see cref="BrowserConnectException"/> whose message leaks neither the token nor the connect URL.</summary>
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
            fixture.Provider, new FixedConnectResolver("token-value"), _scope, new InMemoryAssetCache(),
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
            fixture.Provider, new FixedConnectResolver("t"), _scope, new InMemoryAssetCache(),
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
            fixture.Provider, new FixedConnectResolver("SECRET_TOKEN_XYZ"), _scope, new InMemoryAssetCache(),
            new ThrottleGate(TimeProvider.System), "ws://127.0.0.1:1/chromium/playwright");

        var ex = await Should.ThrowAsync<BrowserConnectException>(
            () => backend.ConnectAsync(new BackendBinding("browserless", "cred-ref"), SessionPolicy.Default, _ct));

        ex.Message.ShouldNotContain("SECRET_TOKEN_XYZ"); // the token never surfaces
        ex.Message.ShouldNotContain("127.0.0.1");        // nor the connect URL
        ex.Retryable.ShouldBeTrue();                     // a refused socket is a transient blip — retryable under connectRetry
    }

    [Fact]
    public async Task Browserless_requires_a_credential_ref()
    {
        var backend = new BrowserlessBackend(
            fixture.Provider, new FixedConnectResolver("t"), _scope, new InMemoryAssetCache(),
            new ThrottleGate(TimeProvider.System), BrowserlessBackend.DefaultEndpointTemplate);

        await Should.ThrowAsync<BrowserConnectException>(
            () => backend.ConnectAsync(new BackendBinding("browserless"), SessionPolicy.Default, _ct));
    }

    [Fact]
    public async Task Browserless_registers_the_resolved_secret_for_scrubbing()
    {
        // The resolved credential must be registered into the run's secret scope BEFORE connect, so every sink scrubs it
        // exactly as a config-resolved one — the invariant the registration store inherits. A dead port fails connect after.
        var scope = new AmbientRunSecretScope();
        using var handle = scope.Begin();
        var backend = new BrowserlessBackend(
            fixture.Provider, new FixedConnectResolver("SENTINEL_scrub_token_0123456789"), scope, new InMemoryAssetCache(),
            new ThrottleGate(TimeProvider.System), "ws://127.0.0.1:1/chromium/playwright");

        await Should.ThrowAsync<BrowserConnectException>(
            () => backend.ConnectAsync(new BackendBinding("browserless", "cred-ref"), SessionPolicy.Default, _ct));

        scope.Current.ShouldContain("SENTINEL_scrub_token_0123456789");
    }

    // ----- browserbase (CDP) -------------------------------------------------

    private BrowserbaseBackend Browserbase(string secret, string apiBaseUrl) => new(
        fixture.Provider, new FixedConnectResolver(secret), _scope, Http, new InMemoryAssetCache(),
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

        var ex = await Should.ThrowAsync<BrowserConnectException>(
            () => backend.ConnectAsync(new BackendBinding("browserbase", "cred-ref"), SessionPolicy.Default, _ct));
        ex.Retryable.ShouldBeFalse(); // a 200 with an empty body is a permanent contract fault, not a transient blip
    }

    [Fact]
    public async Task Browserbase_apiKey_mode_4xx_from_the_session_api_is_a_non_retryable_terminal()
    {
        // A rejected key: the session API answers 4xx BEFORE any CDP connect — an auth-shaped fault that fails fast.
        using var api = new LocalSite().Map("/v1/sessions", "application/json", "{}", status: 401);
        var backend = Browserbase("bb_live_rejected", api.BaseUrl.TrimEnd('/'));

        var ex = await Should.ThrowAsync<BrowserConnectException>(
            () => backend.ConnectAsync(new BackendBinding("browserbase", "cred-ref"), SessionPolicy.Default, _ct));
        ex.Retryable.ShouldBeFalse();
    }

    [Fact]
    public async Task Browserbase_apiKey_mode_5xx_from_the_session_api_is_a_retryable_terminal()
    {
        // A transient server-side fault from the session API — retryable under connectRetry.
        using var api = new LocalSite().Map("/v1/sessions", "application/json", "{}", status: 503);
        var backend = Browserbase("bb_live_apikey", api.BaseUrl.TrimEnd('/'));

        var ex = await Should.ThrowAsync<BrowserConnectException>(
            () => backend.ConnectAsync(new BackendBinding("browserbase", "cred-ref"), SessionPolicy.Default, _ct));
        ex.Retryable.ShouldBeTrue();
    }

    [Fact]
    public async Task Browserbase_connect_failure_does_not_leak_the_apiKey_embedding_connect_url()
    {
        // connectUrl mode: the whole URL is the secret, so an apiKey-bearing URL must still be scrubbed on a failed connect.
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
