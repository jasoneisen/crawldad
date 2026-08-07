using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Runs;
using Crawldad.Web.Infrastructure.Browser.Real;
using Crawldad.Web.Infrastructure.Security;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Npgsql;

namespace Crawldad.Tests.Integration;

/// <summary>
/// The Phase 4 WP3 security gate (§12, CRAWLDAD_PLAN.md Phase 4): a run whose backend binding carries a token /
/// connectUrl asserts that string appears in <b>no</b> event, projection row, log line, or HTTP response body. Both real
/// WP1 remote adapters are driven against loopback servers only (a local Playwright <c>run-server</c> for
/// <c>browserless</c>, a local CDP endpoint + a session-create stub for <c>browserbase</c>) — <b>zero live third-party
/// traffic</b> — with the resolved credential a distinctive sentinel. The payload adversarially interpolates the
/// sentinel into a <c>log</c> message and the shaped <c>result</c> (simulating a page that echoes it), so the exact-match
/// scrub is exercised at every sink; a failure-path variant carries the sentinel in a bad-port connect URL.
/// </summary>
[Collection(RealChromiumCollection.Name)]
public sealed class CredentialLeakTests(RealChromiumFixture chromium, LeakHost leak) : IClassFixture<LeakHost>
{
    // A log node echoing the resolved secret + a result that echoes it both raw and inside a token= param.
    private const string _echoPayload =
        """
        { "name": "leak-{name}", "config": { "backend": "input.backend" }, "vars": {},
          "steps": [ { "log": { "level": "info", "message": "the page echoed ${input.echo}" } } ],
          "result": "{ scraped: input.echo, echoedUrl: 'wss://x?token=' + input.echo }" }
        """;

    private static JsonObject Body(string payloadJson, JsonObject inputs) =>
        new() { ["payload"] = JsonNode.Parse(payloadJson), ["inputs"] = inputs };

    private static JsonObject Backend(string adapter, string credentialRef, JsonObject? options = null) => new()
    {
        ["adapter"] = adapter,
        ["credentialRef"] = credentialRef,
        ["options"] = options ?? new JsonObject(),
    };

    [Fact]
    public async Task Browserless_token_run_leaks_the_token_into_no_sink()
    {
        var host = await leak.EnsureAsync(chromium);
        leak.Capturer.Clear();

        var inputs = new JsonObject
        {
            ["backend"] = Backend("browserless", LeakHost.BrowserlessRef, new JsonObject { ["region"] = "lon" }),
            ["echo"] = LeakHost.TokenSentinel,
        };

        var root = await PostAsync(host, Body(_echoPayload.Replace("{name}", "browserless", StringComparison.Ordinal), inputs));

        root.GetProperty("status").GetString().ShouldBe("succeeded");
        // The result echoed the token raw and inside a token= param — both redacted.
        var result = root.GetProperty("result");
        result.GetProperty("scraped").GetString().ShouldBe(CredentialScrubber.Redaction);
        result.GetProperty("echoedUrl").GetString().ShouldBe($"wss://x?token={CredentialScrubber.Redaction}");

        await AssertRunLeaksNothingAsync(host, root, LeakHost.TokenSentinel);
    }

    [Fact]
    public async Task Browserbase_apiKey_run_leaks_the_key_into_no_sink()
    {
        var host = await leak.EnsureAsync(chromium);
        leak.Capturer.Clear();

        var inputs = new JsonObject
        {
            ["backend"] = Backend("browserbase", LeakHost.BrowserbaseRef, new JsonObject { ["projectId"] = "proj_leak" }),
            ["echo"] = LeakHost.ApiKeySentinel,
        };

        var root = await PostAsync(host, Body(_echoPayload.Replace("{name}", "browserbase", StringComparison.Ordinal), inputs));

        root.GetProperty("status").GetString().ShouldBe("succeeded");
        root.GetProperty("result").GetProperty("scraped").GetString().ShouldBe(CredentialScrubber.Redaction);

        await AssertRunLeaksNothingAsync(host, root, LeakHost.ApiKeySentinel);
    }

