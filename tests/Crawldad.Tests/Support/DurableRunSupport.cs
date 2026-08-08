using System.Collections.Concurrent;
using System.Text.Json;
using Alba;
using Crawldad.Web.Features.Runs;
using Crawldad.Web.Infrastructure.Browser;
using Crawldad.Web.Infrastructure.Browser.Fake;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Support;

/// <summary>Records, in order, the URL of every page the run navigates to (a fresh <c>goto</c> or a pagination postback) —
/// the fetch-count proof for the kill-and-restart gate (§11): a resumed host must NOT re-enter earlier pages.</summary>
internal sealed class PageFetchRecorder
{
    private readonly ConcurrentQueue<string> _urls = new();

    /// <summary>The URLs entered, in order.</summary>
    public IReadOnlyList<string> Urls => [.. _urls];

    /// <summary>Records that the page landed on <paramref name="url"/>.</summary>
    /// <param name="url">The URL the page now reports.</param>
    public void Record(string url) => _urls.Enqueue(url);
}

/// <summary>
/// A one-shot gate that pauses a run at a chosen point so a test can act while it is provably mid-execution (§11): it
/// blocks the first pagination whose current page URL contains a marker, signals <see cref="Reached"/>, then waits until
/// <see cref="Release"/> (a cooperative-cancel test) or the run's cancellation token fires (a kill / deadline test, which
/// throws <see cref="OperationCanceledException"/> out of the blocked call).
/// </summary>
/// <param name="blockWhenUrlContains">Block the pagination that departs a page whose URL contains this marker.</param>
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
/// <param name="fixturesRoot">The fixtures root the inner fake reads.</param>
/// <param name="holder">The gate + recorder for the current run.</param>
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
/// <param name="inner">The inner fake session.</param>
/// <param name="recorder">The fetch recorder.</param>
/// <param name="gate">The pagination gate, or null.</param>
internal sealed class GatedSession(IBrowserSession inner, PageFetchRecorder recorder, RunGate? gate) : IBrowserSession
{
    /// <summary>Whether the session was torn down — asserts a cancelled run left no orphaned session (§11).</summary>
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
/// <param name="inner">The inner fake page.</param>
/// <param name="recorder">The fetch recorder.</param>
/// <param name="gate">The pagination gate, or null.</param>
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
}

/// <summary>Builds and polls durable-run hosts for the §11 async/cancel/kill/deadline gates.</summary>
public static class DurableHost
{
    /// <summary>Builds an Alba host on <paramref name="schema"/> with a frozen clock and the given <c>fake</c> backend
    /// override. Set <paramref name="resetData"/> false for the SECOND host of a kill-and-restart, which must inherit the
    /// first host's persisted checkpoint on the same schema.</summary>
    /// <param name="schema">The Marten/durable schema to run on.</param>
    /// <param name="fakeBackend">The backend registered under the <c>fake</c> adapter id.</param>
    /// <param name="resetData">Whether to reset Marten data after boot (false to inherit a prior host's data).</param>
    /// <param name="settings">Extra host settings to layer on (e.g. a low <c>Crawldad:Limits</c> cap), or null for none.</param>
    public static Task<IAlbaHost> BuildAsync(
        string schema, IBrowserBackend fakeBackend, bool resetData = true, IEnumerable<KeyValuePair<string, string?>>? settings = null) =>
        BuildAsync(schema, (_, _) => fakeBackend, resetData, settings);

    /// <summary>As <see cref="BuildAsync(string, IBrowserBackend, bool, IEnumerable{KeyValuePair{string, string?}})"/>, but the
    /// <c>fake</c> backend is built by a DI factory — so a backend can resolve a host service (e.g. the <c>IRunSecretScope</c> a
    /// CD-15 credential test's backend registers a secret into).</summary>
    /// <param name="schema">The Marten/durable schema to run on.</param>
    /// <param name="fakeBackendFactory">The keyed factory that builds the backend registered under the <c>fake</c> adapter id.</param>
    /// <param name="resetData">Whether to reset Marten data after boot (false to inherit a prior host's data).</param>
    /// <param name="settings">Extra host settings to layer on (e.g. a low <c>Crawldad:Limits</c> cap), or null for none.</param>
    public static async Task<IAlbaHost> BuildAsync(
        string schema, Func<IServiceProvider, object?, IBrowserBackend> fakeBackendFactory, bool resetData = true, IEnumerable<KeyValuePair<string, string?>>? settings = null)
    {
        var host = (await AlbaHost.For<Program>(builder =>
        {
            builder.UseCrawldadTestDefaults(schema);
            foreach (var (key, value) in settings ?? [])
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(new FakeClock());
                services.AddKeyedSingleton<IBrowserBackend>("fake", fakeBackendFactory);
            });
        })).AuthenticatedAsPrimaryTenant();

        if (resetData)
        {
            await host.ResetAllMartenDataAsync();
        }

        return host;
    }

    /// <summary>Polls until the run's <see cref="RunExecutorSaga"/> document is gone (CD-5): the shared finaliser deletes it in
    /// the same transaction as the run's terminal disposition, so its <c>script</c>+<c>inputs</c> stop lingering at rest
    /// (SECURITY.md "Durable state at rest"). Returns as soon as the run reaches terminal — there is no separate cleanup step to
    /// wait on.</summary>
    /// <param name="host">The host to poll.</param>
    /// <param name="runId">The run whose saga should be reclaimed.</param>
    /// <param name="timeout">How long to wait before giving up.</param>
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

    /// <summary>Polls <c>GET /runs/{id}</c> until the run reaches a terminal state (past <c>queued</c> and <c>running</c>),
    /// returning its terminal state body.</summary>
    /// <param name="host">The host to poll.</param>
    /// <param name="runId">The run to poll.</param>
    /// <param name="timeout">How long to wait before giving up.</param>
    public static async Task<JsonElement> PollUntilTerminalAsync(IAlbaHost host, Guid runId, TimeSpan timeout) =>
        await PollUntilAsync(host, runId, timeout, status => status is "succeeded" or "failed" or "cancelled");

    /// <summary>Polls <c>GET /runs/{id}</c> until its status satisfies <paramref name="isDone"/> (CD-16: e.g. "left the queue"
    /// — no longer <c>queued</c>), returning the matching state body.</summary>
    /// <param name="host">The host to poll.</param>
    /// <param name="runId">The run to poll.</param>
    /// <param name="timeout">How long to wait before giving up.</param>
    /// <param name="isDone">The predicate over the wire status that ends the poll.</param>
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
