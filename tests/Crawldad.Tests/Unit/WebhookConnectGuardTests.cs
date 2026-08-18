using System.Net;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Webhooks;

namespace Crawldad.Tests.Unit;

/// <summary>The send-time SSRF guard: it resolves the delivery host, refuses a resolution to any internal/reserved
/// address (the DNS-rebinding defence registration alone cannot give), pins the TCP connection to a validated address,
/// and — through the delivery handler it wires — refuses redirects. Rebinding is driven deterministically via an
/// injected resolver (no real DNS); the redirect/pin/block behaviour runs the real handler against a loopback origin.</summary>
public class WebhookConnectGuardTests
{
    private static readonly ResolveHost _realDns = (host, ct) => new(Dns.GetHostAddressesAsync(host, ct));
    private static readonly CancellationToken _ct = CancellationToken.None;

    // A guard over the production denylist with a fixed, DNS-free resolution — the seam that lets a "public" name
    // resolve to whatever address a rebinding attacker would return.
    private static WebhookConnectGuard Guard(ResolveHost resolve) => new(WebhookUrlPolicy.IsBlockedAddress, resolve);

    private static ResolveHost Resolves(params string[] ips) => (_, _) => new([.. ips.Select(IPAddress.Parse)]);

    private static bool HasSsrfCause(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
        {
            if (e is WebhookSsrfException)
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public async Task A_public_resolution_is_allowed_and_pinned()
    {
        var addresses = await Guard(Resolves("93.184.216.34", "2606:2800:220:1:248:1893:25c8:1946"))
            .ResolveAndValidateAsync("hooks.example.com", _ct);

        addresses.Count.ShouldBe(2); // both validated addresses are returned for the socket to pin to
    }

    [Theory]
    // A name that resolves (or rebinds) to an internal/reserved address is refused at send — the gap registration leaves.
    [InlineData("127.0.0.1")]        // loopback
    [InlineData("10.0.0.1")]         // RFC 1918
    [InlineData("169.254.169.254")]  // link-local (cloud metadata)
    [InlineData("100.64.0.1")]       // CGNAT
    [InlineData("::1")]              // IPv6 loopback
    [InlineData("fc00::1")]          // IPv6 unique-local
    [InlineData("::ffff:10.0.0.1")]  // IPv4-mapped private
    public async Task A_resolution_to_a_blocked_address_is_refused(string ip) =>
        await Should.ThrowAsync<WebhookSsrfException>(
            () => Guard(Resolves(ip)).ResolveAndValidateAsync("rebind.example.com", _ct).AsTask());

    [Fact]
    public async Task A_mixed_answer_with_one_internal_address_is_refused() => // multi-record rebinding
        await Should.ThrowAsync<WebhookSsrfException>(
            () => Guard(Resolves("93.184.216.34", "10.0.0.1")).ResolveAndValidateAsync("rebind.example.com", _ct).AsTask());

    [Fact]
    public async Task An_empty_resolution_is_refused() =>
        await Should.ThrowAsync<WebhookSsrfException>(
            () => Guard(Resolves()).ResolveAndValidateAsync("nowhere.example.com", _ct).AsTask());

    [Fact]
    public async Task A_literal_loopback_host_is_refused_by_real_dns() =>
        await Should.ThrowAsync<WebhookSsrfException>(
            () => Guard(_realDns).ResolveAndValidateAsync("127.0.0.1", _ct).AsTask());

    [Fact]
    public async Task Localhost_resolves_to_loopback_and_is_refused() => // a real name, resolved and re-classified
        await Should.ThrowAsync<WebhookSsrfException>(
            () => Guard(_realDns).ResolveAndValidateAsync("localhost", _ct).AsTask());

    [Fact]
    public async Task A_public_literal_host_passes_real_dns()
    {
        var addresses = await Guard(_realDns).ResolveAndValidateAsync("93.184.216.34", _ct);

        addresses.ShouldHaveSingleItem().ShouldBe(IPAddress.Parse("93.184.216.34"));
    }

    [Fact]
    public void The_delivery_handler_refuses_redirects()
    {
        using var handler = WebhookHttpClient.CreateHandler();

        handler.AllowAutoRedirect.ShouldBeFalse();
    }

    [Fact]
    public async Task A_redirect_is_not_followed()
    {
        using var site = new LocalSite();
        site.Map("/hook", "text/plain", "moved", status: 302, location: site.Url("/internal"));
        site.Map("/internal", "application/json", "SHOULD NOT BE FETCHED", status: 200);

        // Allow the loopback origin (a permissive classifier) so the request reaches it — then prove the 3xx is surfaced,
        // not chased to the (would-be internal) redirect target.
        using var client = new HttpClient(WebhookHttpClient.CreateHandler(static _ => false, _realDns));
        using var response = await client.PostAsync(new Uri(site.Url("/hook")), new StringContent("{}"), _ct);

        ((int)response.StatusCode).ShouldBe(302);
        site.Hits("/internal").ShouldBe(0);
    }

    [Fact]
    public async Task A_pinned_connection_to_an_allowed_address_delivers()
    {
        using var site = new LocalSite().Map("/hook", "application/json", "ok", status: 200);

        using var client = new HttpClient(WebhookHttpClient.CreateHandler(static _ => false, _realDns));
        using var response = await client.PostAsync(new Uri(site.Url("/hook")), new StringContent("{}"), _ct);

        ((int)response.StatusCode).ShouldBe(200);
        site.Hits("/hook").ShouldBe(1); // the custom pinned-connect path serves real traffic
    }

    [Fact]
    public async Task A_target_that_resolves_internal_is_refused_before_any_connection()
    {
        using var site = new LocalSite().Map("/hook", "application/json", "ok", status: 200);

        // The production handler blocks the loopback origin: the delivery fails and the origin is never touched.
        using var client = new HttpClient(WebhookHttpClient.CreateHandler());
        var error = await Should.ThrowAsync<HttpRequestException>(
            () => client.PostAsync(new Uri(site.Url("/hook")), new StringContent("{}"), _ct));

        HasSsrfCause(error).ShouldBeTrue();  // the guard is the cause, not an incidental failure
        site.Hits("/hook").ShouldBe(0);      // pinned check ran before connect — nothing reached the origin
    }

    [Fact]
    public async Task A_validated_address_that_refuses_the_connection_is_a_transport_failure()
    {
        // Permissive classifier so validation passes; nothing is listening, so the pinned connect fails at the socket —
        // exercising the connect-failure cleanup path (not the guard).
        using var client = new HttpClient(WebhookHttpClient.CreateHandler(static _ => false, _realDns));
        var dead = new Uri($"http://127.0.0.1:{Net.FreePort()}/hook");

        var error = await Should.ThrowAsync<HttpRequestException>(
            () => client.PostAsync(dead, new StringContent("{}"), _ct));

        HasSsrfCause(error).ShouldBeFalse();
    }
}
