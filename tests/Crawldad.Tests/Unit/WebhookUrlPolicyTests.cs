using System.Net;
using Crawldad.Web.Features.Webhooks;

namespace Crawldad.Tests.Unit;

/// <summary>The SSRF guard: https-only, and no address that would let a delivery reach the platform's own network —
/// loopback, link-local (including the cloud metadata address), RFC 1918 / CGNAT / unique-local, unspecified, or
/// multicast, in both IPv4 and IPv6 (including an IPv4-mapped IPv6 literal). A DNS host is allowed except localhost.</summary>
public class WebhookUrlPolicyTests
{
    [Theory]
    // Allowed: https to a public DNS host or public IP.
    [InlineData("https://hooks.example.com/webhook", true)]
    [InlineData("https://ci.example.org:8443/hook", true)]
    [InlineData("https://93.184.216.34/hook", true)]                        // public IPv4
    [InlineData("https://[2606:2800:220:1:248:1893:25c8:1946]/hook", true)] // public IPv6
    [InlineData("https://169.1.1.1/hook", true)]                            // 169.x but NOT 169.254 link-local
    // Rejected: scheme.
    [InlineData("http://hooks.example.com/hook", false)]
    [InlineData("ftp://hooks.example.com/hook", false)]
    // Rejected: not an absolute URL.
    [InlineData("not-a-url", false)]
    [InlineData("/relative/only", false)]
    // Rejected: localhost by name.
    [InlineData("https://localhost/hook", false)]
    [InlineData("https://svc.localhost/hook", false)]
    // Rejected: blocked IPv4 ranges.
    [InlineData("https://127.0.0.1/hook", false)]         // loopback
    [InlineData("https://10.1.2.3/hook", false)]          // RFC 1918
    [InlineData("https://172.16.5.5/hook", false)]        // RFC 1918
    [InlineData("https://192.168.0.1/hook", false)]       // RFC 1918
    [InlineData("https://169.254.169.254/hook", false)]   // link-local (cloud metadata)
    [InlineData("https://100.100.0.1/hook", false)]       // CGNAT
    [InlineData("https://0.0.0.0/hook", false)]           // unspecified
    [InlineData("https://239.255.255.250/hook", false)]   // multicast
    // Rejected: blocked IPv6 ranges + an IPv4-mapped private literal.
    [InlineData("https://[::1]/hook", false)]             // loopback
    [InlineData("https://[fe80::1]/hook", false)]         // link-local
    [InlineData("https://[fc00::1]/hook", false)]         // unique-local
    [InlineData("https://[ff02::1]/hook", false)]         // multicast
    [InlineData("https://[::ffff:10.0.0.1]/hook", false)] // IPv4-mapped private
    public void Classifies_delivery_targets(string target, bool allowed)
    {
        var ok = WebhookUrlPolicy.IsAllowed(target, out var error);

        ok.ShouldBe(allowed);
        if (allowed)
        {
            error.ShouldBeNull();
        }
        else
        {
            error.ShouldNotBeNullOrEmpty();
        }
    }

    [Theory]
    // The reserved-range denylist reused at send time (WebhookConnectGuard) to classify a resolved address.
    [InlineData("93.184.216.34", false)]                  // public IPv4
    [InlineData("2606:2800:220:1:248:1893:25c8:1946", false)] // public IPv6
    [InlineData("169.1.1.1", false)]                      // 169.x but NOT 169.254 link-local
    [InlineData("127.0.0.1", true)]                       // loopback
    [InlineData("10.1.2.3", true)]                        // RFC 1918
    [InlineData("172.16.5.5", true)]                      // RFC 1918
    [InlineData("192.168.0.1", true)]                     // RFC 1918
    [InlineData("169.254.169.254", true)]                 // link-local (cloud metadata)
    [InlineData("100.100.0.1", true)]                     // CGNAT
    [InlineData("168.63.129.16", true)]                   // Azure WireServer (platform SSRF sink)
    [InlineData("192.0.0.1", true)]                       // IETF protocol assignments
    [InlineData("192.0.2.5", true)]                       // TEST-NET-1
    [InlineData("192.88.99.1", true)]                     // 6to4 relay anycast (deprecated)
    [InlineData("198.18.0.1", true)]                      // benchmarking
    [InlineData("198.51.100.5", true)]                    // TEST-NET-2
    [InlineData("203.0.113.5", true)]                     // TEST-NET-3
    [InlineData("0.0.0.0", true)]                         // unspecified
    [InlineData("239.255.255.250", true)]                 // multicast
    [InlineData("::1", true)]                             // IPv6 loopback
    [InlineData("2001::1", true)]                         // Teredo
    [InlineData("2001:db8::1", true)]                     // documentation prefix
    [InlineData("2001:4860:4860::8888", false)]           // public 2001:x global unicast stays allowed (not over-blocked)
    [InlineData("fe80::1", true)]                         // IPv6 link-local
    [InlineData("fc00::1", true)]                         // IPv6 unique-local
    [InlineData("ff02::1", true)]                         // IPv6 multicast
    [InlineData("::ffff:10.0.0.1", true)]                 // IPv4-mapped private
    // Embedded-IPv4 translation prefixes: the embedded v4 is re-judged, so an internal one is blocked and a public one is not.
    [InlineData("64:ff9b::a9fe:a9fe", true)]              // NAT64 well-known -> 169.254.169.254 (cloud metadata)
    [InlineData("64:ff9b::7f00:1", true)]                 // NAT64 well-known -> 127.0.0.1
    [InlineData("64:ff9b::5db8:d822", false)]             // NAT64 well-known -> 93.184.216.34 (public) stays allowed
    [InlineData("64:ff9b:1:a9fe:a9:fe00::", true)]        // NAT64 local-use /48 (RFC 8215) -> 169.254.169.254
    [InlineData("64:ff9b:1:5db8:d8:2200::", false)]       // NAT64 local-use /48 -> 93.184.216.34 (public) stays allowed
    [InlineData("2002:a9fe:a9fe::1", true)]               // 6to4 -> 169.254.169.254
    [InlineData("::7f00:1", true)]                        // IPv4-compatible -> 127.0.0.1
    public void Classifies_resolved_addresses(string address, bool blocked) =>
        WebhookUrlPolicy.IsBlockedAddress(IPAddress.Parse(address)).ShouldBe(blocked);
}
