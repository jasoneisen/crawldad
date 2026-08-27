using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crawldad.Api.Infrastructure.Security;

/// <summary>Well-known names for the machine-to-machine API-key scheme. Deliberately the simplest credible mechanism
/// for a hosted API product: a per-tenant key, no ASP.NET Identity / OIDC ceremony (a real IdP is a later ticket).</summary>
public static class CrawldadAuthentication
{
    /// <summary>The authentication scheme name.</summary>
    public const string Scheme = "ApiKey";

    /// <summary>The <c>X-Api-Key</c> header alternative to <c>Authorization: Bearer</c>.</summary>
    public const string ApiKeyHeader = "X-Api-Key";
}

/// <summary>The claim types the authenticated principal carries. The tenant claim is what Wolverine's HTTP tenant detection
/// reads to scope every request-opened Marten session (<c>IsClaimTypeNamed</c>); the actor claim is stamped onto payload
/// mutation events — always from the principal, never a request body.</summary>
internal static class CrawldadClaims
{
    /// <summary>The Marten tenant partition id.</summary>
    public const string TenantId = "crawldad:tenant_id";

    /// <summary>The actor/display identity stamped on mutation events.</summary>
    public const string Actor = "crawldad:actor";
}

/// <summary>Options for the <see cref="ApiKeyAuthenticationHandler"/>. No settings today — the handler validates against the
/// configured <see cref="TenantRegistry"/>; the type exists so the scheme plugs into ASP.NET's authentication builder.</summary>
public sealed class ApiKeyOptions : AuthenticationSchemeOptions;

/// <summary>The API-key authentication handler: validates the presented key against the <see cref="ITenantAuthenticator"/>
/// (the DB-backed registry, cached, with an env-configured fallback) and issues a principal carrying the tenant + actor
/// claims. A missing, unknown, revoked, or suspended-tenant key surfaces as <c>401</c>; the key itself is never logged.</summary>
internal sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyOptions>
{
    private const string _bearerPrefix = "Bearer ";
    private readonly ITenantAuthenticator _authenticator;

    public ApiKeyAuthenticationHandler(IOptionsMonitor<ApiKeyOptions> options, ILoggerFactory logger, UrlEncoder encoder, ITenantAuthenticator authenticator)
        : base(options, logger, encoder) => _authenticator = authenticator;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!TryReadKey(out var key))
        {
            return AuthenticateResult.NoResult(); // no credential presented → challenged as 401
        }

        var tenant = await _authenticator.AuthenticateAsync(key, Context.RequestAborted);
        if (tenant is null)
        {
            return AuthenticateResult.Fail("Invalid API key."); // unknown/revoked/suspended — never echoes the key
        }

        var identity = new ClaimsIdentity(
            [new Claim(CrawldadClaims.TenantId, tenant.Value.Id), new Claim(CrawldadClaims.Actor, tenant.Value.Actor)],
            Scheme.Name,
            nameType: CrawldadClaims.Actor,
            roleType: null);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }

    // Prefer Authorization: Bearer, then X-Api-Key. A present-but-empty value is treated as absent.
    private bool TryReadKey([NotNullWhen(true)] out string? key)
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (authorization.StartsWith(_bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            key = authorization[_bearerPrefix.Length..].Trim();
            return key.Length > 0;
        }

        var apiKey = Request.Headers[CrawldadAuthentication.ApiKeyHeader].ToString();
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            key = apiKey;
            return true;
        }

        key = null;
        return false;
    }
}
