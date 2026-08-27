using Crawldad.Client;
using Crawldad.Contracts.Tenancy;

namespace Crawldad.Portal.Tenancy;

/// <summary>The outcome of a workspace-link attempt. Only <see cref="Linked"/> persists anything — every other value
/// is a validated-and-rejected attempt that left no link written, so a bad key can never be stored.</summary>
public enum WorkspaceLinkOutcome
{
    /// <summary>The key authenticated and its tenant matched — the link was created or updated.</summary>
    Linked,

    /// <summary>The API rejected the key (<c>401</c>) — nothing was stored.</summary>
    InvalidKey,

    /// <summary>The key is valid but authenticates a different tenant than the one entered — nothing was stored.</summary>
    TenantMismatch,

    /// <summary>The key could not be verified (the API was unreachable or errored) — nothing was stored.</summary>
    Unverifiable,
}

/// <summary>The result of <see cref="IWorkspaceLinker.LinkAsync"/>: the <see cref="Outcome"/> and a user-facing
/// <see cref="Message"/> safe to render (it never contains key material).</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Message">A human-readable, non-sensitive description to show the account holder.</param>
public sealed record WorkspaceLinkResult(WorkspaceLinkOutcome Outcome, string Message);

/// <summary>Creates or updates a portal account's workspace link — the account area's real (non-dev-seed) link path.
/// It <b>always validates the submitted key against the live API before persisting</b>: a key that fails to
/// authenticate, or that authenticates the wrong tenant, is never written. The submitted key is treated like a
/// password throughout — passed straight to a one-shot client, never logged, echoed, or returned.</summary>
public interface IWorkspaceLinker
{
    /// <summary>Validates <paramref name="apiKey"/> by reading the tenant profile it authenticates, then — only when
    /// that succeeds and the profile's tenant matches <paramref name="tenantId"/> — upserts the link for
    /// <paramref name="email"/>. No API round-trip means no write.</summary>
    /// <param name="email">The signed-in account's email (the link identity).</param>
    /// <param name="tenantId">The workspace/tenant id the account holder entered (confirmed against the key).</param>
    /// <param name="apiKey">The tenant API key to validate and, on success, store encrypted. Never logged or echoed.</param>
    /// <param name="cancellationToken">Cancels the validation round-trip.</param>
    /// <returns>The outcome and a safe-to-render message.</returns>
    Task<WorkspaceLinkResult> LinkAsync(string email, string tenantId, string apiKey, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IWorkspaceLinker"/>
internal sealed class WorkspaceLinker : IWorkspaceLinker
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPortalTenantLinkStore _links;

    public WorkspaceLinker(IHttpClientFactory httpClientFactory, IPortalTenantLinkStore links)
    {
        _httpClientFactory = httpClientFactory;
        _links = links;
    }

    public async Task<WorkspaceLinkResult> LinkAsync(string email, string tenantId, string apiKey, CancellationToken cancellationToken = default)
    {
        // Validate the key by making the cheapest authenticated read (GET /tenant) through a one-shot client built for
        // THIS key — not the request's tenant client. The result decides whether we ever touch the store.
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

        // Validated: persist the link with the AUTHORITATIVE tenant id from the key (never the raw entered casing), and
        // the store protects the key at rest.
        await _links.UpsertAsync(email, profile.TenantId, apiKey, cancellationToken);
        return new WorkspaceLinkResult(
            WorkspaceLinkOutcome.Linked,
            $"Workspace ‘{profile.TenantId}’ is linked.");
    }
}
