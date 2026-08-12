using Crawldad.Contracts.Webhooks;
using Crawldad.Web.Features.Webhooks;

namespace Crawldad.Tests.Unit;

/// <summary>Boundary validation for <c>PUT /webhooks/{name}</c>: the target passes the SSRF policy, the secret is present
/// and long enough, and any subscribed events are from the catalog.</summary>
public class RegisterWebhookRequestValidatorTests
{
    private readonly RegisterWebhookRequestValidator _validator = new();

    private static RegisterWebhookRequest Request(
        string url = "https://hooks.example.com/x",
        string secret = "whsec_0123456789abcdef",
        IReadOnlyList<string>? events = null) => new(url, secret, events);

    [Fact]
    public void Accepts_a_valid_request() => _validator.Validate(Request()).IsValid.ShouldBeTrue();

    [Fact]
    public void Accepts_all_known_events() =>
        _validator.Validate(Request(events: ["run.succeeded", "run.failed", "run.cancelled"])).IsValid.ShouldBeTrue();

    [Theory]
    [InlineData("")]                                // empty
    [InlineData("http://hooks.example.com/x")]      // not https
    [InlineData("https://10.0.0.1/x")]              // private
    public void Rejects_a_bad_url(string target) => _validator.Validate(Request(url: target)).IsValid.ShouldBeFalse();

    [Theory]
    [InlineData("")]          // empty
    [InlineData("tooshort")]  // below the 16-char floor
    public void Rejects_a_bad_secret(string secret) => _validator.Validate(Request(secret: secret)).IsValid.ShouldBeFalse();

    [Fact]
    public void Rejects_an_unknown_event() => _validator.Validate(Request(events: ["run.exploded"])).IsValid.ShouldBeFalse();
}
