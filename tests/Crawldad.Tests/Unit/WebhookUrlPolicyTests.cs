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
}
