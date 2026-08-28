using System.Text.Json;
using Crawldad.Client;
using Crawldad.Contracts;

namespace Crawldad.Portal.Tenancy;

/// <summary>What a portal-side free-workspace provision produced (issue #119 PR7).</summary>
public enum PortalProvisionOutcome
{
    /// <summary>A new free workspace was created and made the account's active, linked workspace.</summary>
    Provisioned,

    /// <summary>The account already had a free workspace (one per email, ever); the portal re-established the link to it and
    /// selected it, so the account still lands on its workspace.</summary>
    AlreadyProvisioned,

    /// <summary>Self-serve provisioning is not available on this deployment (stored-key mode — no console identity to create
    /// a workspace with). Nothing was created.</summary>
    Unavailable,

    /// <summary>The provision could not be completed (the API was unreachable, rate-limited, or rejected the request).
    /// Nothing was linked.</summary>
    Failed,
}

/// <summary>The result of a portal provision: the <see cref="Outcome"/>, the workspace id when one is now the account's
/// (created or recovered), and a safe-to-render <see cref="Message"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="TenantId">The account's free workspace id when it now has one (Provisioned/AlreadyProvisioned), else null.</param>
/// <param name="Message">A human-readable, non-sensitive description to show the account holder.</param>
public sealed record PortalProvisionResult(PortalProvisionOutcome Outcome, string? TenantId, string Message);

/// <summary>Creates a portal account's <b>free-tier</b> workspace end-to-end (issue #119 PR7): it calls the API's
/// console-only provisioning endpoint as the portal's first-party identity acting for the signed-in user, then — on success —
/// records the account's keyless <see cref="PortalTenantLink"/> to the new workspace and makes it the active selection, so the
/// account resolves to it on the next request exactly like a console-mode attach. Console-mode only: in stored-key mode (no
/// <see cref="ConsoleClientFactory"/>) there is no first-party identity to create a workspace with, so it reports
/// <see cref="PortalProvisionOutcome.Unavailable"/> and the affordance is never shown. This is the plumbing PR8's public
/// signup calls; today it also backs the Account page's transition affordance for a zero-workspace console user.</summary>
public interface IPortalProvisioningService
{
    /// <summary>Provisions the signed-in account's one free workspace and, on success, links + selects it. A repeat is a clean
    /// <see cref="PortalProvisionOutcome.AlreadyProvisioned"/> that still links + selects the pre-existing workspace.</summary>
    /// <param name="email">The signed-in account's email (the console identity + link identity).</param>
    /// <param name="displayName">An optional display name for the new workspace; blank/absent → a server default.</param>
    /// <param name="cancellationToken">Cancels the round-trip.</param>
    Task<PortalProvisionResult> ProvisionAsync(string email, string? displayName, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IPortalProvisioningService"/>
internal sealed class PortalProvisioningService : IPortalProvisioningService
{
    private readonly ConsoleClientFactory? _consoleClients;
    private readonly IPortalTenantLinkStore _links;
    private readonly IPortalWorkspaceSelectionStore _selections;

    public PortalProvisioningService(
        IPortalTenantLinkStore links,
        IPortalWorkspaceSelectionStore selections,
        ConsoleClientFactory? consoleClients = null)
    {
        _links = links;
        _selections = selections;
        _consoleClients = consoleClients; // present only in console-mode (Crawldad:ConsoleAuth configured)
    }

    public async Task<PortalProvisionResult> ProvisionAsync(string email, string? displayName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        if (_consoleClients is null)
        {
            // Stored-key mode: no first-party console identity, so there is nothing to create a workspace as. The Account
            // affordance is hidden in this mode, so this is only reachable by a crafted POST — reported honestly, never a 500.
            return new PortalProvisionResult(
                PortalProvisionOutcome.Unavailable,
                null,
                "Self-serve workspace creation isn't available on this deployment.");
        }

        var normalized = EmailAddress.Normalize(email);
        var client = _consoleClients.BuildForProvisioning(normalized);

        try
        {
            var workspace = await client.ProvisionTenantAsync(displayName, cancellationToken);
            await LinkAndSelectAsync(normalized, workspace.TenantId, cancellationToken);
            return new PortalProvisionResult(
                PortalProvisionOutcome.Provisioned,
                workspace.TenantId,
                $"Your free workspace ‘{workspace.DisplayName}’ is ready.");
        }
        catch (CrawldadApiException ex) when (ex.StatusCode == _conflictStatusCode && ExtractTenantId(ex.ResponseBody) is { } existing)
        {
            // One free workspace per email, ever: the account already has one but the portal lost its link (a rare recovery
            // case — the target population is brand-new users). Re-establish the link to the existing workspace and select it,
            // so the account still lands on its workspace rather than being stranded.
            await LinkAndSelectAsync(normalized, existing, cancellationToken);
            return new PortalProvisionResult(
                PortalProvisionOutcome.AlreadyProvisioned,
                existing,
                "You already have a free workspace — taking you to it.");
        }
        catch (Exception ex) when (ex is CrawldadException or HttpRequestException)
        {
            return new PortalProvisionResult(
                PortalProvisionOutcome.Failed,
                null,
                "We couldn't create your workspace just now. Please try again in a moment.");
        }
    }

    // Records the account's KEYLESS link to the workspace (console-mode: the console credential is the authenticator, no key
    // is stored) and makes it the active selection — so the next request resolves to it exactly like a console-mode attach.
    private async Task LinkAndSelectAsync(string email, string tenantId, CancellationToken cancellationToken)
    {
        await _links.UpsertKeylessAsync(email, tenantId, cancellationToken);
        await _selections.SetAsync(email, tenantId, cancellationToken);
    }

    // The portal doesn't reference ASP.NET's StatusCodes here, so name the one status it branches on once.
    private const int _conflictStatusCode = 409;

    // Pulls the existing workspace id from the API's 409 problem-details `tenantId` extension. Returns null for a missing /
    // malformed / non-JSON body — the caller then degrades to a plain Failed rather than a bad link.
    private static string? ExtractTenantId(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("tenantId", out var tenantId)
                && tenantId.ValueKind == JsonValueKind.String)
            {
                var value = tenantId.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch (JsonException)
        {
            // not JSON / unexpected shape — no id to recover
        }

        return null;
    }
}
