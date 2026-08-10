using Crawldad.Contracts.Browsers;

namespace Crawldad.Tests.Unit;

/// <summary>The register-request DTO redacts its secret from the record string form, so an accidental log of the
/// request (which would call the compiler-generated ToString) never carries credential material.</summary>
public class RegisterBrowserRequestTests
{
    [Fact]
    public void ToString_redacts_the_secret()
    {
        var request = new RegisterBrowserRequest("browserbase", "apiKey", "bb_live_SUPER_SECRET_value");
        var text = request.ToString();

        text.ShouldNotContain("bb_live_SUPER_SECRET_value");
        text.ShouldContain("[redacted]");
        text.ShouldContain("browserbase"); // non-secret metadata still shown
        text.ShouldContain("apiKey");
    }
}
