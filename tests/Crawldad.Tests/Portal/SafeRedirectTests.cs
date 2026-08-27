using Crawldad.Portal.Auth;

namespace Crawldad.Tests.Portal;

public class SafeRedirectTests
{
    [Theory]
    [InlineData("/app/runs", "/app/runs")]
    [InlineData("/dashboard", "/dashboard")]
    [InlineData(null, "/app")]
    [InlineData("", "/app")]
    [InlineData("//evil.example", "/app")]
    [InlineData("https://evil.example", "/app")]
    [InlineData("notlocal", "/app")]
    public void Passes_through_only_same_site_paths(string? input, string expected) =>
        SafeRedirect.ToLocalOrApp(input).ShouldBe(expected);
}
