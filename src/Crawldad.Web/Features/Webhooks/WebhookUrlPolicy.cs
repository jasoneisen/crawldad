using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace Crawldad.Web.Features.Webhooks;

/// <summary>The registration-time SSRF guard for a webhook target URL. Crawldad dials <b>out</b> to a tenant-supplied
/// address, so the policy is deliberately restrictive: <c>https</c> only (deliveries are signed but the transport must
/// still be encrypted), and no IP that would let a delivery reach the platform's own network (loopback, link-local,
/// RFC 1918 / CGNAT / unique-local, unspecified, multicast). An IP-literal host is classified directly; a DNS host is
/// permitted here (its literal is checked, not resolved) except the reserved <c>localhost</c> name — a name's actual
/// resolution is re-checked at send time by <see cref="WebhookConnectGuard"/>, which resolves, re-classifies against
/// this same denylist (<see cref="IsBlockedAddress"/>), and pins the connection, closing the DNS-rebinding gap. This
/// registration check remains the fast, first-line rejection of a non-https URL or an obviously-internal literal.</summary>
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

    // IPv6 prefixes that *embed* an IPv4 destination a translating gateway will reach: NAT64 (RFC 6052, the low 32 bits),
    // 6to4 (RFC 3056, the 32 bits after the 2002 prefix), and the deprecated IPv4-compatible form (the low 32 bits). A
    // DNS64 resolver can synthesise e.g. 64:ff9b::169.254.169.254; the embedded v4 must be judged against the v4 denylist
    // or the NAT64 gateway reaches the metadata service. (The IPv4-*mapped* ::ffff:0:0/96 form is handled separately via
    // IsIPv4MappedToIPv6.)
    private static readonly IPNetwork _nat64 = IPNetwork.Parse("64:ff9b::/96");
    private static readonly IPNetwork _sixToFour = IPNetwork.Parse("2002::/16");
    private static readonly IPNetwork _v4Compatible = IPNetwork.Parse("::/96");

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
            if (IsBlockedAddress(IPAddress.Parse(uri.Host.Trim('[', ']'))))
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

    /// <summary>Whether <paramref name="address"/> falls in a range a delivery must never reach — the reserved-range
    /// denylist shared by this registration check and the send-time <see cref="WebhookConnectGuard"/>. An IPv4-mapped
    /// IPv6 address is unwrapped first, so <c>::ffff:10.0.0.1</c> is judged as the <c>10.0.0.1</c> it really reaches; a
    /// NAT64 / 6to4 / IPv4-compatible address has its embedded IPv4 judged against the v4 denylist too, so a synthesised
    /// <c>64:ff9b::169.254.169.254</c> cannot smuggle a delivery to an internal host through a translating gateway.</summary>
    internal static bool IsBlockedAddress(IPAddress address)
    {
        var ip = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        if (ip.AddressFamily == AddressFamily.InterNetworkV6
            && TryExtractEmbeddedV4(ip, out var embedded)
            && IsBlockedV4(embedded))
        {
            return true;
        }

        return ip.AddressFamily == AddressFamily.InterNetwork ? IsBlockedV4(ip) : IsBlockedV6(ip);
    }

    private static bool IsBlockedV4(IPAddress ipv4) => Array.Exists(_blockedV4, network => network.Contains(ipv4));

    private static bool IsBlockedV6(IPAddress ipv6) => Array.Exists(_blockedV6, network => network.Contains(ipv6));

    // The IPv4 embedded in a NAT64 / 6to4 / IPv4-compatible IPv6 address, or null when the address embeds none. NAT64 and
    // the IPv4-compatible form carry it in the low 32 bits; 6to4 carries it in the 32 bits after the 2002 prefix.
    private static bool TryExtractEmbeddedV4(IPAddress ipv6, [NotNullWhen(true)] out IPAddress? embedded)
    {
        var bytes = ipv6.GetAddressBytes();
        if (_nat64.Contains(ipv6) || _v4Compatible.Contains(ipv6))
        {
            embedded = new IPAddress(bytes[12..16]);
            return true;
        }

        if (_sixToFour.Contains(ipv6))
        {
            embedded = new IPAddress(bytes[2..6]);
            return true;
        }

        embedded = null;
        return false;
    }
}
