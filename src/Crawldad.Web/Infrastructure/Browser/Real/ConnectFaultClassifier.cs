using System.Net;
using System.Net.Http;
using Crawldad.Web.Infrastructure.Security;
using Microsoft.Playwright;

namespace Crawldad.Web.Infrastructure.Browser.Real;

/// <summary>Classifies a raw backend-connect fault — the exception a real adapter caught at its scrubbing boundary,
/// before it is flattened into a secret-free <see cref="BrowserConnectException"/> — as <b>transient</b> (a
/// network/transport/5xx blip a bounded <c>config.connectRetry</c> should re-attempt) or <b>auth-shaped/permanent</b>
/// (a rejected key, a 4xx, or an absent credential — fail fast). The fault is only inspected here, never propagated, so
/// a message that embeds a connect URL/token still never leaves the boundary. The default for an unrecognised fault is
/// non-retryable: a retry only ever earns its keep against a known-transient shape, never a bug.</summary>
internal static class ConnectFaultClassifier
{
    /// <summary>Whether <paramref name="fault"/> is a transient connect blip worth a bounded retry.</summary>
    /// <param name="fault">The raw provider/resolution fault caught at the adapter's connect boundary.</param>
    public static bool IsTransient(Exception fault) => fault switch
    {
        // The credentialRef resolved to nothing (deleted / never-registered): a retry would re-read the SAME absence —
        // this is the acceptance's "run against a deleted credential fails fast with no retry".
        SecretNotFoundException => false,

        // The hosted session API answered, or the socket faulted before it could: a 4xx is a client/auth error (a
        // rejected key, a bad request) a retry cannot fix; a 5xx is a transient server fault; a null status means the
        // request never got an HTTP response at all (connection refused/reset, DNS, TLS) — a transient network fault.
        HttpRequestException http => IsTransientStatus(http.StatusCode),

        // A WS/CDP transport fault (the tunnel-churn case): transient by nature — connection refused/reset, DNS, a WS
        // handshake failure, a 5xx gateway — UNLESS the handshake got an explicit 4xx, the WS-path analogue of a
        // session-API 4xx (a rejected token embedded in the connect URL).
        PlaywrightException pw => !IsAuthHandshake(pw.Message),

        _ => false,
    };

    // A session-API HttpRequestException status: 5xx (or a null status — no HTTP response, i.e. a raw socket/DNS/TLS
    // fault) is transient; a 4xx is the auth/client class that fails fast. Mirrors the WS-handshake split below.
    private static bool IsTransientStatus(HttpStatusCode? status) =>
        status is null || (int)status >= 500;

    // A Playwright connect fault whose WS handshake received an explicit HTTP 4xx — the `ws` client surfaces this as
    // "Unexpected server response: <code>", so a 401 (rejected token) or 403 (host-check/allowlist) there is an
    // auth-shaped rejection that fails fast, while 5xx/refused/reset/timeout stay transient. A best-effort heuristic
    // layered on the authoritative HttpRequestException path: the message is only read here, never surfaced.
    private static bool IsAuthHandshake(string message) =>
        message.Contains("Unexpected server response: 401", StringComparison.Ordinal)
        || message.Contains("Unexpected server response: 403", StringComparison.Ordinal);
}
