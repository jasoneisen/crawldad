using System.Security.Claims;
using Crawldad.Portal.Auth;

namespace Crawldad.Tests.Portal;

public class PortalPrincipalTests
{
    [Fact]
    public void Builds_an_authenticated_principal_with_email_and_name_claims()
    {
        var principal = PortalPrincipal.Create("user@example.com", "Ada Lovelace");

        principal.Identity!.IsAuthenticated.ShouldBeTrue();
        principal.FindFirstValue(ClaimTypes.Email).ShouldBe("user@example.com");
        principal.FindFirstValue(ClaimTypes.NameIdentifier).ShouldBe("user@example.com");
        principal.FindFirstValue(ClaimTypes.Name).ShouldBe("Ada Lovelace");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Name_falls_back_to_email_when_display_name_is_blank(string? displayName)
    {
        var principal = PortalPrincipal.Create("user@example.com", displayName);

        principal.FindFirstValue(ClaimTypes.Name).ShouldBe("user@example.com");
    }
}
