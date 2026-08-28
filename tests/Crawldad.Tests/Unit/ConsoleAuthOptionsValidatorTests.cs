using Crawldad.Api.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Crawldad.Tests.Unit;

/// <summary>The boot-time guard for <c>Crawldad:ConsoleAuth</c> (<see cref="ConsoleAuthOptionsValidator"/>): neither knob
/// set is the valid disabled posture; exactly one set is rejected (a half-configured scheme silently fails to
/// authenticate the portal); and when enabled the tenant must be a GUID and the required role non-empty.</summary>
public class ConsoleAuthOptionsValidatorTests
{
    private const string _tenant = "11111111-2222-3333-4444-555555555555";
    private const string _audience = "api://crawldad-api-stg";

    private static ValidateOptionsResult Validate(ConsoleAuthOptions options) =>
        new ConsoleAuthOptionsValidator().Validate(name: null, options);

    [Fact]
    public void Neither_knob_set_is_valid_disabled()
    {
        Validate(new ConsoleAuthOptions()).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Both_knobs_set_with_a_guid_tenant_is_valid()
    {
        Validate(new ConsoleAuthOptions { TenantId = _tenant, Audience = _audience }).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Tenant_without_audience_fails_closed()
    {
        var result = Validate(new ConsoleAuthOptions { TenantId = _tenant });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("BOTH TenantId and Audience");
    }

    [Fact]
    public void Audience_without_tenant_fails_closed()
    {
        var result = Validate(new ConsoleAuthOptions { Audience = _audience });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("BOTH TenantId and Audience");
    }

    [Fact]
    public void A_non_guid_tenant_is_rejected()
    {
        var result = Validate(new ConsoleAuthOptions { TenantId = "contoso.onmicrosoft.com", Audience = _audience });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("must be a GUID");
    }

    [Fact]
    public void An_empty_required_role_when_enabled_is_rejected()
    {
        var result = Validate(new ConsoleAuthOptions { TenantId = _tenant, Audience = _audience, RequiredRole = "  " });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("RequiredRole");
    }

    [Fact]
    public void Null_options_throws()
    {
        Should.Throw<ArgumentNullException>(() => Validate(null!));
    }
}
