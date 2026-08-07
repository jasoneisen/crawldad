namespace Crawldad.Web.Infrastructure.Browser.Fake;

/// <summary>
/// The record/replay browser backend (§ Deliverable 2, §9 testing seam). Driven entirely by a fixture directory's
/// <c>manifest.json</c> — no Chromium, no network — so the whole interpreter runs deterministically in CI. Registered
/// under the adapter id <c>"fake"</c>; <see cref="BackendBinding.Options"/><c>["fixture"]</c> names the fixture
/// directory under the configured fixtures root. Phase 4 adds real adapters beside it behind the same seam.
/// </summary>
internal sealed class FakeBrowserBackend(string fixturesRoot) : IBrowserBackend
{
    /// <summary>The most recently connected session — a white-box hook for tests to inspect the final DOM.</summary>
    internal FakeBrowserSession? LastSession { get; private set; }

    public Task<IBrowserSession> ConnectAsync(BackendBinding binding, SessionPolicy policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding);

        // The record/replay fake ignores the §8.1 policy: it does no launching, no context options, and no request
        // interception (there is no network). The interpreter still reads policy.DefaultTimeoutMs itself for the fake
        // path, and the fake models no per-page timeouts, so there is nothing here to honour.
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

/// <summary>
/// One connected fake session (§9). Owns nothing external, so disposal is a no-op; it exists so the interpreter's
/// teardown path (<c>await using</c>) is exercised identically to a real backend. It also holds the per-transition
/// scripted-fault attempt counters (§ Deliverable 3): keyed on the session (not the page), they persist across a
/// <see cref="NewPageAsync"/> reopen — which is what makes the pageCrashed-then-succeed scenario work — and reset only
/// when a fresh session is connected (a new run).
/// </summary>
internal sealed class FakeBrowserSession(FakeManifest manifest) : IBrowserSession
{
    private readonly Dictionary<FakeTransition, int> _injectAttempts = new();
    private readonly List<FakePageHandle> _pages = [];

    /// <summary>The fake's region tag — a constant, since the fake serves fixtures with no real backend region (§9.1).</summary>
    public string Region => "fake";

    /// <summary>Always 0 — the fake does no request interception, so there are no route-cache hits to report (§10).</summary>
    public int CacheHits => 0;

    /// <summary>The manifest driving this session's pages.</summary>
    internal FakeManifest Manifest => manifest;

    /// <summary>Every page opened on this session, in order — a white-box hook to assert the crashed page was closed.</summary>
    internal IReadOnlyList<FakePageHandle> Pages => _pages;

    /// <summary>The most recently opened page — a white-box hook for tests to read the (mutated) final DOM.</summary>
    internal FakePageHandle? LastPage { get; private set; }

    /// <summary>Whether this session was torn down (<see cref="DisposeAsync"/>). A real adapter's dispose closes the remote
    /// backend session, so the cancellation gate asserts this to prove a cancelled run left <b>no orphaned session</b> (§11).</summary>
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
