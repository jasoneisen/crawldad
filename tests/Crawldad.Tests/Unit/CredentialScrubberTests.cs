using System.Text.Json;
using Crawldad.Tests.Support;
using Crawldad.Web.Infrastructure.Security;

namespace Crawldad.Tests.Unit;

/// <summary>Negative-space tests for the <see cref="CredentialScrubber"/> primitive: the known-param rule across
/// name/case/encoding variants and wss URLs (Browserless, Browserbase, and ngrok/cloudflared tunnel shapes), the exact
/// live-secret rule, the no-false-positive guarantee on ordinary text (so the acceptance goldens are untouched), and idempotence.</summary>
public class CredentialScrubberTests
{
    private const string _redacted = CredentialScrubber.Redaction;

    private static CredentialScrubber Scrubber(params string[] liveSecrets) => new(new StubSecretScope(liveSecrets));

    // ----- known credential params -------------------------------------------

    [Theory]
    [InlineData("token")]
    [InlineData("apiKey")]
    [InlineData("signingKey")]
    public void Redacts_each_known_param_value(string param)
    {
        var scrubbed = Scrubber().Scrub($"prefix {param}=bb_live_SECRETvalue123 suffix");

        scrubbed.ShouldBe($"prefix {param}={_redacted} suffix");
    }

    [Fact]
    public void Redacts_a_token_in_a_wss_connect_url_but_keeps_the_rest()
    {
        // The Browserless connect shape: the account token is a `token=` query param on
        // wss://production-<region>.browserless.io/chromium/playwright.
        var scrubbed = Scrubber().Scrub(
            "connecting wss://production-lon.browserless.io/chromium/playwright?token=tok_SECRET_123&blockAds=true");

        // scheme/host/path and the non-secret blockAds param survive; only the token value is gone.
        scrubbed.ShouldBe(
            $"connecting wss://production-lon.browserless.io/chromium/playwright?token={_redacted}&blockAds=true");
        scrubbed.ShouldNotContain("tok_SECRET_123");
    }

    [Fact]
    public void Redacts_the_live_signingKey_in_a_browserbase_connect_url_and_keeps_the_host()
    {
        // The Browserbase connect shape: a region-encoded host with a SINGLE per-session `signingKey` JWT (base64url,
        // '.'-separated) — the URL no longer embeds the account apiKey. The JWT is synthetic (same eyJhbGci prefix +
        // header.payload.signature structure), NOT a real key.
        const string Jwt =
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzZXNzaW9uSWQiOiJmYWtlIiwiZXhwIjo5OTk5OTk5OTk5fQ.s1gn4tur3_FAKE_forTESTINGonly_notARealKey";
        var scrubbed = Scrubber().Scrub($"wss://connect.usw2.browserbase.com/?signingKey={Jwt}");

        // The region host and path survive; the whole JWT (through both '.' separators, to end of value) is gone.
        scrubbed.ShouldBe($"wss://connect.usw2.browserbase.com/?signingKey={_redacted}");
        scrubbed.ShouldNotContain(Jwt);
        scrubbed.ShouldNotContain("s1gn4tur3");
    }

    [Fact]
    public void Redacts_an_apiKey_bearing_browserbase_connect_url_and_keeps_the_session_id()
    {
        // An apiKey-bearing connectUrl (connectUrl mode, or a user-constructed shape) is still redacted; sessionId is
        // ephemeral, not a secret. The live default shape now uses signingKey (see the test above).
        var scrubbed = Scrubber().Scrub("wss://connect.browserbase.com?apiKey=bb_live_ACCOUNTKEY&sessionId=ses_abc123");

        scrubbed.ShouldBe($"wss://connect.browserbase.com?apiKey={_redacted}&sessionId=ses_abc123");
        scrubbed.ShouldNotContain("bb_live_ACCOUNTKEY");
    }

    [Theory]
    [InlineData("?APIKEY=SECRET", "?APIKEY=")]        // upper-case name
    [InlineData("&Token=SECRET", "&Token=")]          // mixed-case name
    [InlineData("token=bb%2Flive%2FSECRET", "token=")] // url-encoded value
    public void Param_matching_is_case_insensitive_and_encoding_agnostic(string input, string keptPrefix)
    {
        var scrubbed = Scrubber().Scrub(input);

        scrubbed.ShouldBe(keptPrefix + _redacted);
        scrubbed.ShouldNotContain("SECRET");
    }

