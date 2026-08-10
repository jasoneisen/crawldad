using System.Collections.Generic;
using Crawldad.Contracts.Browsers;
using Crawldad.Web.Features.Browsers;

namespace Crawldad.Tests.Unit;

/// <summary>Boundary validation for the register body: adapter/mode known, secret non-empty, a connectUrl secret is
/// wss/https-shaped, options carry no empty value. The name (route key) is guarded in the endpoint, not here.</summary>
public class RegisterBrowserRequestValidatorTests
{
    private static readonly RegisterBrowserRequestValidator _validator = new();

    private static bool IsValid(string adapter, string mode, string secret,
        IReadOnlyDictionary<string, string>? options = null) =>
        _validator.Validate(new RegisterBrowserRequest(adapter, mode, secret, options)).IsValid;

    [Fact]
    public void Accepts_an_apiKey_registration() =>
        IsValid("browserbase", "apiKey", "bb_live_apikey").ShouldBeTrue();

    [Fact]
    public void Accepts_a_connectUrl_registration() =>
        IsValid("browserbase", "connectUrl", "wss://connect.example.com/?signingKey=x").ShouldBeTrue();

    [Fact]
    public void Accepts_options_with_non_empty_values() =>
        IsValid("browserless", "apiKey", "tok", new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = "sfo" }).ShouldBeTrue();

    [Fact]
    public void Rejects_an_unknown_adapter() => IsValid("selenium", "apiKey", "tok").ShouldBeFalse();

    [Fact]
    public void Rejects_an_unknown_mode() => IsValid("browserbase", "basic", "tok").ShouldBeFalse();

    [Fact]
    public void Rejects_an_empty_secret() => IsValid("browserbase", "apiKey", "").ShouldBeFalse();

    [Fact]
    public void Rejects_a_connectUrl_secret_that_is_not_wss_or_https() =>
        IsValid("browserbase", "connectUrl", "bb_live_apikey").ShouldBeFalse();

    [Fact]
    public void Rejects_a_connectUrl_registration_with_an_empty_secret() =>
        IsValid("browserbase", "connectUrl", "").ShouldBeFalse();

    [Fact]
    public void Rejects_options_with_an_empty_value() =>
        IsValid("browserless", "apiKey", "tok", new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = "" }).ShouldBeFalse();
}
