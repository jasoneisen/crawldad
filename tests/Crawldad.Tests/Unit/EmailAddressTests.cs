using Crawldad.Contracts;
using Crawldad.Portal.Auth;

namespace Crawldad.Tests.Unit;

/// <summary>The single shared email normalizer (issue #119 PR4, finding #2). It is the historical
/// <c>Trim().ToLowerInvariant()</c> behaviour, and the portal's <c>NormalizeEmail</c> now delegates to it — so a membership
/// the API writes under an email and a lookup the portal does under that email fold to byte-identical keys. A drift here
/// would silently 403 legitimate users, so parity is pinned.</summary>
public class EmailAddressTests
{
    [Theory]
    [InlineData("  User@Example.COM  ", "user@example.com")]
    [InlineData("already@lower.test", "already@lower.test")]
    [InlineData("MixedCase@X.io", "mixedcase@x.io")]
    [InlineData("\tTabbed@W.dev\n", "tabbed@w.dev")]
    public void Normalize_trims_and_lowercases(string input, string expected) =>
        EmailAddress.Normalize(input).ShouldBe(expected);

    [Theory]
    [InlineData("  A@B.com ")]
    [InlineData("x@y.test")]
    [InlineData("MiXeD@Z.IO")]
    [InlineData("\tTabbed@w.dev\n")]
    public void The_portal_normalizer_delegates_to_the_shared_one_byte_for_byte(string input) =>
        PortalAuthService.NormalizeEmail(input).ShouldBe(EmailAddress.Normalize(input));
}
