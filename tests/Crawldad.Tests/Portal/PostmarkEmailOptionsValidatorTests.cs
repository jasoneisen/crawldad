using Crawldad.Portal.Auth;
using Microsoft.Extensions.Options;

namespace Crawldad.Tests.Portal;

/// <summary>The portal's boot-time email guard (<see cref="PostmarkEmailOptionsValidator"/>): neither secret set (the
/// dev/test default) passes as "unconfigured", both set with a valid from-address passes as "configured", and every
/// half-configured or malformed shape fails startup with a specific message — so a partially-configured portal host can
/// never silently fall back to fail-closed while looking configured.</summary>
public class PostmarkEmailOptionsValidatorTests
{
    private const string _token = "pm-test-token";
    private const string _from = "noreply@crawldad.dev";

    private static readonly PostmarkEmailOptionsValidator _validator = new();

    private static ValidateOptionsResult Validate(PostmarkEmailOptions options) => _validator.Validate(name: null, options);

    [Fact]
    public void Neither_secret_set_is_valid_the_unconfigured_default() =>
        Validate(new PostmarkEmailOptions()).Succeeded.ShouldBeTrue();

    [Fact]
    public void Both_secrets_set_with_a_valid_from_address_is_valid() =>
        Validate(new PostmarkEmailOptions { ServerToken = _token, FromAddress = _from }).Succeeded.ShouldBeTrue();

    [Fact]
    public void Only_the_token_set_fails_both_or_neither()
    {
        var result = Validate(new PostmarkEmailOptions { ServerToken = _token });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("BOTH ServerToken and FromAddress");
    }

    [Fact]
    public void Only_the_from_address_set_fails_both_or_neither()
    {
        var result = Validate(new PostmarkEmailOptions { FromAddress = _from });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("BOTH ServerToken and FromAddress");
    }

    [Fact]
    public void A_configured_provider_with_an_unparseable_from_address_fails()
    {
        var result = Validate(new PostmarkEmailOptions { ServerToken = _token, FromAddress = "not-an-email" });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("FromAddress must be a valid email address");
    }

    [Fact]
    public void A_configured_provider_with_a_blank_message_stream_fails()
    {
        var result = Validate(new PostmarkEmailOptions { ServerToken = _token, FromAddress = _from, MessageStream = "   " });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("MessageStream must not be blank");
    }

    [Fact]
    public void Null_options_is_rejected() =>
        Should.Throw<ArgumentNullException>(() => Validate(null!));
}