    [Fact]
    public async Task A_failed_connect_carrying_the_credential_leaks_into_no_sink()
    {
        var host = await leak.EnsureAsync(chromium);
        leak.Capturer.Clear();

        // connectUrl mode: the whole (bad-port) URL is the secret and embeds the apiKey sentinel; the connect fails.
        var inputs = new JsonObject
        {
            ["backend"] = Backend("browserbase", LeakHost.ConnectUrlRef, new JsonObject { ["mode"] = BrowserbaseBackend.ConnectUrlMode }),
            ["echo"] = LeakHost.ApiKeySentinel,
        };

        var root = await PostAsync(host, Body(_echoPayload.Replace("{name}", "failing", StringComparison.Ordinal), inputs));

        root.GetProperty("status").GetString().ShouldBe("failed");
        var failure = root.GetProperty("failure");
        failure.GetProperty("code").GetString().ShouldBe("backend_unavailable");
        failure.GetProperty("message").GetString()!.ShouldNotContain(LeakHost.ApiKeySentinel); // scrubbed by construction

        await AssertRunLeaksNothingAsync(host, root, LeakHost.ApiKeySentinel);
    }

    [Fact]
    public async Task Framework_style_log_carrying_a_connect_string_is_scrubbed()
    {
        // The positive control for the log-pipeline decorator: an arbitrary framework category that echoes a connect
        // string is scrubbed just like an application log — proving the factory decoration is wired, not that no log ever
        // happened to contain the sentinel.
        var host = await leak.EnsureAsync(chromium);
        leak.Capturer.Clear();

        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Playwright.Connect");
        logger.LogError("connecting to wss://production-lon.browserless.io/chromium/playwright?token={Token}", LeakHost.TokenSentinel);

        var lines = leak.Capturer.Lines;
        lines.ShouldContain(line => line.Contains(CredentialScrubber.Redaction, StringComparison.Ordinal));
        lines.ShouldNotContain(line => line.Contains(LeakHost.TokenSentinel, StringComparison.Ordinal));
    }

    // ----- the sink sweep -----------------------------------------------------

    private async Task AssertRunLeaksNothingAsync(IAlbaHost host, JsonElement responseRoot, string sentinel)
    {
        var runId = responseRoot.GetProperty("runId").GetGuid();

        // (d) HTTP response body.
        responseRoot.GetRawText().ShouldNotContain(sentinel);

        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();

        // (a) every event in the run's stream (typed data re-serialised to JSON).
        var events = await session.Events.FetchStreamAsync(runId);
        var eventsJson = string.Join('\n', events.Select(e => JsonSerializer.Serialize(e.Data, e.Data.GetType())));
        eventsJson.ShouldNotContain(sentinel);

        // (b) the projection/document row.
        var run = await session.LoadAsync<Run>(runId);
        run.ShouldNotBeNull();
        JsonSerializer.Serialize(run).ShouldNotContain(sentinel);

        // (a, raw) the raw JSON in the DB — independent of Marten's deserialisation.
        (await DumpRawStorageAsync(host)).ShouldNotContain(sentinel);

        // (c) every captured log line for the run (framework categories included).
        string.Join('\n', leak.Capturer.Lines).ShouldNotContain(sentinel);
    }

    private static async Task<JsonElement> PostAsync(IAlbaHost host, JsonObject body)
    {
        var result = await host.Scenario(x =>
        {
            x.Post.Json(body).ToUrl("/runs");
            x.StatusCodeShouldBe(200);
        });
        return await result.ReadAsJsonAsync<JsonElement>();
    }

    private static async Task<string> DumpRawStorageAsync(IAlbaHost host)
    {
        var connectionString = host.Services.GetRequiredService<IConfiguration>().GetConnectionString("marten")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var dump = new StringBuilder();
        await AppendRowsAsync(connection, "select data::text from crawldad_leak.mt_events", dump);
        await AppendRowsAsync(connection, "select data::text from crawldad_leak.mt_doc_run", dump);
        return dump.ToString();
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Both SQL strings are compile-time constants scanning fixed Marten tables in the leak-test schema; no user input reaches the command.")]
    private static async Task AppendRowsAsync(NpgsqlConnection connection, string sql, StringBuilder dump)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            dump.AppendLine(reader.GetString(0));
        }
    }
}

