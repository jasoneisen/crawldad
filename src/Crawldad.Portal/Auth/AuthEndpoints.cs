using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace Crawldad.Portal.Auth;

/// <summary>Plain HTTP endpoints backing the auth flow that a Blazor component can't own — here, sign-out, which
/// must clear the cookie on the real response.</summary>
internal static class AuthEndpoints
{
    internal static void MapPortalAuth(this IEndpointRouteBuilder endpoints)
    {
        // Sign-out is a state change, so a POST — and because it binds a form value, UseAntiforgery enforces the
        // token that the shell's sign-out form carries. LocalRedirect refuses any non-local target.
        endpoints.MapPost("/auth/signout", async (HttpContext http, [FromForm] string? returnUrl) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.LocalRedirect(string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl);
        });
    }
}
