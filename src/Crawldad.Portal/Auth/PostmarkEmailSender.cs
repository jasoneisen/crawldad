using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crawldad.Portal.Auth;

/// <summary>The production <see cref="IEmailSender"/>: delivers a sign-in code through Postmark's HTTP API
/// (<c>POST https://api.postmarkapp.com/email</c>, the <c>X-Postmark-Server-Token</c> header, a JSON
/// From/To/Subject/TextBody/MessageStream body) over an <see cref="IHttpClientFactory"/> named client. It is wired by
/// <see cref="EmailModule"/> only when <see cref="PostmarkEmailOptions"/> is fully configured — in <b>any</b>
/// environment, so a real token can smoke-test it locally.
///
/// <para>It upholds the <see cref="IEmailSender"/> contract the OTP flow depends on: a non-2xx response or a transport
/// failure <b>throws</b> (<see cref="EmailDeliveryException"/>) — it never silently succeeds — so a fail-closed request
/// leaves no orphan challenge row (send happens before persist). It does <b>not</b> branch on the recipient, so it
/// behaves identically for known and unknown addresses (no account enumeration). No PII or secret is logged: a failure
/// records only the HTTP status and Postmark error code — never the recipient (matching the portal's convention of not
/// logging addresses at warning+), never the server token, and never the code.</para></summary>
internal sealed class PostmarkEmailSender(
    IHttpClientFactory httpClientFactory,
    IOptions<PostmarkEmailOptions> options,
    ILogger<PostmarkEmailSender> logger) : IEmailSender
{
    /// <summary>The named <see cref="HttpClient"/> the sender rides on (base address preset by <see cref="EmailModule"/>).</summary>
    internal const string HttpClientName = "Crawldad.Postmark";

    /// <summary>Postmark's email send endpoint base. The send uses the relative <c>email</c> path against it.</summary>
    internal static readonly Uri ApiBaseAddress = new("https://api.postmarkapp.com/");

    /// <summary>The Postmark server-token header. A constant, so a typo can't silently unauthenticate the call.</summary>
    private const string _serverTokenHeader = "X-Postmark-Server-Token";

    // General (NOT web/camelCase) defaults: Postmark's fields are PascalCase, exactly as declared on the DTOs below.
    // Case-insensitive read tolerates any casing drift in an error body. One cached instance ⇒ no per-call allocation.
    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public async Task SendOtpCodeAsync(string email, string code, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var payload = new PostmarkEmailRequest(
            From: o.FromAddress,
            To: email,
            Subject: "Your Crawldad sign-in code",
            TextBody: BuildTextBody(code),
            MessageStream: o.MessageStream);

        using var request = new HttpRequestMessage(HttpMethod.Post, "email");
        request.Headers.Add(_serverTokenHeader, o.ServerToken);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, _json), Encoding.UTF8, "application/json");

        var client = httpClientFactory.CreateClient(HttpClientName);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            // Transport failure (DNS, TLS, connection refused): no status to report. Log without PII, then fail closed.
            logger.LogWarning(ex, "Postmark sign-in email could not be sent: the provider was unreachable.");
            throw new EmailDeliveryException("Sign-in code delivery failed: the email provider was unreachable.", ex);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            // Non-2xx: fail closed. Log/report only non-sensitive metadata — the HTTP status and Postmark's own error
            // code (never the recipient, the token, or the response body verbatim).
            var errorCode = await TryReadErrorCodeAsync(response, cancellationToken);
            logger.LogWarning(
                "Postmark rejected the sign-in email (HTTP {StatusCode}, Postmark error code {ErrorCode}).",
                (int)response.StatusCode, errorCode);

            var status = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
            var errorText = errorCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
            throw new EmailDeliveryException(
                $"Sign-in code delivery failed: Postmark returned HTTP {status} (error code {errorText}).");
        }
    }

    /// <summary>The plain-text body carrying the code. Kept in one place so the copy is unit-testable; it never reaches
    /// a log.</summary>
    private static string BuildTextBody(string code) =>
        $"Your Crawldad sign-in code is {code}.\n\n" +
        "This code expires in 10 minutes and can be used once. " +
        "If you didn't request it, you can safely ignore this email.";

    /// <summary>Best-effort read of Postmark's numeric <c>ErrorCode</c> from a non-2xx body for diagnostics. A body that
    /// isn't the expected JSON (an edge/proxy error page, an empty body) yields <c>null</c> rather than masking the
    /// original failure — the caller still throws on the status alone.</summary>
    private static async Task<int?> TryReadErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<PostmarkErrorResponse>(body, _json)?.ErrorCode;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The Postmark send-email request body. PascalCase field names are Postmark's wire contract, matched by
    /// the default (non-web) serializer options above.</summary>
    private sealed record PostmarkEmailRequest(string From, string To, string Subject, string TextBody, string MessageStream);

    /// <summary>The fields of a Postmark error body we surface — its numeric <c>ErrorCode</c>. Deserialize-only.</summary>
    private sealed record PostmarkErrorResponse(int? ErrorCode);
}