/// <summary>
/// A single Alba host for the leak suite, wired with the real remote adapters pointed at loopback servers (a Playwright
/// <c>run-server</c>, a local CDP endpoint + a session-create stub), a <see cref="MapSecretStore"/> resolving a distinct
/// sentinel per credential mode, and a <see cref="CapturingLoggerProvider"/> on the (scrubbing-decorated) logging
/// pipeline. Built lazily on first use so it can reuse the collection's shared Playwright driver; disposed with the
/// class. <b>No live third-party traffic.</b>
/// </summary>
public sealed class LeakHost : IAsyncLifetime
{
    /// <summary>The Marten schema the leak host isolates its streams/docs into (also scanned raw by the sink sweep).</summary>
    internal const string SchemaName = "crawldad_leak";

    /// <summary>A distinctive Browserless account-token sentinel (the resolved <c>browserless</c> credential).</summary>
    internal const string TokenSentinel = "brwsrless_LEAKCANARY_token_0123456789abcdefABCDEF";

    /// <summary>A distinctive Browserbase apiKey sentinel (the resolved <c>browserbase</c> credential).</summary>
    internal const string ApiKeySentinel = "bb_live_LEAKCANARY_apikey_0123456789abcdefABCDEF";

    internal const string BrowserlessRef = "browserless-cred";
    internal const string BrowserbaseRef = "browserbase-cred";
    internal const string ConnectUrlRef = "browserbase-connecturl-cred";

    private RunServerHandle? _runServer;
    private CdpChromium? _cdp;
    private LocalSite? _stub;
    private IAlbaHost? _host;

    /// <summary>Captures every rendered log line (post-scrub) so the sweep can assert no credential reaches a sink.</summary>
    internal CapturingLoggerProvider Capturer { get; } = new();

    public Task InitializeAsync() => Task.CompletedTask; // built lazily once the shared driver is available

    /// <summary>Builds the leak host on first call (reusing the collection's shared Playwright driver), then returns it.</summary>
    /// <param name="chromium">The shared real-Chromium fixture (its driver + CDP-launch helper).</param>
    /// <returns>The leak host.</returns>
    public async Task<IAlbaHost> EnsureAsync(RealChromiumFixture chromium)
    {
        if (_host is not null)
        {
            return _host;
        }

        _runServer = await RealChromiumFixture.StartRunServerAsync();
        _cdp = await chromium.LaunchCdpChromiumAsync();
        _stub = new LocalSite().Map("/v1/sessions", "application/json",
            $$"""{"connectUrl":"{{_cdp.Endpoint}}","region":"us-east-1","expiresAt":"2099-01-01T00:00:00Z"}""");

        var secrets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BrowserlessRef] = TokenSentinel,
            [BrowserbaseRef] = ApiKeySentinel,
            // connectUrl mode: the whole URL is the secret and embeds the apiKey sentinel; a closed port makes it fail.
            [ConnectUrlRef] = $"ws://127.0.0.1:1/?apiKey={ApiKeySentinel}&sessionId=ses_leakcanary",
        };

        _host = await AlbaHost.For<Program>(builder =>
        {
            builder.UseCrawldadTestDefaults(SchemaName);
            builder.UseSetting("Crawldad:Browserless:EndpointTemplate", _runServer.WsBase);
            builder.UseSetting("Crawldad:Browserbase:ApiBaseUrl", _stub.BaseUrl.TrimEnd('/'));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(new FakeClock());
                services.AddSingleton<ISecretStore>(new MapSecretStore(secrets));
                // Reuse the one shared driver (a non-disposing wrapper, so the host never disposes the fixture's driver).
                services.AddSingleton<IPlaywrightProvider>(new SharedPlaywrightProvider(chromium.Provider));
                services.AddSingleton<ILoggerProvider>(Capturer);
            });
        });

        await _host.ResetAllMartenDataAsync();
        return _host;
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        _stub?.Dispose();
        if (_cdp is not null)
        {
            await _cdp.DisposeAsync();
        }

        _runServer?.Dispose();
    }
}

/// <summary>A non-owning <see cref="IPlaywrightProvider"/> wrapper: forwards to the shared driver but is not disposable,
/// so the leak host never tears down the collection fixture's driver.</summary>
/// <param name="inner">The shared driver.</param>
internal sealed class SharedPlaywrightProvider(IPlaywrightProvider inner) : IPlaywrightProvider
{
    public ValueTask<IPlaywright> GetAsync(CancellationToken ct) => inner.GetAsync(ct);
}
