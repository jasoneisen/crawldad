using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Api.Features.Runs;
using Crawldad.Api.Infrastructure.Browser;
using Crawldad.Api.Infrastructure.Browser.Fake;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Support;

/// <summary>Records, in order, the URL of every page the run navigates to (a fresh <c>goto</c> or a pagination postback) —
/// the fetch-count proof for the kill-and-restart gate: a resumed host must NOT re-enter earlier pages.</summary>
internal sealed class PageFetchRecorder
{
    private readonly ConcurrentQueue<string> _urls = new();

    public IReadOnlyList<string> Urls => [.. _urls];

    public void Record(string url) => _urls.Enqueue(url);
}

/// <summary>A one-shot gate pausing a run at a chosen point for mid-execution test control: blocks the first pagination
/// whose URL contains a marker, signals <see cref="Reached"/>, then waits for <see cref="Release"/> or cancellation.</summary>
/// <param name="blockWhenUrlContains">Matched against the URL of the page being departed, not the destination.</param>
internal sealed class RunGate(string blockWhenUrlContains)
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the run has blocked at the gate.</summary>
    public Task Reached => _reached.Task;

    /// <summary>Releases the gate so the blocked run proceeds (a cooperative-cancel test).</summary>
    public void Release() => _released.TrySetResult();

    /// <summary>Blocks if <paramref name="currentUrl"/> matches, until released or <paramref name="ct"/> fires.</summary>
    /// <param name="currentUrl">The page the run is about to leave.</param>
    /// <param name="ct">The run's cancellation token (host shutdown / deadline).</param>
    public async Task WaitAsync(string currentUrl, CancellationToken ct)
    {
        if (!currentUrl.Contains(blockWhenUrlContains, StringComparison.Ordinal))
        {
            return;
        }

        _reached.TrySetResult();
        await _released.Task.WaitAsync(ct);
    }
}

/// <summary>The per-run gate + fetch recorder a host's <see cref="GatedFakeBackend"/> reads. Mutable so ONE shared host can
/// be re-armed per test (each test <see cref="Arm"/>s a fresh recorder and its chosen gate), avoiding a per-test host build
/// — and the parallel schema-migration contention it causes.</summary>
internal sealed class GateHolder
{
    /// <summary>The current fetch recorder.</summary>
    public PageFetchRecorder Recorder { get; private set; } = new();

    /// <summary>The current pagination gate, or null to run straight through.</summary>
    public RunGate? Gate { get; private set; }

    /// <summary>Re-arms for a test: a fresh recorder and the given gate. Returns the recorder for assertions.</summary>
    /// <param name="gate">The gate for the next run, or null.</param>
    public PageFetchRecorder Arm(RunGate? gate)
    {
        Recorder = new PageFetchRecorder();
        Gate = gate;
        return Recorder;
    }
}

/// <summary>A record/replay backend that decorates <see cref="FakeBrowserBackend"/> to (a) record every page fetch and
/// (b) optionally gate one pagination — the seam the durable-run gates drive. All real behaviour delegates to the fake;
/// the gate + recorder come from a <see cref="GateHolder"/> read at connect time, so one host serves many tests.</summary>
internal sealed class GatedFakeBackend(string fixturesRoot, GateHolder holder) : IBrowserBackend
{
    private readonly FakeBrowserBackend _inner = new(fixturesRoot);

    /// <summary>The most recently connected session — a hook to assert clean teardown (no orphaned session).</summary>
    public GatedSession? LastSession { get; private set; }

    /// <inheritdoc />
    public async Task<IBrowserSession> ConnectAsync(BackendBinding binding, SessionPolicy policy, CancellationToken ct)
    {
        var session = new GatedSession(await _inner.ConnectAsync(binding, policy, ct), holder.Recorder, holder.Gate);
        LastSession = session;
        return session;
    }
}

/// <summary>The gated session: forwards to the inner fake session and tracks its own teardown.</summary>
internal sealed class GatedSession(IBrowserSession inner, PageFetchRecorder recorder, RunGate? gate) : IBrowserSession
{
    /// <summary>Whether the session was torn down — used to assert a cancelled run left no orphaned session.</summary>
    public bool Disposed { get; private set; }

    /// <inheritdoc />
    public string Region => inner.Region;

    /// <inheritdoc />
    public int CacheHits => inner.CacheHits;

    /// <inheritdoc />
    public async Task<IPageHandle> NewPageAsync(CancellationToken ct) => new GatedPage(await inner.NewPageAsync(ct), recorder, gate);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        Disposed = true;
    }
}

/// <summary>The gated page: forwards to the inner fake page, records the landing URL after each navigation, and gates one
/// pagination so the run can be caught mid-execution.</summary>
internal sealed class GatedPage(IPageHandle inner, PageFetchRecorder recorder, RunGate? gate) : IPageHandle
{
    /// <inheritdoc />
    public string Url => inner.Url;

    /// <inheritdoc />
    public async Task GotoAsync(string url, string? waitUntil, int? timeoutMs, CancellationToken ct)
    {
        await inner.GotoAsync(url, waitUntil, timeoutMs, ct);
        recorder.Record(inner.Url);
    }