    [Fact]
    public void Redacts_a_param_embedded_in_json_text()
    {
        var scrubbed = Scrubber().Scrub("""{"connectUrl":"wss://h/x?apiKey=bb_live_INJSON"}""");

        scrubbed.ShouldContain($"apiKey={_redacted}");
        scrubbed.ShouldNotContain("bb_live_INJSON");
    }

    // ----- tunnel connect URLs (ngrok / cloudflared) -------------------------

    [Theory]
    [InlineData("wss://d34db33f.ngrok-free.app")]              // ngrok free tunnel
    [InlineData("wss://random-forest-1234.trycloudflare.com")] // cloudflared quick tunnel
    public void Redacts_a_token_query_on_a_tunnel_host_even_when_unregistered(string host)
    {
        // Defence-in-depth: a tunnel CDP URL carrying ?token=… is redacted by the known-param rule with NO run secret
        // registered — scheme/host/path survive, only the token value is gone. Synthetic host + token.
        var scrubbed = Scrubber().Scrub($"{host}/devtools/browser/f4ke-id?token=tok_FAKE_tunnel_forTESTINGonly");

        scrubbed.ShouldBe($"{host}/devtools/browser/f4ke-id?token={_redacted}");
        scrubbed.ShouldNotContain("tok_FAKE_tunnel_forTESTINGonly");
    }

    [Theory]
    [InlineData("wss://d34db33f.ngrok-free.app/devtools/browser/f4ke-brows3r-id-forTESTINGonly")]
    [InlineData("wss://random-forest-1234.trycloudflare.com/devtools/browser/f4ke-brows3r-id-forTESTINGonly")]
    public void Redacts_a_whole_tunnel_connect_url_registered_as_a_run_secret(string endpoint)
    {
        // connectUrl mode registers the WHOLE tunnel URL as a run secret; the /devtools/browser/<id> path is itself a
        // bearer token, so the entire URL (host + path, no recognised query param) vanishes wherever it surfaces.
        var scrubber = Scrubber(endpoint);

        scrubber.Scrub($"connecting to {endpoint} now").ShouldBe($"connecting to {_redacted} now");
        // The same URL echoed inside a JSON body and a provider-exception line is redacted just as thoroughly.
        scrubber.Scrub($$"""{"connectUrl":"{{endpoint}}"}""").ShouldBe($$"""{"connectUrl":"{{_redacted}}"}""");
        scrubber.Scrub($"PlaywrightException: connect ECONNREFUSED {endpoint}")
            .ShouldBe($"PlaywrightException: connect ECONNREFUSED {_redacted}");
    }

    // ----- no false positives (goldens must be untouched) --------------------

    [Theory]
    [InlineData("the token was rotated at midnight")]  // the word "token" without =value
    [InlineData("apiKeys are configured per tenant")]   // "apiKey" as a plain word
    [InlineData("token=")]                               // a bare param with no value
    [InlineData("https://aca-prod.accela.com/LJCMG/Cap/CapDetail.aspx?Module=Enforcement&capID=24ENF-00001")]
    [InlineData("open a tunnel: ngrok http 9222 → d34db33f.ngrok-free.app")]  // bare tunnel host, no param, not registered
    [InlineData("cloudflared prints https://random-forest-1234.trycloudflare.com")] // ditto — no over-redaction
    public void Leaves_ordinary_text_unchanged(string text)
    {
        var scrubbed = Scrubber().Scrub(text);

        scrubbed.ShouldBe(text);
        scrubbed.ShouldNotContain(_redacted);
    }

    // ----- exact live-secret rule --------------------------------------------

    [Fact]
    public void Redacts_a_live_run_secret_wherever_it_appears()
    {
        const string Secret = "bb_live_LEAKCANARY_exactmatch_0123456789";

        var scrubbed = Scrubber(Secret).Scrub($"the scraped page echoed {Secret} in its body");

        scrubbed.ShouldBe($"the scraped page echoed {_redacted} in its body");
    }

    [Fact]
    public void Does_not_exact_scrub_a_secret_below_the_length_floor()
    {
        // A pathologically short "secret" must not mangle every occurrence of a common substring.
        var shortSecret = new string('a', CredentialScrubber.MinExactScrubLength - 1);

        var scrubbed = Scrubber(shortSecret).Scrub($"value {shortSecret} here");

        scrubbed.ShouldBe($"value {shortSecret} here");
    }

