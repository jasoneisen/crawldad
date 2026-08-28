using System.Collections.Generic;
using System.Text;
using Crawldad.Api.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Crawldad.Tests.Support;

/// <summary>The CI test-issuer harness for the <see cref="ConsoleAuthModule"/> scheme (issue #119 PR2, the review's
/// finding #8). It exercises the <b>real</b> JwtBearer validation path with <b>test-issued</b> tokens by swapping only
/// the signing-key <i>source</i> — the production module wires signing keys from Entra's published metadata, and this
/// harness injects a static test key through a <b>test-only</b> <c>IConfigureNamedOptions&lt;JwtBearerOptions&gt;</c>
/// (<see cref="InjectTestKey"/>). That configurator lives in the test assembly and is never bound from configuration, so
/// there is no production config key that can select it — a different issuer <i>configuration</i> of the same scheme,
/// never a bypass branch. Everything else (issuer/audience/lifetime/role/version) is validated exactly as in production.</summary>
internal static class ConsoleAuthTestHarness
{
    /// <summary>The Entra directory GUID the test scheme is configured for; the v1.0 issuer is derived from it.</summary>
    public const string TenantId = "11111111-2222-3333-4444-555555555555";

    /// <summary>The API App-ID-URI audience the test scheme requires (the production shape, e.g. api://crawldad-api-stg).</summary>
    public const string Audience = "api://crawldad-console-test";

    /// <summary>The v1.0 issuer the production module derives from <see cref="TenantId"/> (sts.windows.net shape).</summary>
    public const string Issuer = "https://sts.windows.net/11111111-2222-3333-4444-555555555555/";

    /// <summary>The portal UAMI's client id a real v1.0 token carries in <c>appid</c> (immaterial to PR2 — the AppRole,
    /// not an appid pin, is the control — but stamped so the tokens are shaped like the real thing).</summary>
    public const string PortalAppId = "99999999-8888-7777-6666-555555555555";

    // The static test signing key. Test-only: injected via InjectTestKey below, NEVER read from configuration, so it
    // cannot be selected by any production config value. 64 bytes ⇒ valid for HS256.
    private static readonly SymmetricSecurityKey _signingKey =
        new(Encoding.UTF8.GetBytes("crawldad-pr2-console-auth-test-signing-key-0123456789-abcdef!!"));

    /// <summary>The test-only options configurator: points the scheme at the static test key instead of Entra metadata,
    /// so a self-issued token exercises the real validator with no network. Wired only in tests.</summary>
    public static void InjectTestKey(JwtBearerOptions jwt)
    {
        ArgumentNullException.ThrowIfNull(jwt);

        // Drop the production metadata source so no JWKS fetch is attempted; validate against the static key below.
        jwt.MetadataAddress = null!;
        jwt.Authority = null;
        jwt.ConfigurationManager = null;
        jwt.TokenValidationParameters.IssuerSigningKey = _signingKey;
    }

    /// <summary>Builds a <c>Crawldad:ConsoleAuth</c> configuration for the scheme (the production-shaped values).</summary>
    public static IConfiguration Configuration(string? tenantId = TenantId, string? audience = Audience, string? requiredRole = null)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (tenantId is not null)
        {
            settings[$"{ConsoleAuthOptions.Section}:TenantId"] = tenantId;
        }

        if (audience is not null)
        {
            settings[$"{ConsoleAuthOptions.Section}:Audience"] = audience;
        }

        if (requiredRole is not null)
        {
            settings[$"{ConsoleAuthOptions.Section}:RequiredRole"] = requiredRole;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    /// <summary>Mints a test v1.0-shaped access token signed by the static test key, with every claim tweakable so each
    /// validation branch (valid / wrong audience / wrong issuer / missing role / expired / v2-shaped) can be exercised.</summary>
    public static string MintToken(
        string issuer = Issuer,
        string audience = Audience,
        string? role = ConsoleAuthOptions.DefaultRequiredRole,
        string version = ConsoleAuthModule.TokenVersion,
        TimeSpan? lifetime = null)
    {
        var now = DateTime.UtcNow;
        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [ConsoleAuthModule.VersionClaim] = version,
            ["appid"] = PortalAppId,
        };
        if (role is not null)
        {
            claims[ConsoleAuthModule.RolesClaim] = new[] { role };
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.Add(lifetime ?? TimeSpan.FromMinutes(5)),
            SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256),
            Claims = claims,
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>Drives a bearer token through the <b>real</b> <c>ConsolePrincipal</c> JwtBearer handler (built from the
    /// production <see cref="ConsoleAuthModule"/> plus the test-key configurator) and returns the authenticate result —
    /// so a test can assert success for a valid token and failure for every rejected shape.</summary>
    public static async Task<AuthenticateResult> AuthenticateAsync(string? bearerToken, IConfiguration? configuration = null)
    {
        var config = configuration ?? Configuration();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(config);
        ConsoleAuthModule.AddConsolePrincipal(services, config);
        // Test-only: swap the signing-key source AFTER the module's own config (so it wins), leaving issuer/audience intact.
        services.Configure<JwtBearerOptions>(ConsoleAuthModule.Scheme, InjectTestKey);

        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        if (bearerToken is not null)
        {
            context.Request.Headers.Authorization = $"Bearer {bearerToken}";
        }

        var handler = await provider.GetRequiredService<IAuthenticationHandlerProvider>()
            .GetHandlerAsync(context, ConsoleAuthModule.Scheme);
        return await handler!.AuthenticateAsync();
    }
}
