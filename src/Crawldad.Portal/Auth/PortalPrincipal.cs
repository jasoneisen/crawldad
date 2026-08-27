using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Crawldad.Portal.Auth;

/// <summary>Builds the cookie <see cref="ClaimsPrincipal"/> for a signed-in portal user. The email is both the
/// name identifier and the email claim; the display name falls back to the email when unset.</summary>
internal static class PortalPrincipal
{
    internal static ClaimsPrincipal Create(string email, string? displayName)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, email),
            new(ClaimTypes.Email, email),
            new(ClaimTypes.Name, string.IsNullOrWhiteSpace(displayName) ? email : displayName),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }
}
