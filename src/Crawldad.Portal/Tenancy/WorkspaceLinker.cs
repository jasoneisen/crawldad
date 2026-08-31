using Crawldad.Client;
using Crawldad.Contracts.Tenancy;
using Microsoft.AspNetCore.Http;

namespace Crawldad.Portal.Tenancy;

/// <summary>The outcome of a workspace-claim attempt. Nothing is ever persisted here: a claim proves key possession and
/// records a console membership on the API — the key is <b>always discarded</b>, never stored (issue #119 simplification).
/// Every value other than <see cref="Claimed"/> is a validated-and-rejected attempt.</summary>
public enum WorkspaceLinkOutcome
{
    /// <summary>The key authenticated, its tenant matched, and the account's Owner membership was recorded — the console
    /// credential authenticates the workspace from here on. The key is discarded.</summary>
    Claimed,

    /// <summary>The API rejected the key (<c>401</c>) — nothing recorded.</summary>
    InvalidKey,

    /// <summary>The key is valid but authenticates a different tenant than the one entered — nothing recorded.</summary>
    TenantMismatch,

    /// <summary>The workspace is an operator-configured (env) tenant that has no membership surface — it cannot be claimed as
    /// a self-serve workspace (the API returns <c>400 self_service_unavailable</c> when recording the membership). Nothing
    /// recorded, and — crucially — no key is kept (issue #119: the old stored-key fallback is gone).</summary>
    OperatorManaged,

    /// <summary>The key could not be verified, or the membership could not be recorded (the API was unreachable or errored) —
    /// nothing recorded.</summary>
    Unverifiable,
}

/// <summary>The result of <see cref="IWorkspaceLinker.LinkAsync"/>: the <see cref="Outcome"/> and a user-facing
/// <see cref="Message"/> safe to render (it never contains key material).</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Message">A human-readable, non-sensitive description to show the account holder.</param>
public sealed record WorkspaceLinkResult(WorkspaceLinkOutcome Outcome, string Message);

/// <summary>Claims an existing workspace for a portal account (the account area's understated "Claim an existing workspace"
/// action, issue #119 simplification). It <b>validates the submitted key against the live API before recording anything</b>:
/// it probes <c>GET /tenant</c> with a one-shot client built for that key, confirms the key authenticates the entered
/// workspace, then records the account's Owner <b>membership</b> (the console authorization authority) — and <b>always
/// discards the key</b>. There is no stored key anywhere anymore; the console credential authenticates the workspace from
/// here on. An operator-configured (env) tenant has no membership surface, so it can never be claimed as a workspace (a clear
/// message, no silently-kept key). The submitted key is treated like a password throughout — passed straight to a one-shot
/// client, never logged, echoed, stored, or returned.</summary>
public interface IWorkspaceLinker
{
    /// <summary>Validates <paramref name="apiKey"/> by reading the tenant profile it authenticates, and — only when that
    /// succeeds and the profile's tenant matches <paramref name="tenantId"/> — records the account's Owner membership, then
    /// discards the key. No API round-trip means nothing recorded.</summary>
    /// <param name="email">The signed-in account's email (the membership identity).</param>
    /// <param name="tenantId">The workspace/tenant id the account holder entered (confirmed against the key).</param>
    /// <param name="apiKey">The tenant API key to validate. Never logged, echoed, stored, or returned.</param>
    /// <param name="cancellationToken">Cancels the round-trips.</param>
    /// <returns>The outcome and a safe-to-render message.</returns>
    Task<WorkspaceLinkResult> LinkAsync(string email, string tenantId, string apiKey, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IWorkspaceLinker"/>
internal sealed class WorkspaceLinker : IWorkspaceLinker
{
    private readonly IHttpClientFactory _httpClientFactory;

    public WorkspaceLinker(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<WorkspaceLinkResult> LinkAsync(string email, string tenantId, string apiKey, CancellationToken cancellationToken = default)
    {
        // Validate the key by making the cheapest authenticated read (GET /tenant) through a one-shot client built for
        // THIS key — not the request's console client. The result decides whether we ever record a membership.
        var http = _httpClientFactory.CreateClient(PortalTenancy.ApiHttpClientName); // base address preset at wiring
        var probe = new CrawldadClient(http, new CrawldadClientOptions { ApiKey = apiKey });

        TenantProfileResponse profile;
        try
        {
            profile = await probe.GetTenantAsync(cancellationToken);
        }
        catch (CrawldadUnauthorizedException)
        {
            return new WorkspaceLinkResult(
                WorkspaceLinkOutcome.InvalidKey,
                "That API key was rejected. Check you pasted the full, current key and try again.");
        }
        catch (Exception ex) when (ex is CrawldadException or HttpRequestException)
        {
            return new WorkspaceLinkResult(
                WorkspaceLinkOutcome.Unverifiable,
                "We couldn't reach the Crawldad API to verify that key. Try again in a moment.");
        }

        var entered = tenantId.Trim();
        if (!string.Equals(profile.TenantId, entered, StringComparison.OrdinalIgnoreCase))
        {
            return new WorkspaceLinkResult(
                WorkspaceLinkOutcome.TenantMismatch,
                $"That key is valid, but it authenticates workspace ‘{profile.TenantId}’ — not ‘{entered}’. Check the workspace ID.");
        }

        // Validated: record the account's OWNER membership (the console authorization authority) using the same proven key,
        // so a later console read/write for this email resolves to the workspace. The key is discarded either way — it is
        // NEVER stored (issue #119: the stored-key path is gone). An env-configured tenant has no membership surface (400):
        // it can't be claimed as a workspace, and we keep no key for it.
        try
        {
            await probe.RecordOwnerMembershipAsync(email, cancellationToken);
        }
        catch (CrawldadApiException ex) when (ex.StatusCode == StatusCodes.Status400BadRequest)
        {
            return new WorkspaceLinkResult(
                WorkspaceLinkOutcome.OperatorManaged,
                $"‘{profile.TenantId}’ is an operator-configured tenant — it can't be claimed as a workspace. Ask your operator for access, or create a free workspace instead.");
        }
        catch (Exception ex) when (ex is CrawldadException or HttpRequestException)
        {
            return new WorkspaceLinkResult(
                WorkspaceLinkOutcome.Unverifiable,
                "We couldn't record your access to that workspace just now. Try again in a moment.");
        }

        return new WorkspaceLinkResult(
            WorkspaceLinkOutcome.Claimed,
            $"Workspace ‘{profile.TenantId}’ is now yours.");
    }
}
