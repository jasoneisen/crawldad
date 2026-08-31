using System.Security.Claims;
using Crawldad.Portal.Auth;
using Microsoft.AspNetCore.Components.Authorization;

namespace Crawldad.Portal.Tenancy;

/// <summary>The circuit-safe sibling of <see cref="IPortalTenantContext"/>. The interactive live-trace page runs over a
/// Blazor <c>InteractiveServer</c> circuit, where <see cref="Microsoft.AspNetCore.Http.IHttpContextAccessor"/> — the
/// identity source the per-request context relies on — is <see langword="null"/>. This resolver instead reads the
/// signed-in user from the <see cref="AuthenticationStateProvider"/> (the framework seeds it from the authenticated
/// connection on a circuit, and from <c>HttpContext.User</c> during prerender), so it resolves the same
/// <see cref="PortalTenant"/> the static-SSR pages get — a workspace id plus a <see cref="CrawldadClient"/> authenticated as
/// the portal console identity for that workspace. The console token lives only in server-side circuit memory: it is not
/// written to <see cref="Microsoft.AspNetCore.Components.PersistentComponentState"/>, a component parameter, or anything that
/// reaches the browser.</summary>
public interface ICircuitTenantResolver
{
    /// <summary>Whether console access is configured on this deployment — the circuit-side mirror of
    /// <see cref="IPortalTenantContext.ConsoleConfigured"/>, so the live page can tell "no workspace yet" apart from
    /// "console access not configured".</summary>
    bool ConsoleConfigured { get; }

    /// <summary>Resolves the current circuit user's active workspace, or <see langword="null"/> when the circuit is
    /// unauthenticated, has no active workspace, or console access is unconfigured — the non-throwing shape the live page
    /// branches on to render its empty state (never a client with no credential).</summary>
    Task<PortalTenant?> TryResolveAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ICircuitTenantResolver"/>
internal sealed class CircuitTenantResolver : ICircuitTenantResolver
{
    private readonly AuthenticationStateProvider _authState;
    private readonly IPortalWorkspaceSelectionStore _selections;
    private readonly ConsoleClientFactory? _consoleClients;

    public CircuitTenantResolver(
        AuthenticationStateProvider authState,
        IPortalWorkspaceSelectionStore selections,
        ConsoleClientFactory? consoleClients = null)
    {
        _authState = authState;
        _selections = selections;
        _consoleClients = consoleClients; // present only when Crawldad:ConsoleAuth is configured → console-mode
    }

    public bool ConsoleConfigured => _consoleClients is not null;

    public async Task<PortalTenant?> TryResolveAsync(CancellationToken cancellationToken = default)
    {
        // The same active-workspace + console resolution the static-SSR PortalTenantContext uses, shared so the two never
        // drift. The console token lives on this server-side object and is never serialized to the circuit's browser.
        var email = await CurrentUserEmailAsync();
        return await PortalTenantResolution.ResolveAsync(email, _selections, _consoleClients, cancellationToken);
    }

    // The signed-in circuit account's normalized email, or null when the circuit carries no authenticated principal or
    // no email claim. Reads ClaimTypes.Email and normalizes through PortalAuthService.NormalizeEmail — byte-for-byte the
    // same identity the OTP sign-in stores and the static-SSR PortalTenantContext resolves, so a circuit and a request
    // for the same user land on the same active workspace.
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