    /// <inheritdoc />
    public Task WaitForLoadStateAsync(string state, int? timeoutMs, CancellationToken ct) => inner.WaitForLoadStateAsync(state, timeoutMs, ct);

    /// <inheritdoc />
    public ILocatorHandle Locator(string selector) => inner.Locator(selector);

    /// <inheritdoc />
    public ILocatorHandle GetByTitle(string title) => inner.GetByTitle(title);

    public ILocatorHandle GetByRole(string role, string? name) => inner.GetByRole(role, name);

    public ILocatorHandle GetByText(string text) => inner.GetByText(text);

    /// <inheritdoc />
    public IFrameHandle FrameLocator(string selector) => inner.FrameLocator(selector);

    /// <inheritdoc />
    public Task AddStyleTagAsync(string content, CancellationToken ct) => inner.AddStyleTagAsync(content, ct);

    /// <inheritdoc />
    public async Task RunAndWaitForRequestAsync(Func<Task> trigger, string urlPrefix, string? method, int? timeoutMs, CancellationToken ct)
    {
        if (gate is not null)
        {
            await gate.WaitAsync(inner.Url, ct); // block BEFORE the pagination click, so the next page is not fetched
        }

        await inner.RunAndWaitForRequestAsync(trigger, urlPrefix, method, timeoutMs, ct);
        recorder.Record(inner.Url);
    }

    /// <inheritdoc />
    public Task<IDownloadHandle> RunAndWaitForDownloadAsync(Func<Task> trigger, int? timeoutMs, CancellationToken ct) =>
        inner.RunAndWaitForDownloadAsync(trigger, timeoutMs, ct);

    /// <inheritdoc />
    public Task CloseAsync(CancellationToken ct) => inner.CloseAsync(ct);

    /// <inheritdoc />
    public Task<byte[]> ScreenshotAsync(CancellationToken ct) => inner.ScreenshotAsync(ct);

    /// <inheritdoc />
    public Task<string> ContentAsync(CancellationToken ct) => inner.ContentAsync(ct);
}

/// <summary>Builds and polls durable-run hosts for async/cancel/kill/deadline gates.</summary>
public static class DurableHost
{
    /// <summary>The default terminal/queue poll window: a generous contention margin, not a functional expectation — real
    /// promotions finish sub-second. It absorbs infra stalls (connection-pool acquisition, Marten lock waits, GC pauses)
    /// outside the durable layer, large enough to avoid flaky contention failures, small enough to still catch a stuck pipeline.</summary>
    public static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Builds an Alba host on <paramref name="schema"/> with a frozen clock (override via <paramref name="clock"/>)
    /// and the given <c>fake</c> backend override. <paramref name="resetData"/> true (the default) drops the schema before
    /// the host boots — so nothing is left for startup recovery to resume — and resets Marten data after. Set it false for
    /// the SECOND host of a kill-and-restart, which must inherit the first host's persisted checkpoint on the same schema.</summary>
    public static Task<IAlbaHost> BuildAsync(
        string schema, IBrowserBackend fakeBackend, bool resetData = true, IEnumerable<KeyValuePair<string, string?>>? settings = null, TimeProvider? clock = null,
        Action<IServiceCollection>? configureServices = null) =>
        BuildAsync(schema, (_, _) => fakeBackend, resetData, settings, clock, configureServices);