    [Fact]
    public void Redacts_a_short_form_fill_secret_down_to_its_lower_floor()
    {
        // A form-fill secret (a user-chosen PIN/short password) is redacted at the lower form floor — even at 4 chars,
        // well below the connect floor a short PIN would otherwise slip under and leak. Below the form floor (1-3 chars)
        // it stays inert (the over-redaction guard).
        var atFloor = new string('7', CredentialScrubber.MinFormSecretScrubLength);        // 4 chars → redacted
        var belowFloor = new string('3', CredentialScrubber.MinFormSecretScrubLength - 1); // 3 chars → inert
        var scrubber = new CredentialScrubber(new StubSecretScope() { FormSecrets = [atFloor, belowFloor] });

        var scrubbed = scrubber.Scrub($"pin {atFloor} and {belowFloor} here");

        scrubbed.ShouldBe($"pin {_redacted} and {belowFloor} here");
    }

    [Fact]
    public void Exact_secret_in_a_param_position_is_redacted_once_not_doubly()
    {
        const string Secret = "tok_LEAKCANARY_longenough_123456";

        var scrubbed = Scrubber(Secret).Scrub($"token={Secret}");

        scrubbed.ShouldBe($"token={_redacted}");
    }

    // ----- always-on secrets (the configured tenant API keys) ----------------

    [Fact]
    public void Redacts_a_configured_always_on_secret_wherever_it_appears()
    {
        const string ApiKey = "tenant-api-key-0123456789abcdef";
        var scrubber = new CredentialScrubber(new StubSecretScope(), [ApiKey, "short"]);

        // The always-on set redacts the key even in free-form text no param rule would catch (a stray Authorization
        // log); a below-floor entry ("short") stays inert.
        var scrubbed = scrubber.Scrub($"Authorization: Bearer {ApiKey} then short");

        scrubbed.ShouldBe($"Authorization: Bearer {_redacted} then short");
        scrubbed.ShouldNotContain(ApiKey);
    }

    // ----- idempotence -------------------------------------------------------

    [Theory]
    [InlineData("wss://h/x?apiKey=bb_live_abc&sessionId=ses_1")]
    [InlineData("wss://connect.usw2.browserbase.com/?signingKey=eyJhbGci.eyJz.s1g")] // live Browserbase shape
    [InlineData("wss://d34db33f.ngrok-free.app/devtools/browser/f4ke-id?token=tok_FAKE")] // tunnel shape
    [InlineData("token=tok_abc123&next=1")]
    public void Param_scrub_is_idempotent(string input)
    {
        var scrubber = Scrubber();
        var once = scrubber.Scrub(input);
        var twice = scrubber.Scrub(once);

        twice.ShouldBe(once);
    }

    [Fact]
    public void Exact_scrub_is_idempotent()
    {
        const string Secret = "bb_live_LEAKCANARY_idempotent_0123456789";
        var scrubber = Scrubber(Secret);

        var once = scrubber.Scrub($"echo {Secret} and token={Secret}");
        var twice = scrubber.Scrub(once);

        twice.ShouldBe(once);
        twice.ShouldNotContain(Secret);
    }

    // ----- JSON result scrubbing ---------------------------------------------

    [Fact]
    public void ScrubJson_returns_null_for_a_null_element() =>
        Scrubber().ScrubJson(null).ShouldBeNull();

    [Fact]
    public void ScrubJson_is_a_no_op_on_credential_free_json()
    {
        using var doc = JsonDocument.Parse("""{"newLinks":["/a","/b"],"crawledToEnd":true}""");
        var raw = doc.RootElement.GetRawText();

        var scrubbed = Scrubber().ScrubJson(doc.RootElement);

        scrubbed.ShouldNotBeNull();
        scrubbed.Value.GetRawText().ShouldBe(raw); // byte-identical → goldens never change
    }

    [Fact]
    public void ScrubJson_redacts_a_live_secret_echoed_into_a_result()
    {
        const string Secret = "bb_live_LEAKCANARY_inresult_0123456789";
        using var doc = JsonDocument.Parse($$"""{"scraped":"{{Secret}}"}""");

        var scrubbed = Scrubber(Secret).ScrubJson(doc.RootElement);

        scrubbed.ShouldNotBeNull();
        scrubbed.Value.GetProperty("scraped").GetString().ShouldBe(_redacted);
    }
}
