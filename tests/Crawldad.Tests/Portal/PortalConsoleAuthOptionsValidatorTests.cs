using Crawldad.Portal.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Crawldad.Tests.Portal;

/// <summary>The portal console-auth boot guard (issue #119 PR4): neither knob set is the valid disabled posture; exactly
/// one set is a half-configured failure; and when enabled the tenant id must be a GUID. Mirrors the Data-Protection guard.</summary>
public class PortalConsoleAuthOptionsValidatorTests
{
    private static readonly PortalConsoleAuthOptionsValidator _validator = new();
    private const string _guid = "11111111-2222-3333-4444-555555555555";

    private static ValidateOptionsResult Validate(string tenantId, string audience) =>
        _validator.Validate(null, new PortalConsoleAuthOptions { TenantId = tenantId, Audience = audience });

    [Fact]
    public void Neither_set_is_the_valid_disabled_posture() =>
        Validate("", "").Succeeded.ShouldBeTrue();

    [Fact]
    public void Both_set_with_a_guid_tenant_is_valid() =>
        Validate(_guid, "api://crawldad-api-stg").Succeeded.ShouldBeTrue();

    [Fact]
    public void Only_the_tenant_is_half_configured() =>
        Validate(_guid, "").Failed.ShouldBeTrue();

    [Fact]
    public void Only_the_audience_is_half_configured() =>
        Validate("", "api://crawldad-api-stg").Failed.ShouldBeTrue();

    [Fact]
    public void A_non_guid_tenant_is_rejected()
    {
        var result = Validate("not-a-guid", "api://crawldad-api-stg");

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("GUID");
    }

    [Fact]
    public void Rejects_null_options() =>
        Should.Throw<ArgumentNullException>(() => _validator.Validate(null, null!));
}
