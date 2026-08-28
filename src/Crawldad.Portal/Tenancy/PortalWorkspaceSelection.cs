using Crawldad.Portal.Auth;
using Marten;

namespace Crawldad.Portal.Tenancy;

/// <summary>Which workspace (tenant) a portal account has <b>active</b> right now (issue #119 PR6), so a multi-workspace user
/// stays on the workspace they picked across requests. Stored in the "portal" schema, keyed by the account's normalized email
/// (the same identity as <see cref="PortalUser"/> and <see cref="PortalTenantLink"/>), so it lines up 1:1 with the account.
/// It is a pure <b>preference</b> — never authority: the API's membership store still decides whether the account may act as
/// the selected tenant, so a stale selection (a workspace the user has left) simply fails the console gate rather than leaking
/// anything. Absent, resolution falls back to the account's <see cref="PortalTenantLink"/> tenant.</summary>
public sealed class PortalWorkspaceSelection
{
    /// <summary>The account's email — the document id, always normalized (matching <see cref="PortalUser"/>). Unique.</summary>
    public string Email { get; set; } = "";

    /// <summary>The tenant id of the active workspace — the value the <c>X-Crawldad-Workspace</c> selector carries.</summary>
    public string TenantId { get; set; } = "";

    /// <summary>When the selection was last written (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Persistence for <see cref="PortalWorkspaceSelection"/> — the active-workspace preference, read on every
/// dashboard request and written by the workspace switcher. Opens its own session per call over the shared
/// <see cref="IDocumentStore"/> (so it serves request-scoped callers alike). No secrets: it holds only an email→tenant id.</summary>
public interface IPortalWorkspaceSelectionStore
{
    /// <summary>Loads the account's active-workspace selection, or null when it has never chosen one (resolution then falls
    /// back to the account's link tenant).</summary>
    Task<PortalWorkspaceSelection?> GetAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>Sets the account's active workspace to <paramref name="tenantId"/> (create-or-replace, by normalized email),
    /// advancing the timestamp.</summary>
    Task SetAsync(string email, string tenantId, CancellationToken cancellationToken = default);
}

/// <summary>The Marten-backed <see cref="IPortalWorkspaceSelectionStore"/>. Mirrors <see cref="MartenPortalTenantLinkStore"/>:
/// its own session per call over the shared store, normalized-email identity.</summary>
internal sealed class MartenPortalWorkspaceSelectionStore(IDocumentStore store, TimeProvider clock) : IPortalWorkspaceSelectionStore
{
    public async Task<PortalWorkspaceSelection?> GetAsync(string email, CancellationToken cancellationToken = default)
    {
        var id = PortalAuthService.NormalizeEmail(email);
        await using var session = store.QuerySession();
        return await session.LoadAsync<PortalWorkspaceSelection>(id, cancellationToken);
    }

    public async Task SetAsync(string email, string tenantId, CancellationToken cancellationToken = default)
    {
        var id = PortalAuthService.NormalizeEmail(email);
        await using var session = store.LightweightSession();
        session.Store(new PortalWorkspaceSelection { Email = id, TenantId = tenantId, UpdatedAt = clock.GetUtcNow() });
        await session.SaveChangesAsync(cancellationToken);
    }
}
