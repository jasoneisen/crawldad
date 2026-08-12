using System.Security.Cryptography;
using System.Text;

namespace Crawldad.Web.Features.Webhooks;

/// <summary>Computes the <c>X-Crawldad-Signature</c> value for a webhook delivery: an HMAC-SHA256 over the exact request
/// body, bound to a per-delivery timestamp so a receiver can also reject replays. The signed message is
/// <c>"{timestamp}.{body}"</c>; the header value is <c>sha256=&lt;lowercase-hex&gt;</c>. The receiver recomputes the same
/// HMAC with its copy of the endpoint secret and compares in constant time. The signature is derived, not secret — it is
/// safe to send and log; the secret never appears in a header, body, or log.</summary>
internal static class WebhookSignature
{
    /// <summary>The signature algorithm prefix on the header value.</summary>
    public const string Prefix = "sha256=";

    /// <summary>Signs <paramref name="body"/> under <paramref name="secret"/>, bound to <paramref name="timestamp"/>
    /// (Unix seconds). Returns the full header value (<c>sha256=&lt;hex&gt;</c>).</summary>
    public static string Compute(string secret, long timestamp, string body)
    {
        var signed = Encoding.UTF8.GetBytes($"{timestamp}.{body}");
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed);
        return Prefix + Convert.ToHexStringLower(mac);
    }
}
