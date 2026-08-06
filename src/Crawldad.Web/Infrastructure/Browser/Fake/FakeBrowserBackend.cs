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

    public Task<IBrowserSession> ConnectAsync(BackendBinding binding, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding);

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

/// <summary>One connected fake session (§9). Owns nothing external, so disposal is a no-op; it exists so the
/// interpreter's teardown path (<c>await using</c>) is exercised identically to a real backend.</summary>
internal sealed class FakeBrowserSession(FakeManifest manifest) : IBrowserSession
{
    /// <summary>The most recently opened page — a white-box hook for tests to read the (mutated) final DOM.</summary>
    internal FakePageHandle? LastPage { get; private set; }

    public Task<IPageHandle> NewPageAsync(CancellationToken ct)
    {
        var page = new FakePageHandle(manifest);
        LastPage = page;
        return Task.FromResult<IPageHandle>(page);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
