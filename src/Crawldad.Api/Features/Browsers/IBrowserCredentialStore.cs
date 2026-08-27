using Crawldad.Contracts.Browsers;
using Marten;
using Microsoft.AspNetCore.DataProtection;

namespace Crawldad.Api.Features.Browsers;

/// <summary>The tenant-scoped credential store behind the browsers API and connect resolution. Every method takes the
/// tenant explicitly (the authenticated principal's, never payload data) and opens its own tenant-scoped Marten session,
/// so a tenant can only ever register, list, delete, or resolve its own browsers. Secrets are encrypted at rest.</summary>
public interface IBrowserCredentialStore
{
    /// <summary>Registers (or replaces) <paramref name="name"/> for <paramref name="tenant"/>, encrypting the secret at
    /// rest; a replace keeps the original <c>createdAt</c>. Returns the stored metadata — never the secret.</summary>
    Task<BrowserSummary> RegisterAsync(string tenant, string name, string adapter, string mode, string secret,
        IReadOnlyDictionary<string, string>? options, CancellationToken ct);

    /// <summary>Lists the tenant's registered browsers (secrets omitted), ordered by name for a deterministic response.</summary>
    Task<IReadOnlyList<BrowserSummary>> ListAsync(string tenant, CancellationToken ct);

    /// <summary>Deletes the tenant's <paramref name="name"/> registration; <see langword="false"/> when it did not exist.</summary>
    Task<bool> DeleteAsync(string tenant, string name, CancellationToken ct);

    /// <summary>Decrypts the tenant's <paramref name="name"/> secret for a connect, or <see langword="null"/> when the
    /// tenant has no such registration (a cross-tenant name is simply absent in this tenant — no existence oracle).</summary>
    Task<string?> TryResolveSecretAsync(string tenant, string name, CancellationToken ct);
}

/// <summary>The Marten-backed <see cref="IBrowserCredentialStore"/>. Opens a tenant-scoped session per call via the
/// shared <see cref="IDocumentStore"/> (the connect path has no ambient request session), and protects the secret with a
/// purpose-bound <see cref="IDataProtector"/> so the stored document never holds plaintext.</summary>
internal sealed class MartenBrowserCredentialStore : IBrowserCredentialStore
{
    /// <summary>The Data-Protection purpose the credential protector is bound to. Purpose isolation means this key ring
    /// cannot decrypt (or be decrypted by) any other protector in the app.</summary>
    internal const string ProtectorPurpose = "Crawldad.Web.Features.Browsers.BrowserCredentialStore.v1";

    private readonly IDocumentStore _store;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _clock;

    public MartenBrowserCredentialStore(IDocumentStore store, IDataProtectionProvider protection, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(protection);
        _store = store;
        _protector = protection.CreateProtector(ProtectorPurpose);
        _clock = clock;
    }

    public async Task<BrowserSummary> RegisterAsync(string tenant, string name, string adapter, string mode,
        string secret, IReadOnlyDictionary<string, string>? options, CancellationToken ct)
    {
        await using var session = _store.LightweightSession(tenant);
        var existing = await session.LoadAsync<BrowserRegistration>(name, ct);
        var now = _clock.GetUtcNow();
        var doc = new BrowserRegistration
        {
            Id = name,
            Adapter = adapter,
            Mode = mode,
            ProtectedSecret = _protector.Protect(secret),
            Options = options,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
        };
        session.Store(doc);
        await session.SaveChangesAsync(ct);
        return Summary(doc);
    }

    public async Task<IReadOnlyList<BrowserSummary>> ListAsync(string tenant, CancellationToken ct)
    {
        await using var session = _store.QuerySession(tenant);
        var docs = await session.Query<BrowserRegistration>().OrderBy(static b => b.Id).ToListAsync(ct);
        return [.. docs.Select(Summary)];
    }

    public async Task<bool> DeleteAsync(string tenant, string name, CancellationToken ct)
    {
        await using var session = _store.LightweightSession(tenant);
        if (await session.LoadAsync<BrowserRegistration>(name, ct) is null)
        {
            return false;
        }

        session.Delete<BrowserRegistration>(name);
        await session.SaveChangesAsync(ct);
        return true;
    }

    public async Task<string?> TryResolveSecretAsync(string tenant, string name, CancellationToken ct)
    {
        await using var session = _store.QuerySession(tenant);
        var doc = await session.LoadAsync<BrowserRegistration>(name, ct);
        return doc is null ? null : _protector.Unprotect(doc.ProtectedSecret);
    }

    private static BrowserSummary Summary(BrowserRegistration doc) =>
        new(doc.Id, doc.Adapter, doc.Mode, doc.Options, doc.CreatedAt, doc.UpdatedAt);
}
