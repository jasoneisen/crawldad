using Crawldad.Contracts.Fixtures;
using Marten;

namespace Crawldad.Web.Features.Fixtures;

/// <summary>The tenant-scoped fixture-set store behind the fixtures API and the <c>fixture</c> replay backend. Every
/// method takes the tenant explicitly (the authenticated principal's, never payload data) and opens its own tenant-scoped
/// Marten session, so a tenant only ever records, lists, reads, or deletes its own sets — a cross-tenant name is simply
/// absent in this tenant's partition, no existence oracle.</summary>
public interface IFixtureStore
{
    /// <summary>Records (or replaces) <paramref name="fixture"/> for <paramref name="tenant"/>, returning its summary.</summary>
    Task<FixtureSummary> SaveAsync(string tenant, FixtureSet fixture, CancellationToken ct);

    /// <summary>Lists the tenant's recorded fixture sets (page HTML omitted), ordered by name for a deterministic response.</summary>
    Task<IReadOnlyList<FixtureSummary>> ListAsync(string tenant, CancellationToken ct);

    /// <summary>Loads the tenant's full <paramref name="name"/> set (manifest + pages) for replay, or null when absent.</summary>
    Task<FixtureSet?> LoadAsync(string tenant, string name, CancellationToken ct);

    /// <summary>Deletes the tenant's <paramref name="name"/> set; <see langword="false"/> when it did not exist.</summary>
    Task<bool> DeleteAsync(string tenant, string name, CancellationToken ct);
}

/// <summary>The Marten-backed <see cref="IFixtureStore"/>. Opens a tenant-scoped session per call via the shared
/// <see cref="IDocumentStore"/> (the replay backend has no ambient request session), mirroring the browser-credential
/// store. Tenant isolation is Marten's conjoined tenancy — no query here filters by tenant explicitly.</summary>
internal sealed class MartenFixtureStore(IDocumentStore store) : IFixtureStore
{
    public async Task<FixtureSummary> SaveAsync(string tenant, FixtureSet fixture, CancellationToken ct)
    {
        await using var session = store.LightweightSession(tenant);
        session.Store(fixture);
        await session.SaveChangesAsync(ct);
        return Summary(fixture);
    }

    public async Task<IReadOnlyList<FixtureSummary>> ListAsync(string tenant, CancellationToken ct)
    {
        await using var session = store.QuerySession(tenant);
        var sets = await session.Query<FixtureSet>().OrderBy(static f => f.Id).ToListAsync(ct);
        return [.. sets.Select(Summary)];
    }

    public async Task<FixtureSet?> LoadAsync(string tenant, string name, CancellationToken ct)
    {
        await using var session = store.QuerySession(tenant);
        return await session.LoadAsync<FixtureSet>(name, ct);
    }

    public async Task<bool> DeleteAsync(string tenant, string name, CancellationToken ct)
    {
        await using var session = store.LightweightSession(tenant);
        if (await session.LoadAsync<FixtureSet>(name, ct) is null)
        {
            return false;
        }

        session.Delete<FixtureSet>(name);
        await session.SaveChangesAsync(ct);
        return true;
    }

    private static FixtureSummary Summary(FixtureSet set) =>
        new(set.Id, set.PageCount, set.TransitionCount, set.TotalBytes, set.RunId, set.CreatedAt);
}
