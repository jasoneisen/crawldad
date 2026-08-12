using Crawldad.Contracts.Webhooks;

namespace Crawldad.Tests.Unit;

/// <summary>The register-request DTO redacts the signing secret from its string form, so an accidental log of the request
/// never carries credential material — while still showing the (non-secret) url and event selection.</summary>
public class RegisterWebhookRequestTests
{
    [Fact]
    public void ToString_redacts_the_secret_and_shows_events()
    {
        var text = new RegisterWebhookRequest("https://hooks.example.com/x", "whsec_super_secret_value", ["run.failed"]).ToString();

        text.ShouldNotContain("whsec_super_secret_value");
        text.ShouldContain("[redacted]");
        text.ShouldContain("https://hooks.example.com/x");
        text.ShouldContain("run.failed");
    }

    [Fact]
    public void ToString_shows_all_when_no_events_are_selected() =>
        new RegisterWebhookRequest("https://hooks.example.com/x", "secret_value_0123456789").ToString().ShouldContain("all");
}