    /// <summary>As <see cref="BuildAsync(string, IBrowserBackend, bool, IEnumerable{KeyValuePair{string, string?}}, TimeProvider, Action{IServiceCollection})"/>, but the
    /// <c>fake</c> backend is built by a DI factory — so a backend can resolve a host service (e.g. the <c>IRunSecretScope</c> a
    /// credential test's backend registers a secret into). Pass <paramref name="clock"/> to drive time (e.g. an
    /// <see cref="AdvanceableClock"/> for the SSE keepalive tail); it defaults to the frozen <see cref="FakeClock"/>.</summary>
    public static async Task<IAlbaHost> BuildAsync(
        string schema, Func<IServiceProvider, object?, IBrowserBackend> fakeBackendFactory, bool resetData = true, IEnumerable<KeyValuePair<string, string?>>? settings = null, TimeProvider? clock = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var host = (await AlbaHost.For<Program>(builder =>
        {
            builder.UseCrawldadTestDefaults(schema, resetData); // resetData false => keep the first host's state (kill-and-restart)
            foreach (var (key, value) in settings ?? [])
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(clock ?? new FakeClock());
                services.AddKeyedSingleton<IBrowserBackend>("fake", fakeBackendFactory);
                configureServices?.Invoke(services); // a test can layer extra wiring on (e.g. a Marten session listener that forces a stream-version race)
            });
        })).AuthenticatedAsPrimaryTenant();

        if (resetData)
        {
            // The pre-boot drop already guarantees an empty schema; this stays because it is also what ensures Marten's
            // storage exists before the first query on a host that skips the Development boot-time schema apply.
            await host.ResetAllMartenDataAsync();
        }

        return host;
    }

    /// <summary>Polls until the run's <see cref="RunExecutorSaga"/> document is gone: the shared finaliser deletes it in the
    /// same transaction as the run's terminal disposition, so its <c>script</c>+<c>inputs</c> stop lingering at rest. Returns
    /// as soon as the run reaches terminal.</summary>
    public static async Task WaitUntilSagaGoneAsync(IAlbaHost host, Guid runId, TimeSpan timeout)
    {
        var store = host.Services.GetRequiredService<IDocumentStore>();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await using (var session = store.LightweightSession(TestTenants.PrimaryId))
            {
                if (await session.LoadAsync<RunExecutorSaga>(runId) is null)
                {
                    return;
                }
            }

            await Task.Delay(40);
        }

        throw new TimeoutException($"the executor saga for run {runId} was not reclaimed within {timeout}");
    }

    /// <summary>The window a real-Chromium parity/canary scrape gets after it auto-upgrades across the 120 s sync cap
    /// before the test gives up: generous enough to absorb a slow hosted runner, bounded so a genuinely stuck run fails
    /// with a diagnosable <see cref="TimeoutException"/> rather than hanging the suite.</summary>
    private static readonly TimeSpan _upgradedRunPollTimeout = TimeSpan.FromSeconds(180);

    /// <summary>Drives the default <b>synchronous</b> <c>POST /runs</c> to a terminal state, tolerating the sync-cap
    /// auto-upgrade (defense-in-depth for the parity/canary suites): a run finishing inside the 120 s window answers
    /// <c>200</c> with the terminal <see cref="Crawldad.Contracts.Runs.RunResponse"/> and is returned verbatim; a run that
    /// crosses the window auto-upgrades to <c>202 { status:"running" }</c> and is polled to its terminal
    /// <see cref="Crawldad.Contracts.Runs.RunStateResponse"/> — whose scrubbed <c>runId</c>/<c>status</c>/<c>result</c>/
    /// <c>failure</c> are identical to the inline body (same terminal disposition and shape, proven by <c>SyncCapTests</c>),
    /// as are the <c>stats</c> counters; only <c>stats.durationMs</c> (wall-time) can differ when the run was upgraded, and
    /// the parity goldens don't assert on it — so a caller's golden/shape/stats assertions hold either way. On a poll
    /// timeout it surfaces the stuck run's timeline in the exception, so a CI hang is diagnosable at a glance rather than
    /// an opaque "did not reach the awaited state".</summary>
    public static async Task<JsonElement> PostRunToTerminalAsync(IAlbaHost host, JsonObject body, TimeSpan? pollTimeout = null)
    {
        var posted = await host.Scenario(x =>
        {
            x.Post.Json(body).ToUrl("/runs");
            x.IgnoreStatusCode(); // 200 (finished within the sync cap) and 202 (auto-upgraded to async) are both valid
        });

        var root = (await posted.ReadAsJsonAsync<JsonElement>()).Clone();
        if (posted.Context.Response.StatusCode == StatusCodes.Status200OK)
        {
            return root; // finished inside the sync window — the inline terminal response, unchanged from today
        }

        posted.Context.Response.StatusCode.ShouldBe(StatusCodes.Status202Accepted, $"unexpected POST /runs status; body: {root}");

        // Auto-upgraded (or queued) onto the async surface: poll to the identical terminal disposition. PollUntilTerminalAsync
        // waits past both `running` and `queued`, so a queued 202 resolves here too.
        var runId = root.GetProperty("runId").GetGuid();
        try
        {
            return await PollUntilTerminalAsync(host, runId, pollTimeout ?? _upgradedRunPollTimeout);
        }
        catch (TimeoutException ex)
        {
            var timeline = await host.Scenario(x =>
            {
                x.Get.Url($"/runs/{runId}/timeline");
                x.IgnoreStatusCode();
            });
            throw new TimeoutException($"{ex.Message}. Stuck-run timeline: {await timeline.ReadAsTextAsync()}", ex);
        }
    }

    /// <summary>Polls <c>GET /runs/{id}</c> until the run reaches a terminal state (past <c>queued</c> and <c>running</c>),
    /// returning its terminal state body.</summary>
    public static async Task<JsonElement> PollUntilTerminalAsync(IAlbaHost host, Guid runId, TimeSpan timeout) =>
        await PollUntilAsync(host, runId, timeout, status => status is "succeeded" or "failed" or "cancelled");

    /// <summary>Polls <c>GET /runs/{id}</c> until its status satisfies <paramref name="isDone"/> (e.g. "left the queue" —
    /// no longer <c>queued</c>), returning the matching state body.</summary>
    public static async Task<JsonElement> PollUntilAsync(IAlbaHost host, Guid runId, TimeSpan timeout, Func<string, bool> isDone)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var result = await host.Scenario(x =>
            {
                x.Get.Url($"/runs/{runId}");
                x.StatusCodeShouldBe(200);
            });
            var root = (await result.ReadAsJsonAsync<JsonElement>()).Clone();
            if (isDone(root.GetProperty("status").GetString()!))
            {
                return root;
            }

            await Task.Delay(40);
        }

        throw new TimeoutException($"run {runId} did not reach the awaited state within {timeout}");
    }
}
