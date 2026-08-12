using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace Crawldad.Web.Features.Webhooks;

/// <summary>The SSRF guard for a webhook target URL, enforced at registration. Crawldad dials <b>out</b> to a
/// tenant-supplied address, so the policy is deliberately restrictive: <c>https</c> only (deliveries are signed but the
/// transport must still be encrypted), and no IP that would let a delivery reach the platform's own network
/// (loopback, link-local, RFC 1918 / CGNAT / unique-local, unspecified, multicast). An IP-literal host is classified
/// directly; a DNS host is permitted at registration (its literal is checked, not resolved) except the reserved
/// <c>localhost</c> name. DNS-rebinding defence — resolving and re-checking at send time — is a documented follow-up;
/// the shipped stance is https + literal-address classification.</summary>
internal static class WebhookUrlPolicy
{
    // The IPv4 ranges a delivery must never reach: "this network", private, CGNAT, loopback, link-local, and
    // multicast/reserved. Membership is a single range test, so the policy has no per-range branch to cover.
    private static readonly IPNetwork[] _blockedV4 =
    [
        IPNetwork.Parse("0.0.0.0/8"),        // unspecified / "this network"
        IPNetwork.Parse("10.0.0.0/8"),       // RFC 1918 private
        IPNetwork.Parse("100.64.0.0/10"),    // CGNAT
        IPNetwork.Parse("127.0.0.0/8"),      // loopback
        IPNetwork.Parse("169.254.0.0/16"),   // link-local
        IPNetwork.Parse("172.16.0.0/12"),    // RFC 1918 private
        IPNetwork.Parse("192.168.0.0/16"),   // RFC 1918 private
        IPNetwork.Parse("224.0.0.0/4"),      // multicast
        IPNetwork.Parse("240.0.0.0/4"),      // reserved / broadcast
    ];

    // The IPv6 ranges a delivery must never reach: unspecified, loopback, link-local, unique-local, and multicast.
    private static readonly IPNetwork[] _blockedV6 =
    [
        IPNetwork.Parse("::/128"),   // unspecified
        IPNetwork.Parse("::1/128"),  // loopback
        IPNetwork.Parse("fe80::/10"), // link-local
        IPNetwork.Parse("fc00::/7"),  // unique-local
        IPNetwork.Parse("ff00::/8"),  // multicast
    ];

    /// <summary>Whether <paramref name="url"/> is an acceptable delivery target. On rejection, <paramref name="error"/>
    /// carries a caller-safe reason (no host resolution, no internal detail).</summary>
    public static bool IsAllowed(string url, [NotNullWhen(false)] out string? error)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            error = "url must be an absolute URL";
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            error = "url must use https";
            return false;
        }

        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            if (IsBlocked(IPAddress.Parse(uri.Host.Trim('[', ']'))))
            {
                error = "url must not target a loopback, link-local, or private (RFC 1918 / unique-local) address";
                return false;
            }

            error = null;
            return true;
        }

        if (IsLocalhostName(uri.Host))
        {
            error = "url must not target localhost";
            return false;
        }

        error = null;
        return true;
    }

    // A DNS host that is the reserved loopback name (or a subdomain of it), which no public receiver uses.
    private static bool IsLocalhostName(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    // Whether an IP literal is in a blocked range — unwrapping an IPv4-mapped IPv6 address first so "::ffff:10.0.0.1"
    // is judged as the 10.0.0.1 it really reaches.
    private static bool IsBlocked(IPAddress address)
    {
        var ip = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        var networks = ip.AddressFamily == AddressFamily.InterNetwork ? _blockedV4 : _blockedV6;
        return Array.Exists(networks, network => network.Contains(ip));
    }
}
