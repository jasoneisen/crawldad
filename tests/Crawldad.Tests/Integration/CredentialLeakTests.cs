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
using Crawldad.Web.Infrastructure.Storage;
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

    // The async/checkpoint variant: the secret is snapshotted into a var AND the checkpoint cursor, so the DURABLE state a
    // resumed run restores from (the RunProgress checkpoint) is exercised at the scrub boundary alongside the trace + result.
    private const string _asyncCheckpointPayload =
        """
        { "name": "leak-async", "config": { "backend": "input.backend" }, "vars": { "leaked": "input.echo" },
          "steps": [
            { "loop": { "maxIterations": 2, "while": "false", "do": [
                { "checkpoint": { "name": "cp", "cursor": "leaked", "resume": [] } },
                { "log": { "level": "info", "message": "the page echoed ${input.echo}" } }
            ] } }
          ],
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
    public async Task Browserless_async_run_with_a_checkpoint_leaks_the_token_into_no_sink()
    {
        var host = await leak.EnsureAsync(chromium);
        leak.Capturer.Clear();

        var inputs = new JsonObject
        {
            ["backend"] = Backend("browserless", LeakHost.BrowserlessRef, new JsonObject { ["region"] = "lon" }),
            ["echo"] = LeakHost.TokenSentinel,
        };

        // Drive the run through the background executor saga (async), so the durable checkpoint + RunProgress result are
        // written at the scrub boundary too — the state a resumed run would restore from.
        var body = Body(_asyncCheckpointPayload, inputs);
        body["async"] = true;
        var accepted = await host.Scenario(x =>
        {
            x.Post.Json(body).ToUrl("/runs");
            x.StatusCodeShouldBe(202);
        });
        var runId = (await accepted.ReadAsJsonAsync<JsonElement>()).GetProperty("runId").GetGuid();
        var root = await DurableHost.PollUntilTerminalAsync(host, runId, TimeSpan.FromSeconds(30));

        root.GetProperty("status").GetString().ShouldBe("succeeded");
        root.GetProperty("result").GetProperty("scraped").GetString().ShouldBe(CredentialScrubber.Redaction);
        await AssertRunLeaksNothingAsync(host, root, LeakHost.TokenSentinel);
    }

    // A failing async run: connect (browserless), then fail with the secret in scope — so the executor captures a real
    // failure screenshot (§13), exercising the screenshot ref/blob at the scrub boundary (not a vacuously-empty store).
    private const string _asyncScreenshotFailPayload =
        """
        { "name": "leak-shotfail", "config": { "backend": "input.backend" }, "vars": { "leaked": "input.echo" },
          "steps": [
            { "goto": { "url": "about:blank" } },
            { "fail": { "class": "terminal", "code": "boom", "message": "the page held ${input.echo}" } }
          ],
          "result": "'x'" }
        """;

    [Fact]
    public async Task Browserless_async_failing_run_captures_a_clean_screenshot_and_leaks_nothing()
    {
        var host = await leak.EnsureAsync(chromium);
        leak.Capturer.Clear();

        var inputs = new JsonObject
        {
            ["backend"] = Backend("browserless", LeakHost.BrowserlessRef, new JsonObject { ["region"] = "lon" }),
            ["echo"] = LeakHost.TokenSentinel,
        };
        var body = Body(_asyncScreenshotFailPayload, inputs);
        body["async"] = true;
        var accepted = await host.Scenario(x =>
        {
            x.Post.Json(body).ToUrl("/runs");
            x.StatusCodeShouldBe(202);
        });
        var runId = (await accepted.ReadAsJsonAsync<JsonElement>()).GetProperty("runId").GetGuid();
        var root = await DurableHost.PollUntilTerminalAsync(host, runId, TimeSpan.FromSeconds(30));

        root.GetProperty("status").GetString().ShouldBe("failed");

        // A screenshot WAS captured on the failing step (a real ref, not a vacuously-empty store) — and it leaks nothing:
        // the ref is content-addressed and the sweep below re-asserts the store's keys + bytes are clean.
        var screenshots = (InMemoryScreenshotStore)host.Services.GetRequiredService<IScreenshotStore>();
        screenshots.Blobs.ShouldNotBeEmpty();

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

    // A minimal async run whose backend credential travels ONLY by reference (no input echoes it) — the probe for the
    // durable orchestration state at rest (the saga document + Wolverine envelopes).
    private const string _byRefPayload =
        """
        { "name": "durable-byref", "config": { "backend": "input.backend" }, "vars": {},
          "steps": [ { "goto": { "url": "about:blank" } } ],
          "result": "'ok'" }
        """;

    [Fact]
    public async Task An_async_by_reference_run_keeps_the_resolved_secret_out_of_the_saga_and_wolverine_envelopes()
    {
        var host = await leak.EnsureAsync(chromium);
        leak.Capturer.Clear();

        // The credential travels ONLY by reference (DurableRef → DurableSecretSentinel, resolved at connect); no input
        // echoes the secret, so a hit anywhere at rest is a real by-reference-model breach.
        var inputs = new JsonObject { ["backend"] = Backend("browserless", LeakHost.DurableRef, new JsonObject { ["region"] = "lon" }) };
        var body = Body(_byRefPayload, inputs);
        body["async"] = true;

        var accepted = await host.Scenario(x =>
        {
            x.Post.Json(body).ToUrl("/runs");
            x.StatusCodeShouldBe(202);
        });
        var runId = (await accepted.ReadAsJsonAsync<JsonElement>()).GetProperty("runId").GetGuid();
        var root = await DurableHost.PollUntilTerminalAsync(host, runId, TimeSpan.FromSeconds(30));
        root.GetProperty("status").GetString().ShouldBe("succeeded");

        // The scrubbed sinks (events, projections, run-progress, SSE, timeline, screenshots, logs, response) stay clean too.
        await AssertRunLeaksNothingAsync(host, root, LeakHost.DurableSecretSentinel);

        // The RunExecutorSaga LINGERS after the run finished — it is never marked complete — and holds the run's
        // inputs+script at rest, but by reference: the credentialRef IS stored, the resolved secret is NOT (§12).
        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession())
        {
            var saga = await session.LoadAsync<RunExecutorSaga>(runId);
            saga.ShouldNotBeNull();                                         // still at rest post-completion (the retention finding)
            saga.Inputs.ShouldContain(LeakHost.DurableRef);                  // the reference is persisted…
            saga.Inputs.ShouldNotContain(LeakHost.DurableSecretSentinel);    // …the resolved secret is not
        }

        // The saga document + every Wolverine durable envelope body (StartRun inputs+script, ExecuteRun, the scheduled
        // RunDeadline, dead-letters) carry no resolved secret — the durable at-rest surfaces the earlier sweep omitted.
        (await DumpDurableStateAsync(host)).ShouldNotContain(LeakHost.DurableSecretSentinel);
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

        // (e, WP3) the SSE stream: the full backfilled tail carries only scrubbed event data (closes the deferred P4 gap).
        var frames = await SseReader.ReadToCloseAsync(host, runId, lastEventId: null, TimeSpan.FromSeconds(20));
        string.Join('\n', frames.Select(f => f.Data)).ShouldNotContain(sentinel);

        // (f, WP3) the RunTimeline projection row (steps, extracted refs, region, failure/screenshot ref).
        var timeline = await host.Scenario(x =>
        {
            x.Get.Url($"/runs/{runId}/timeline");
            x.StatusCodeShouldBe(200);
        });
        (await timeline.ReadAsJsonAsync<JsonElement>()).GetRawText().ShouldNotContain(sentinel);

        // (g, WP3) the screenshot blob store: no ref (key) nor captured byte carries the credential (§12/§13).
        var screenshots = (InMemoryScreenshotStore)host.Services.GetRequiredService<IScreenshotStore>();
        string.Join('\n', screenshots.Blobs.Keys).ShouldNotContain(sentinel);
        screenshots.Blobs.Values.ShouldAllBe(bytes => !Encoding.UTF8.GetString(bytes).Contains(sentinel, StringComparison.Ordinal));
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
        // The executor-owned run-progress store holds the durable checkpoint (cursor + var snapshot) and the run result an
        // async/resumed run persists — swept here too so the §12 no-leak invariant covers the resume path.
        await AppendRowsAsync(connection, "select data::text from crawldad_leak.mt_doc_runprogress", dump);
        return dump.ToString();
    }

    // The durable ORCHESTRATION state a run leaves at rest (distinct from the scrubbed Marten sinks above): the
    // RunExecutorSaga document (inputs+script, which lingers after completion) and Wolverine's durable envelope tables —
    // incoming (StartRun's inputs+script, ExecuteRun, the scheduled RunDeadline), outgoing, and dead-letters. Envelope
    // bodies are bytea; escape-encoded so binary framing is skippable text and an embedded ASCII sentinel is found verbatim.
    private static async Task<string> DumpDurableStateAsync(IAlbaHost host)
    {
        var connectionString = host.Services.GetRequiredService<IConfiguration>().GetConnectionString("marten")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var dump = new StringBuilder();
        await AppendRowsAsync(connection, "select data::text from crawldad_leak.mt_doc_runexecutorsaga", dump);
        await AppendRowsAsync(connection, "select encode(body, 'escape') from crawldad_leak.wolverine_incoming_envelopes", dump);
        await AppendRowsAsync(connection, "select encode(body, 'escape') from crawldad_leak.wolverine_outgoing_envelopes", dump);
        await AppendRowsAsync(connection, "select encode(body, 'escape') from crawldad_leak.wolverine_dead_letters", dump);
        return dump.ToString();
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "All SQL strings are compile-time constants scanning fixed Marten/Wolverine tables in the leak-test schema; no user input reaches the command.")]
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

    /// <summary>A by-reference-<b>only</b> token sentinel: it is the <em>resolved</em> credential for the durable-at-rest
    /// sweep and is NEVER echoed into an input, so a hit in the saga/envelope tables is a real by-reference-model breach
    /// (distinct from <see cref="TokenSentinel"/>, which the echo tests deliberately place into a raw input value).</summary>
    internal const string DurableSecretSentinel = "brwsrless_DURABLEREST_secret_0123456789abcdefABCDEF";

    internal const string BrowserlessRef = "browserless-cred";
    internal const string BrowserbaseRef = "browserbase-cred";
    internal const string ConnectUrlRef = "browserbase-connecturl-cred";
    internal const string DurableRef = "durable-byref-cred";

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
            [DurableRef] = DurableSecretSentinel,
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
