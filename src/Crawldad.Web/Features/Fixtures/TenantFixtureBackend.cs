using Crawldad.Web.Infrastructure.Browser;
using Crawldad.Web.Infrastructure.Browser.Fake;

namespace Crawldad.Web.Features.Fixtures;

/// <summary>The tenant fixture <b>replay</b> backend, registered under the adapter id <c>"fixture"</c>. A run whose
/// <c>config.backend</c> resolves to <c>{ adapter: "fixture", options: { fixtureSet: "&lt;name&gt;" } }</c> replays the
/// named tenant set deterministically with zero live traffic. Resolution is strictly tenant-scoped — the set is loaded
/// from the run's own tenant partition, so a tenant can never name another tenant's set nor an internal shipped fixture
/// (those live only under the separate <c>fake</c> adapter, unreachable here). Replay reuses the fake replay engine in
/// strict mode, so a divergence from recorded coverage fails classified rather than mis-replaying.</summary>
internal sealed class TenantFixtureBackend(IFixtureStore store) : IBrowserBackend
{
    /// <summary>The option key naming which of the tenant's recorded sets to replay.</summary>
    public const string FixtureSetOption = "fixtureSet";

    public async Task<IBrowserSession> ConnectAsync(BackendBinding binding, SessionPolicy policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding);

        // Like the fake, the replay backend applies no session policy — there is no launch/context/route and no network.
        if (binding.Options?.GetValueOrDefault(FixtureSetOption) is not string name)
        {
            throw new FakeBackendException($"the 'fixture' backend requires Options[\"{FixtureSetOption}\"] naming a tenant fixture set");
        }

        // binding.Tenant is the run's tenant, always set on a real run (the interpreter requires a non-null tenant); the
        // set is loaded from that partition alone, so replay is tenant-isolated by construction.
        var set = await store.LoadAsync(binding.Tenant!, name, ct)
            ?? throw new FakeBackendException($"no fixture set '{name}' exists for this tenant — record it first via POST /fixtures/{name}/record");

        var manifest = FakeManifest.Parse(set.ManifestJson, new InMemoryFixtureContent(set.Pages), strict: true);
        return new FakeBrowserSession(manifest);
    }
}
