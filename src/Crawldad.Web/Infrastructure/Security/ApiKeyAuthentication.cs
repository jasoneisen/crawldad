using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crawldad.Web.Infrastructure.Security;

/// <summary>Well-known names for the machine-to-machine API-key scheme (CD-1). Deliberately the simplest credible mechanism
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
/// mutation events — always from the principal, never a request body (§12).</summary>
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

/// <summary>
/// The API-key authentication handler (CD-1): reads the key from <c>Authorization: Bearer &lt;key&gt;</c> or
/// <c>X-Api-Key: &lt;key&gt;</c>, validates it against the <see cref="TenantRegistry"/> (fixed-time hash compare), and on
/// success issues a principal carrying the tenant + actor claims. A missing key is <see cref="AuthenticateResult.NoResult"/>
/// and an unknown key is a <see cref="AuthenticateResult.Fail(string)"/> — both surface as <c>401</c> through the
/// authorization layer (every route requires an authenticated tenant, §12). The key itself is never logged.
/// </summary>
internal sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyOptions>
{
    private const string _bearerPrefix = "Bearer ";
    private readonly TenantRegistry _tenants;

    public ApiKeyAuthenticationHandler(IOptionsMonitor<ApiKeyOptions> options, ILoggerFactory logger, UrlEncoder encoder, TenantRegistry tenants)
        : base(options, logger, encoder) => _tenants = tenants;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!TryReadKey(out var key))
        {
            return Task.FromResult(AuthenticateResult.NoResult()); // no credential presented → challenged as 401
        }

        if (!_tenants.TryAuthenticate(key, out var tenant))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key.")); // never echoes the key
        }

        var identity = new ClaimsIdentity(
            [new Claim(CrawldadClaims.TenantId, tenant.Value.Id), new Claim(CrawldadClaims.Actor, tenant.Value.Actor)],
            Scheme.Name,
            nameType: CrawldadClaims.Actor,
            roleType: null);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
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
