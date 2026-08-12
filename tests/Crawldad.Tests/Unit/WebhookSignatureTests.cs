using System.Security.Cryptography;
using System.Text;
using Crawldad.Web.Features.Webhooks;

namespace Crawldad.Tests.Unit;

/// <summary>The HMAC signature scheme a receiver verifies: <c>sha256=&lt;hex&gt;</c> over <c>"{timestamp}.{body}"</c> under
/// the endpoint secret. Proves the value matches the documented receiver recipe, is deterministic, and changes when any
/// input changes (so a tampered body/timestamp/secret fails verification).</summary>
public class WebhookSignatureTests
{
    private const string _secret = "whsec_0123456789abcdef";
    private const long _timestamp = 1_700_000_000;
    private const string _body = "{\"id\":\"e1\",\"type\":\"run.succeeded\"}";

    [Fact]
    public void Matches_the_documented_receiver_recipe()
    {
        var expected = "sha256=" + Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(_secret), Encoding.UTF8.GetBytes($"{_timestamp}.{_body}")));

        WebhookSignature.Compute(_secret, _timestamp, _body).ShouldBe(expected);
    }

    [Fact]
    public void Is_prefixed_and_deterministic()
    {
        var signature = WebhookSignature.Compute(_secret, _timestamp, _body);

        signature.ShouldStartWith("sha256=");
        signature.Length.ShouldBe("sha256=".Length + 64); // 32-byte HMAC as lowercase hex
        signature.ShouldBe(WebhookSignature.Compute(_secret, _timestamp, _body));
    }

    [Fact]
    public void Changes_when_any_input_changes()
    {
        var baseline = WebhookSignature.Compute(_secret, _timestamp, _body);

        WebhookSignature.Compute("other-secret-value", _timestamp, _body).ShouldNotBe(baseline);
        WebhookSignature.Compute(_secret, _timestamp + 1, _body).ShouldNotBe(baseline);
        WebhookSignature.Compute(_secret, _timestamp, _body + " ").ShouldNotBe(baseline);
    }
}
