using System.Security.Claims;
using Crawldad.Portal.Auth;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;

namespace Crawldad.Portal.Tenancy;

/// <summary>The circuit-safe sibling of <see cref="IPortalTenantContext"/>. The interactive live-trace page runs over a
/// Blazor <c>InteractiveServer</c> circuit, where <see cref="Microsoft.AspNetCore.Http.IHttpContextAccessor"/> — the
/// identity source the per-request context relies on — is <see langword="null"/>. This resolver instead reads the
/// signed-in user from the <see cref="AuthenticationStateProvider"/> (the framework seeds it from the authenticated
/// connection on a circuit, and from <c>HttpContext.User</c> during prerender), so it resolves the same
/// <see cref="PortalTenant"/> the static-SSR pages get — a tenant id plus a <see cref="CrawldadClient"/> authenticated
/// as that tenant. The tenant API key is decrypted into the client and never leaves server memory: it is not written to
/// <see cref="Microsoft.AspNetCore.Components.PersistentComponentState"/>, a component parameter, or anything that
/// reaches the browser.</summary>
public interface ICircuitTenantResolver
{
    /// <summary>Resolves the current circuit user's tenant, or <see langword="null"/> when the circuit is
    /// unauthenticated or the account has no <see cref="PortalTenantLink"/> — the non-throwing shape the live page
    /// branches on to render its "link your workspace" empty state (never a client with an empty API key).</summary>
    Task<PortalTenant?> TryResolveAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ICircuitTenantResolver"/>
internal sealed class CircuitTenantResolver : ICircuitTenantResolver
{
    private readonly AuthenticationStateProvider _authState;
    private readonly IPortalTenantLinkStore _links;
    private readonly IDataProtector _protector;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConsoleClientFactory? _consoleClients;

    public CircuitTenantResolver(
        AuthenticationStateProvider authState,
        IPortalTenantLinkStore links,
        IDataProtectionProvider protection,
        IHttpClientFactory httpClientFactory,
        ConsoleClientFactory? consoleClients = null)
    {
        _authState = authState;
        _links = links;
        _protector = PortalTenancy.ApiKeyProtector(protection);
        _httpClientFactory = httpClientFactory;
        _consoleClients = consoleClients; // present only when Crawldad:ConsoleAuth is configured → console-mode
    }

    public async Task<PortalTenant?> TryResolveAsync(CancellationToken cancellationToken = default)
    {
        var email = await CurrentUserEmailAsync();
        if (email is null)
        {
            return null; // unauthenticated circuit → no tenant
        }

        var link = await _links.GetAsync(email, cancellationToken);
        if (link is null)
        {
            return null; // authenticated but not yet linked
        }

        // The same console-vs-key resolution the static-SSR PortalTenantContext uses (issue #119 PR5), shared so the two
        // never drift. The plaintext key (stored-key mode, or a console transition fallback) lives on this server-side object
        // and is never serialized to the circuit's browser.
        return PortalTenantResolution.Resolve(email, link, _protector, _httpClientFactory, _consoleClients);
    }

    // The signed-in circuit account's normalized email, or null when the circuit carries no authenticated principal or
    // no email claim. Reads ClaimTypes.Email and normalizes through PortalAuthService.NormalizeEmail — byte-for-byte the
    // same identity the OTP sign-in stores and the static-SSR PortalTenantContext resolves, so a circuit and a request
    // for the same user land on the same PortalTenantLink.
    private async Task<string?> CurrentUserEmailAsync()
    {
        var state = await _authState.GetAuthenticationStateAsync();
        var user = state.User;
        if (!user.Identities.Any(static identity => identity.IsAuthenticated))
        {
            return null; // anonymous circuit
        }

        var email = user.FindFirstValue(ClaimTypes.Email);
        return string.IsNullOrWhiteSpace(email) ? null : PortalAuthService.NormalizeEmail(email);
    }
}
