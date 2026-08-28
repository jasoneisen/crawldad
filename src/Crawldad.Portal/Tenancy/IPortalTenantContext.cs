using System.Security.Claims;
using Crawldad.Client;
using Crawldad.Portal.Auth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace Crawldad.Portal.Tenancy;

/// <summary>The per-request bridge from the signed-in portal user to the Crawldad tenant they act as. Resolves the
/// user's <see cref="PortalTenantLink"/> once per request and hands back a <see cref="PortalTenant"/> — the tenant
/// id plus a <see cref="CrawldadClient"/> already authenticated as that tenant. When the request is unauthenticated
/// or the account has no link, resolution is a clean "not linked" state, never a client with an empty API key: the
/// data pages either branch on <see cref="TryResolveAsync"/> returning <see langword="null"/> or catch the
/// <see cref="NotLinkedException"/> from <see cref="RequireAsync"/>.</summary>
public interface IPortalTenantContext
{
    /// <summary>Resolves the current user's tenant (caching for the rest of the request), or <see langword="null"/>
    /// when unauthenticated or unlinked. The non-throwing shape for rendering a "link your tenant" empty state.</summary>
    Task<PortalTenant?> TryResolveAsync(CancellationToken cancellationToken = default);

    /// <summary>Resolves the current user's tenant, throwing <see cref="NotLinkedException"/> when unauthenticated or
    /// unlinked. The throwing shape for pages that guard their API calls with a single catch.</summary>
    Task<PortalTenant> RequireAsync(CancellationToken cancellationToken = default);
}

/// <summary>How a resolved <see cref="PortalTenant"/> authenticates to the API (issue #119 PR4).</summary>
public enum PortalAuthMode
{
    /// <summary>The stored tenant API key (the default, and the only mode when <c>Crawldad:ConsoleAuth</c> is unconfigured).</summary>
    Key,

    /// <summary>The portal's first-party console credential (bearer token + membership), with the stored key as fallback.</summary>
    Console,
}

/// <summary>A resolved tenant for the current request: the tenant id and a <see cref="CrawldadClient"/> authenticated
/// as that tenant. The underlying API key is consumed to build the client and never exposed.</summary>
public sealed class PortalTenant
{
    internal PortalTenant(string tenantId, CrawldadClient client, PortalAuthMode authMode = PortalAuthMode.Key)
    {
        TenantId = tenantId;
        Client = client;
        AuthMode = authMode;
    }

    /// <summary>The Crawldad tenant this request acts as (for display; the API derives the tenant from the credential).</summary>
    public string TenantId { get; }

    /// <summary>A Crawldad API client bound to this tenant — the one handle the data pages call the API through.</summary>
    public CrawldadClient Client { get; }

    /// <summary>How this request authenticates: the stored key, or the console credential (issue #119 PR4). The account
    /// area surfaces it as the workspace's console-access state.</summary>
    public PortalAuthMode AuthMode { get; }
}

/// <inheritdoc cref="IPortalTenantContext"/>
internal sealed class PortalTenantContext : IPortalTenantContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IPortalTenantLinkStore _links;
    private readonly IDataProtector _protector;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConsoleClientFactory? _consoleClients;

    private bool _resolved;
    private PortalTenant? _tenant;

    public PortalTenantContext(
        IHttpContextAccessor httpContextAccessor,
        IPortalTenantLinkStore links,
        IDataProtectionProvider protection,
        IHttpClientFactory httpClientFactory,
        ConsoleClientFactory? consoleClients = null)
    {
        _httpContextAccessor = httpContextAccessor;
        _links = links;
        _protector = PortalTenancy.ApiKeyProtector(protection);
        _httpClientFactory = httpClientFactory;
        _consoleClients = consoleClients; // present only when Crawldad:ConsoleAuth is configured → console-mode
    }

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
            ?? throw new NotLinkedException("The current portal user is not linked to a Crawldad tenant.");

    private async Task<PortalTenant?> ResolveCoreAsync(CancellationToken cancellationToken)
    {
        var email = CurrentUserEmail();
        if (email is null)
        {
            return null; // unauthenticated → no tenant
        }

        var link = await _links.GetAsync(email, cancellationToken);
        if (link is null)
        {
            return null; // authenticated but not yet linked
        }

        var apiKey = _protector.Unprotect(link.ProtectedApiKey);

        // Console-mode (Crawldad:ConsoleAuth configured): dashboard reads go through the portal's first-party console
        // credential (bearer token + membership selectors), with the stored key as fallback for writes and for reads whose
        // membership does not yet exist. Unconfigured (_consoleClients is null) is the byte-identical stored-key path.
        if (_consoleClients is not null)
        {
            var consoleClient = _consoleClients.Build(email, link.TenantId, apiKey);
            return new PortalTenant(link.TenantId, consoleClient, PortalAuthMode.Console);
        }

        var http = _httpClientFactory.CreateClient(PortalTenancy.ApiHttpClientName); // base address preset at wiring
        var client = new CrawldadClient(http, new CrawldadClientOptions { ApiKey = apiKey });
        return new PortalTenant(link.TenantId, client);
    }

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
