using System.Security.Claims;
using Crawldad.Client;
using Crawldad.Portal.Auth;
using Microsoft.AspNetCore.Http;

namespace Crawldad.Portal.Tenancy;

/// <summary>The per-request bridge from the signed-in portal user to the Crawldad workspace (tenant) they act as. The portal
/// is <b>console-mode only</b> for data (issue #119 simplification): it calls the API exclusively as its first-party console
/// identity, and the API's membership store is the authority for which workspaces a user may act as. Resolution reads the
/// account's active-workspace pointer (<see cref="PortalWorkspaceSelection"/>) once per request and hands back a
/// <see cref="PortalTenant"/> — the workspace id plus a <see cref="CrawldadClient"/> authenticated as the portal console
/// identity for that workspace. There is <b>no stored tenant key</b> anywhere. When the request is unauthenticated, the
/// account has no active workspace yet, or console access is not configured on the deployment, resolution is a clean
/// <see langword="null"/> — never a client with no credential; <see cref="ConsoleConfigured"/> lets a page tell the
/// "no workspace yet" case apart from the honest "console access not configured" case.</summary>
public interface IPortalTenantContext
{
    /// <summary>Whether console access is configured on this deployment (<c>Crawldad:ConsoleAuth</c> set). When
    /// <see langword="false"/> the portal cannot reach the API for data at all, so a data page renders an honest
    /// "console access not configured" state rather than a "no workspace yet" one. Dev and production both run
    /// console-mode configured (with a test/fake token source in dev/CI); an unconfigured portal is an operator misconfig.</summary>
    bool ConsoleConfigured { get; }

    /// <summary>Resolves the current user's active workspace (caching for the rest of the request), or <see langword="null"/>
    /// when unauthenticated, without an active workspace, or when console access is unconfigured. The non-throwing shape for
    /// rendering an empty state.</summary>
    Task<PortalTenant?> TryResolveAsync(CancellationToken cancellationToken = default);

    /// <summary>Resolves the current user's active workspace, throwing <see cref="NotLinkedException"/> when there is none.
    /// The throwing shape for pages that guard their API calls with a single catch.</summary>
    Task<PortalTenant> RequireAsync(CancellationToken cancellationToken = default);
}

/// <summary>A resolved workspace for the current request: the workspace (tenant) id and a <see cref="CrawldadClient"/>
/// authenticated as the portal console identity acting for that workspace.</summary>
public sealed class PortalTenant
{
    internal PortalTenant(string tenantId, CrawldadClient client)
    {
        TenantId = tenantId;
        Client = client;
    }

    /// <summary>The Crawldad workspace (tenant) id this request acts as (for display/selectors; the API derives authority from
    /// the console credential + membership store, never from this value).</summary>
    public string TenantId { get; }

    /// <summary>A Crawldad API client bound to this workspace via the portal console credential — the one handle the data
    /// pages call the API through.</summary>
    public CrawldadClient Client { get; }
}

/// <inheritdoc cref="IPortalTenantContext"/>
internal sealed class PortalTenantContext : IPortalTenantContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IPortalWorkspaceSelectionStore _selections;
    private readonly ConsoleClientFactory? _consoleClients;

    private bool _resolved;
    private PortalTenant? _tenant;

    public PortalTenantContext(
        IHttpContextAccessor httpContextAccessor,
        IPortalWorkspaceSelectionStore selections,
        ConsoleClientFactory? consoleClients = null)
    {
        _httpContextAccessor = httpContextAccessor;
        _selections = selections;
        _consoleClients = consoleClients; // present only when Crawldad:ConsoleAuth is configured → console-mode
    }

    public bool ConsoleConfigured => _consoleClients is not null;

    public async Task<PortalTenant?> TryResolveAsync(CancellationToken cancellationToken = default)
    {
        if (!_resolved)
        {
            _tenant = await ResolveCoreAsync(cancellationToken);
            _resolved = true;
        }

        return _tenant;
    }

    public async Task<PortalTenant> RequireAsync(CancellationToken cancellationToken = default) =>
        await TryResolveAsync(cancellationToken)
            ?? throw new NotLinkedException("The current portal user has no active Crawldad workspace.");

    private Task<PortalTenant?> ResolveCoreAsync(CancellationToken cancellationToken) =>
        // The active workspace is the account's stored selection (written by signup / claim / switch); its membership is the
        // API's authority. Unauthenticated, unconfigured, or no selection all resolve to a clean null.
        PortalTenantResolution.ResolveAsync(CurrentUserEmail(), _selections, _consoleClients, cancellationToken);

    // The signed-in account's normalized email, or null when the request carries no authenticated portal principal.
    private string? CurrentUserEmail()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            return null; // resolved outside a request (no ambient HttpContext)
        }

        if (!httpContext.User.Identities.Any(static identity => identity.IsAuthenticated))
        {
            return null; // anonymous request
        }

        var email = httpContext.User.FindFirstValue(ClaimTypes.Email);
        return string.IsNullOrWhiteSpace(email) ? null : PortalAuthService.NormalizeEmail(email);
    }
}
