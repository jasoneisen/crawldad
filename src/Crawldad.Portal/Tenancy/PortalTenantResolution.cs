namespace Crawldad.Portal.Tenancy;

/// <summary>The one place the console workspace-resolution decision lives (issue #119 simplification), shared by the
/// static-SSR <see cref="PortalTenantContext"/> and the circuit <see cref="CircuitTenantResolver"/> so the two never drift.
/// The portal is console-mode only: there is no stored tenant key and no key-mode fallback. A resolved account acts as its
/// <b>active workspace</b> — the account's <see cref="PortalWorkspaceSelection"/> pointer, written by signup / claim / switch —
/// with the API's membership store as the authority (a stale selection whose membership was revoked simply fails the console
/// gate on the next read, never leaks). Resolution is <see langword="null"/> (a clean not-linked-shaped state) when the request
/// is unauthenticated, console access is unconfigured (no <see cref="ConsoleClientFactory"/>), or the account has no active
/// workspace yet.</summary>
internal static class PortalTenantResolution
{
    /// <summary>Builds the <see cref="PortalTenant"/> for a resolved <paramref name="email"/>: a console client acting for the
    /// account's active workspace selection. Returns null when <paramref name="email"/> is null (unauthenticated),
    /// <paramref name="consoleClients"/> is null (console access unconfigured), or the account has no active-workspace selection.</summary>
    public static async Task<PortalTenant?> ResolveAsync(
        string? email,
        IPortalWorkspaceSelectionStore selections,
        ConsoleClientFactory? consoleClients,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selections);

        if (email is null || consoleClients is null)
        {
            return null; // unauthenticated, or console access not configured on this deployment
        }

        var selection = await selections.GetAsync(email, cancellationToken);
        if (selection is null)
        {
            return null; // authenticated + configured, but the account has no active workspace yet
        }

        var client = consoleClients.Build(email, selection.TenantId);
        return new PortalTenant(selection.TenantId, client);
    }
}
