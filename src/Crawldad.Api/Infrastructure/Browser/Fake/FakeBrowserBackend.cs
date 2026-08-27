namespace Crawldad.Api.Infrastructure.Browser.Fake;

/// <summary>The record/replay browser backend: driven entirely by a fixture directory's <c>manifest.json</c> — no
/// Chromium, no network — so the whole interpreter runs deterministically in CI. Registered under the adapter id
/// <c>"fake"</c>; <see cref="BackendBinding.Options"/><c>["fixture"]</c> names the fixture directory.</summary>
internal sealed class FakeBrowserBackend(string fixturesRoot) : IBrowserBackend
{
    /// <summary>The most recently connected session — a white-box hook for tests to inspect the final DOM.</summary>
    internal FakeBrowserSession? LastSession { get; private set; }

    public Task<IBrowserSession> ConnectAsync(BackendBinding binding, SessionPolicy policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding);

        // The fake ignores the policy: no launching, no context options, no request interception (there is no
        // network). The interpreter itself reads policy.DefaultTimeoutMs for the fake path, and the fake models no
        // per-page timeouts, so there is nothing here to honour.
        var fixtureName = binding.Options?.GetValueOrDefault("fixture") as string;
        if (fixtureName is null)
        {
            throw new FakeBackendException("the 'fake' backend requires Options[\"fixture\"] naming a fixture directory");
        }

        var manifest = FakeManifest.Load(Path.Combine(fixturesRoot, fixtureName));
        var session = new FakeBrowserSession(manifest);
        LastSession = session;
        return Task.FromResult<IBrowserSession>(session);
    }
}

/// <summary>One connected fake session. Disposal is a no-op (nothing external is owned), but exists so the
/// interpreter's teardown path runs identically to a real backend. Scripted-fault attempt counters are keyed on the
/// session, not the page, so they persist across a <see cref="NewPageAsync"/> reopen and reset only on a new session.</summary>
internal sealed class FakeBrowserSession(FakeManifest manifest) : IBrowserSession
{
    private readonly Dictionary<FakeTransition, int> _injectAttempts = new();
    private readonly List<FakePageHandle> _pages = [];

    /// <summary>The fake's region tag — a constant, since the fake serves fixtures with no real backend region.</summary>
    public string Region => "fake";

    /// <summary>Always 0 — the fake does no request interception, so there are no route-cache hits to report.</summary>
    public int CacheHits => 0;

    /// <summary>The manifest driving this session's pages.</summary>
    internal FakeManifest Manifest => manifest;

    /// <summary>Every page opened on this session, in order — a white-box hook to assert the crashed page was closed.</summary>
    internal IReadOnlyList<FakePageHandle> Pages => _pages;

    /// <summary>The most recently opened page — a white-box hook for tests to read the (mutated) final DOM.</summary>
    internal FakePageHandle? LastPage { get; private set; }

    /// <summary>Whether this session was torn down (<see cref="DisposeAsync"/>). A real adapter's dispose closes the remote
    /// backend session, so the cancellation gate asserts this to prove a cancelled run left <b>no orphaned session</b>.</summary>
    internal bool Disposed { get; private set; }

    public Task<IPageHandle> NewPageAsync(CancellationToken ct)
    {
        var page = new FakePageHandle(this);
        _pages.Add(page);
        LastPage = page;
        return Task.FromResult<IPageHandle>(page);
    }

    /// <summary>Records one trigger of <paramref name="transition"/> and returns its 1-based attempt number this session.</summary>
    /// <param name="transition">The transition whose scripted fault is being evaluated.</param>
    internal int NextInjectAttempt(FakeTransition transition)
    {
        var next = _injectAttempts.GetValueOrDefault(transition) + 1;
        _injectAttempts[transition] = next;
        return next;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
