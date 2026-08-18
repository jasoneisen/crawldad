using System.Net;
using System.Net.Http;
using Crawldad.Web.Infrastructure.Browser.Real;
using Crawldad.Web.Infrastructure.Security;
using Microsoft.Playwright;

namespace Crawldad.Tests.Unit;

/// <summary>The connect-fault taxonomy that drives <c>config.connectRetry</c>: which raw provider/resolution faults are
/// transient (retryable — a tunnel/network/5xx/throttle blip) and which are auth-shaped/permanent (fail fast — a
/// rejected key, a client-error 4xx other than the throttle codes 429/408, an absent credential). The classifier only
/// inspects the fault; the adapter still flattens it to a secret-free <c>BrowserConnectException</c>.</summary>
public class ConnectFaultClassifierTests
{
    [Fact]
    public void An_absent_credential_is_not_transient()
    {
        // The credentialRef resolved to nothing (deleted / never-registered): a retry re-reads the same absence.
        ConnectFaultClassifier.IsTransient(new SecretNotFoundException("cred-ref")).ShouldBeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]     // 400
    [InlineData(HttpStatusCode.Unauthorized)]   // 401 — rejected key
    [InlineData(HttpStatusCode.Forbidden)]      // 403 — host-check / allowlist
    [InlineData(HttpStatusCode.NotFound)]       // 404
    [InlineData(HttpStatusCode.Conflict)]       // 409 — another client-error 4xx a retry can't fix
    public void A_client_error_4xx_from_the_session_api_is_not_transient(HttpStatusCode status)
    {
        ConnectFaultClassifier.IsTransient(new HttpRequestException("rejected", null, status)).ShouldBeFalse();
    }

    [Theory]
    [InlineData((HttpStatusCode)429)]              // Too Many Requests — rate limiting (canonically transient throttle)
    [InlineData(HttpStatusCode.RequestTimeout)]    // 408 — request timeout (a transient blip, not auth-shaped)
    public void A_throttle_4xx_from_the_session_api_is_transient(HttpStatusCode status)
    {
        // Issue #85: 429/408 from a hosted session API are transient throttling, retried with the same bounded budget as
        // a 5xx — symmetric with the WS-handshake path, which already fails fast only on the auth-shaped 401/403.
        ConnectFaultClassifier.IsTransient(new HttpRequestException("throttled", null, status)).ShouldBeTrue();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)] // 500
    [InlineData(HttpStatusCode.BadGateway)]          // 502
    [InlineData(HttpStatusCode.ServiceUnavailable)]  // 503
    public void A_5xx_from_the_session_api_is_transient(HttpStatusCode status)
    {
        ConnectFaultClassifier.IsTransient(new HttpRequestException("server error", null, status)).ShouldBeTrue();
    }

    [Fact]
    public void A_network_fault_with_no_http_status_is_transient()
    {
        // No HTTP response at all (connection refused/reset, DNS, TLS) — StatusCode is null.
        ConnectFaultClassifier.IsTransient(new HttpRequestException("connection refused")).ShouldBeTrue();
    }

    [Fact]
    public void A_ws_or_cdp_transport_fault_is_transient()
    {
        // The tunnel-churn case: Playwright's chromium.connect / connectOverCDP failed to reach the endpoint.
        ConnectFaultClassifier.IsTransient(new PlaywrightException("browserType.connect: connect ECONNREFUSED 127.0.0.1:1")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("browserType.connect: WebSocket error: Unexpected server response: 401")] // rejected token at the handshake
    [InlineData("browserType.connect: WebSocket error: Unexpected server response: 403")] // host-check rejection at the handshake
    public void A_4xx_ws_handshake_rejection_is_not_transient(string message)
    {
        ConnectFaultClassifier.IsTransient(new PlaywrightException(message)).ShouldBeFalse();
    }

    [Fact]
    public void A_5xx_ws_handshake_response_is_transient()
    {
        // A gateway 5xx surfaced at the WS handshake is a transient blip, not an auth rejection.
        ConnectFaultClassifier.IsTransient(new PlaywrightException("browserType.connect: WebSocket error: Unexpected server response: 502")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("browserType.connect: WebSocket error: Unexpected server response: 429")] // rate limit at the handshake
    [InlineData("browserType.connect: WebSocket error: Unexpected server response: 408")] // request timeout at the handshake
    public void A_throttle_4xx_ws_handshake_response_is_transient(string message)
    {
        // The WS side of the issue-#85 symmetry: a 429/408 at the handshake is transient throttling, retried — the WS
        // path fails fast only on the auth-shaped 401/403, exactly as the HTTP session-API path now does.
        ConnectFaultClassifier.IsTransient(new PlaywrightException(message)).ShouldBeTrue();
    }

    [Fact]
    public void An_unrecognised_fault_is_not_transient()
    {
        // Fail-fast default: a retry only earns its keep against a known-transient shape, never a bug.
        ConnectFaultClassifier.IsTransient(new InvalidOperationException("boom")).ShouldBeFalse();
    }
}
