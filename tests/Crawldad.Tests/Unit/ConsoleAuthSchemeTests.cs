using Crawldad.Api.Infrastructure.Security;
using Crawldad.Tests.Support;

namespace Crawldad.Tests.Unit;

/// <summary>Drives the <b>real</b> <c>ConsolePrincipal</c> JwtBearer handler (the production <see cref="ConsoleAuthModule"/>
/// wiring) with test-issued tokens, so every validation branch runs against genuine crypto rather than a bypass. The only
/// thing swapped for the test is the signing-key <i>source</i> (a static test key instead of Entra's JWKS), via the
/// test-only configurator in <see cref="ConsoleAuthTestHarness"/> — issuer, audience, lifetime, version, and the
/// fail-closed AppRole check are validated exactly as in production (issue #119 PR2, review findings #5/#8).</summary>
public class ConsoleAuthSchemeTests
{
    [Fact]
    public async Task A_valid_v1_token_with_the_required_role_authenticates()
    {
        var result = await ConsoleAuthTestHarness.AuthenticateAsync(ConsoleAuthTestHarness.MintToken());

        result.Succeeded.ShouldBeTrue();
        result.Principal!.HasClaim(ConsoleAuthModule.RolesClaim, ConsoleAuthOptions.DefaultRequiredRole).ShouldBeTrue();
        result.Principal.HasClaim(ConsoleAuthModule.VersionClaim, ConsoleAuthModule.TokenVersion).ShouldBeTrue();
    }

    [Fact]
    public async Task No_token_is_not_authenticated()
    {
        var result = await ConsoleAuthTestHarness.AuthenticateAsync(bearerToken: null);

        result.Succeeded.ShouldBeFalse();
        result.None.ShouldBeTrue(); // no credential presented → NoResult (challenged as 401 by the pipeline)
    }

    [Fact]
    public async Task A_token_for_the_wrong_audience_is_rejected()
    {
        var result = await ConsoleAuthTestHarness.AuthenticateAsync(
            ConsoleAuthTestHarness.MintToken(audience: "api://someone-elses-api"));

        result.Succeeded.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_token_from_the_wrong_issuer_is_rejected()
    {
        var result = await ConsoleAuthTestHarness.AuthenticateAsync(
            ConsoleAuthTestHarness.MintToken(issuer: "https://sts.windows.net/00000000-0000-0000-0000-000000000000/"));

        result.Succeeded.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
    }

    [Fact]
    public async Task An_expired_token_is_rejected()
    {
        var result = await ConsoleAuthTestHarness.AuthenticateAsync(
            ConsoleAuthTestHarness.MintToken(lifetime: TimeSpan.FromMinutes(-5)));

        result.Succeeded.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_token_without_the_required_role_is_rejected_fail_closed()
    {
        var result = await ConsoleAuthTestHarness.AuthenticateAsync(ConsoleAuthTestHarness.MintToken(role: null));

        result.Succeeded.ShouldBeFalse();
        result.Failure!.Message.ShouldContain("AppRole");
    }

    [Fact]
    public async Task A_v2_shaped_token_is_rejected_because_the_scheme_pins_v1()
    {
        // A token that otherwise validates (issuer/audience/signature/lifetime) but carries ver=2.0 is refused — the
        // scheme pins the managed-identity v1.0 shape.
        var result = await ConsoleAuthTestHarness.AuthenticateAsync(ConsoleAuthTestHarness.MintToken(version: "2.0"));

        result.Succeeded.ShouldBeFalse();
        result.Failure!.Message.ShouldContain("v1.0");
    }

    [Fact]
    public async Task The_required_role_is_configurable_and_enforced()
    {
        var config = ConsoleAuthTestHarness.Configuration(requiredRole: "Console.Admin");

        // The default-role token no longer satisfies the configured role → rejected.
        var wrongRole = await ConsoleAuthTestHarness.AuthenticateAsync(ConsoleAuthTestHarness.MintToken(), config);
        wrongRole.Succeeded.ShouldBeFalse();

        // A token carrying the configured role authenticates.
        var rightRole = await ConsoleAuthTestHarness.AuthenticateAsync(
            ConsoleAuthTestHarness.MintToken(role: "Console.Admin"), config);
        rightRole.Succeeded.ShouldBeTrue();
    }
}
