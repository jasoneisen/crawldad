using Crawldad.Web.Features.Browsers;

namespace Crawldad.Tests.Unit;

/// <summary>The registration validation vocabulary: the name slug rule, the known adapter/mode sets, and the
/// connectUrl shape check — the guards shared by the request validator and the endpoint's route-name check.</summary>
public class BrowserRegistrationRulesTests
{
    [Theory]
    [InlineData("prod")]
    [InlineData("a")]
    [InlineData("my-browser")]
    [InlineData("browser-1")]
    [InlineData("ab")]
    public void Accepts_a_valid_name_slug(string name) => BrowserRegistrationRules.IsValidName(name).ShouldBeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Prod")]        // uppercase
    [InlineData("my_browser")]  // underscore
    [InlineData("-prod")]       // leading hyphen
    [InlineData("prod-")]       // trailing hyphen
    [InlineData("my browser")]  // space
    [InlineData("my:browser")]  // colon (would break the Secrets:{tenant}:{ref} fallback key)
    public void Rejects_an_invalid_name_slug(string? name) => BrowserRegistrationRules.IsValidName(name).ShouldBeFalse();

    [Fact]
    public void Accepts_a_64_char_name_but_rejects_65()
    {
        BrowserRegistrationRules.IsValidName(new string('a', 64)).ShouldBeTrue();
        BrowserRegistrationRules.IsValidName(new string('a', 65)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("browserbase", true)]
    [InlineData("browserless", true)]
    [InlineData("local", false)]   // no credential — not registerable
    [InlineData("fake", false)]
    [InlineData("unknown", false)]
    public void Knows_the_registerable_adapters(string adapter, bool known) =>
        BrowserRegistrationRules.IsKnownAdapter(adapter).ShouldBe(known);

    [Theory]
    [InlineData("connectUrl", true)]
    [InlineData("apiKey", true)]
    [InlineData("basic", false)]
    public void Knows_the_credential_modes(string mode, bool known) =>
        BrowserRegistrationRules.IsKnownMode(mode).ShouldBe(known);

    [Theory]
    [InlineData("wss://connect.example.com/?signingKey=x", true)]
    [InlineData("WSS://connect.example.com", true)]         // scheme is case-insensitive
    [InlineData("https://tunnel.example.com", true)]
    [InlineData("ws://insecure.example.com", false)]        // ws:// is not accepted
    [InlineData("http://insecure.example.com", false)]
    [InlineData("bb_live_apikey_not_a_url", false)]
    public void Checks_the_connect_url_shape(string secret, bool ok) =>
        BrowserRegistrationRules.IsConnectUrlShape(secret).ShouldBe(ok);
}
