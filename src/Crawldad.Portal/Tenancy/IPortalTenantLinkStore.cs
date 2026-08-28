using Crawldad.Portal.Auth;
using Marten;
using Microsoft.AspNetCore.DataProtection;

namespace Crawldad.Portal.Tenancy;

/// <summary>Persistence for <see cref="PortalTenantLink"/> documents. The write path protects the tenant API key at
/// rest, so callers pass the raw key and never touch Data Protection themselves; the read path returns the stored
/// document (ciphertext), which the account area uses for metadata (tenant id, timestamps) and the tenant context
/// decrypts per request. Both dev seeding and the future account UI write through <see cref="UpsertAsync"/>.</summary>
public interface IPortalTenantLinkStore
{
    /// <summary>Loads the link for <paramref name="email"/> (normalized), or <see langword="null"/> when the account
    /// has none. The returned document holds the API key as ciphertext only — never the raw key.</summary>
    Task<PortalTenantLink?> GetAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces the link for <paramref name="email"/> (normalized), encrypting
    /// <paramref name="apiKey"/> at rest. A replace preserves the original <see cref="PortalTenantLink.CreatedAt"/>
    /// and advances <see cref="PortalTenantLink.UpdatedAt"/>. Returns the stored document (ciphertext).</summary>
    Task<PortalTenantLink> UpsertAsync(string email, string tenantId, string apiKey, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces the link for <paramref name="email"/> (normalized) as a <b>keyless</b> console-mode link
    /// (issue #119 PR5): the email→tenant mapping with <see cref="PortalTenantLink.ProtectedApiKey"/> = <see langword="null"/>,
    /// so no key is stored. Written by the attach flow when console-mode is configured, after it has verified the key and
    /// recorded the membership — the console credential authenticates from here on. A replace preserves the original
    /// <see cref="PortalTenantLink.CreatedAt"/> and advances <see cref="PortalTenantLink.UpdatedAt"/>; a replace of a
    /// previously stored-key link <b>clears</b> the ciphertext. Returns the stored document.</summary>
    Task<PortalTenantLink> UpsertKeylessAsync(string email, string tenantId, CancellationToken cancellationToken = default);
}

/// <summary>The Marten-backed <see cref="IPortalTenantLinkStore"/>. Opens its own session per call over the shared
/// <see cref="IDocumentStore"/> (so it can serve request-scoped callers and the startup dev seeder alike) and
/// protects the API key with a purpose-bound <see cref="IDataProtector"/> so the stored document never holds
/// plaintext. Mirrors the API's <c>MartenBrowserCredentialStore</c>.</summary>
internal sealed class MartenPortalTenantLinkStore : IPortalTenantLinkStore
{
    private readonly IDocumentStore _store;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _clock;

    public MartenPortalTenantLinkStore(IDocumentStore store, IDataProtectionProvider protection, TimeProvider clock)
    {
        _store = store;
        _protector = PortalTenancy.ApiKeyProtector(protection);
        _clock = clock;
    }

    public async Task<PortalTenantLink?> GetAsync(string email, CancellationToken cancellationToken = default)
    {
        var id = PortalAuthService.NormalizeEmail(email);
        await using var session = _store.QuerySession();
        return await session.LoadAsync<PortalTenantLink>(id, cancellationToken);
    }

    public Task<PortalTenantLink> UpsertAsync(string email, string tenantId, string apiKey, CancellationToken cancellationToken = default) =>
        UpsertCoreAsync(email, tenantId, _protector.Protect(apiKey), cancellationToken);

    public Task<PortalTenantLink> UpsertKeylessAsync(string email, string tenantId, CancellationToken cancellationToken = default) =>
        UpsertCoreAsync(email, tenantId, protectedApiKey: null, cancellationToken);

    // The shared upsert: create-or-replace by normalized email, preserving CreatedAt and advancing UpdatedAt. A null
    // protectedApiKey stores a keyless (console-mode) link and clears any previously stored ciphertext.
    private async Task<PortalTenantLink> UpsertCoreAsync(string email, string tenantId, string? protectedApiKey, CancellationToken cancellationToken)
    {
        var id = PortalAuthService.NormalizeEmail(email);
        await using var session = _store.LightweightSession();
        var existing = await session.LoadAsync<PortalTenantLink>(id, cancellationToken);
        var now = _clock.GetUtcNow();
        var doc = new PortalTenantLink
        {
            Email = id,
            TenantId = tenantId,
            ProtectedApiKey = protectedApiKey,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
        };
        session.Store(doc);
        await session.SaveChangesAsync(cancellationToken);
        return doc;
    }
}
