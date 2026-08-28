using System.Collections.Generic;
using System.Linq;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Tests.Support;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Crawldad.Tests.Unit;

/// <summary>The console-auth scheme wiring (<see cref="ConsoleAuthModule"/>): unconfigured leaves the scheme unregistered
/// (so <c>ApiKey</c> stays the sole/default scheme and the host is unchanged); configured registers a non-default
/// JwtBearer scheme whose signing keys come <b>only</b> from Entra metadata — never a static/config key (the review's
/// finding #8). The finding-#8 invariant is asserted directly: there is no production config value that can inject a
/// signing key or a trusted issuer.</summary>
public class ConsoleAuthModuleTests
{
    private const string _tenant = "11111111-2222-3333-4444-555555555555";
    private const string _audience = "api://crawldad-api-stg";

    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value, StringComparer.Ordinal))
            .Build();

    private static JwtBearerOptions ResolveJwtOptions(IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(config);
        ConsoleAuthModule.AddConsolePrincipal(services, config);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(ConsoleAuthModule.Scheme);
    }

    private static async Task<AuthenticationScheme?> ResolveSchemeAsync(IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(config);
        // Mirror the host: ApiKey is the pre-existing default scheme; the module adds ConsolePrincipal on top (or not).
        services.AddAuthentication(CrawldadAuthentication.Scheme);
        ConsoleAuthModule.AddConsolePrincipal(services, config);
        await using var provider = services.BuildServiceProvider();
        return await provider.GetRequiredService<IAuthenticationSchemeProvider>().GetSchemeAsync(ConsoleAuthModule.Scheme);
    }

    [Fact]
    public async Task Unconfigured_does_not_register_the_console_scheme()
    {
        (await ResolveSchemeAsync(Config())).ShouldBeNull();
    }

    [Fact]
    public async Task Configured_registers_the_console_scheme_as_a_jwt_bearer_handler()
    {
        var scheme = await ResolveSchemeAsync(Config(
            ($"{ConsoleAuthOptions.Section}:TenantId", _tenant),
            ($"{ConsoleAuthOptions.Section}:Audience", _audience)));

        scheme.ShouldNotBeNull();
        scheme.HandlerType.ShouldBe(typeof(JwtBearerHandler));
    }

    [Fact]
    public void Configured_validates_against_entra_metadata_with_no_static_signing_key()
    {
        var jwt = ResolveJwtOptions(Config(
            ($"{ConsoleAuthOptions.Section}:TenantId", _tenant),
            ($"{ConsoleAuthOptions.Section}:Audience", _audience)));

        // Keys + trusted issuer are pinned to Entra's published metadata for this tenant (v1.0 shape).
        jwt.MetadataAddress.ShouldBe($"https://login.microsoftonline.com/{_tenant}/.well-known/openid-configuration");
        jwt.TokenValidationParameters.ValidIssuer.ShouldBe($"https://sts.windows.net/{_tenant}/");
        jwt.Audience.ShouldBe(_audience);
        jwt.MapInboundClaims.ShouldBeFalse();

        // Finding #8: no static signing key is ever set — the keys come only from the metadata/JWKS above.
        jwt.TokenValidationParameters.IssuerSigningKey.ShouldBeNull();
        jwt.TokenValidationParameters.IssuerSigningKeys.ShouldBeNull();
    }

    [Fact]
    public void No_production_config_key_can_inject_a_signing_key_or_a_trusted_issuer()
    {
        // Even with attacker-shaped extra keys under the section, the module binds ONLY TenantId/Audience/RequiredRole,
        // so the resolved validation parameters still trust exactly the Entra issuer and hold no static key.
        var jwt = ResolveJwtOptions(Config(
            ($"{ConsoleAuthOptions.Section}:TenantId", _tenant),
            ($"{ConsoleAuthOptions.Section}:Audience", _audience),
            ($"{ConsoleAuthOptions.Section}:SigningKey", "not-a-real-option-deadbeef"),
            ($"{ConsoleAuthOptions.Section}:IssuerSigningKey", "not-a-real-option"),
            ($"{ConsoleAuthOptions.Section}:Issuer", "https://sts.windows.net/attacker/"),
            ($"{ConsoleAuthOptions.Section}:MetadataAddress", "https://evil.example/.well-known/openid-configuration")));

        jwt.TokenValidationParameters.IssuerSigningKey.ShouldBeNull();
        jwt.TokenValidationParameters.IssuerSigningKeys.ShouldBeNull();
        jwt.TokenValidationParameters.ValidIssuer.ShouldBe($"https://sts.windows.net/{_tenant}/");
        jwt.MetadataAddress.ShouldBe($"https://login.microsoftonline.com/{_tenant}/.well-known/openid-configuration");
    }

    [Fact]
    public void The_options_type_exposes_no_signing_key_or_issuer_override_knob()
    {
        // The structural half of finding #8: the config-bound options carry nothing that could select a key or issuer.
        var settable = typeof(ConsoleAuthOptions).GetProperties()
            .Where(p => p.CanWrite)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        settable.ShouldBe(new HashSet<string>(StringComparer.Ordinal) { "TenantId", "Audience", "RequiredRole" });
    }

    [Fact]
    public void AddConsolePrincipal_rejects_null_arguments()
    {
        var services = new ServiceCollection();
        var config = Config();
        Should.Throw<ArgumentNullException>(() => ConsoleAuthModule.AddConsolePrincipal(null!, config));
        Should.Throw<ArgumentNullException>(() => ConsoleAuthModule.AddConsolePrincipal(services, null!));
    }
}
