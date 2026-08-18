using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
    [InlineData("64:ff9b::a9fe:a9fe")] // NAT64-synthesised 169.254.169.254 (DNS64 rebinding)
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
    public void The_delivery_handler_disables_redirects_and_proxy()
    {
        using var handler = WebhookHttpClient.CreateHandler();

        handler.AllowAutoRedirect.ShouldBeFalse();
        // With a proxy the ConnectCallback would receive the proxy endpoint, not the target — pinning the proxy while it
        // reaches the tenant host. UseProxy must stay off so the guard governs the real destination.
        handler.UseProxy.ShouldBeFalse();
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

    [Fact]
    [SuppressMessage("Security", "CA5359:Do not disable certificate validation",
        Justification = "The delivery client validates certificates in production; this test bypasses validation only to accept an in-process, self-signed loopback certificate — the point is to run a real TLS handshake through the custom pinned ConnectCallback.")]
    public async Task An_https_delivery_completes_a_real_tls_handshake_through_the_pinned_connect()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var certificate = SelfSignedLoopbackCertificate();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        string? observedSni = null;
        var serve = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var tls = new SslStream(connection.GetStream(), leaveInnerStreamOpen: false);
            await tls.AuthenticateAsServerAsync(
                (_, hello, _, _) =>
                {
                    observedSni = hello.ServerName;
                    return ValueTask.FromResult(new SslServerAuthenticationOptions { ServerCertificate = certificate });
                },
                state: null,
                timeout.Token);

            var request = new byte[1024];
            _ = await tls.ReadAsync(request, timeout.Token); // consume the request head; the 2-byte body fits the socket buffer
            await tls.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray(), timeout.Token);
            await tls.FlushAsync(timeout.Token);
        });

        // Allow the loopback listener (permissive classifier), and trust the self-signed cert so the handshake can complete;
        // the handler still owns the pinned ConnectCallback, so TLS is layered on top of the guard's own transport.
        using var handler = WebhookHttpClient.CreateHandler(static _ => false, _realDns);
        handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        using var client = new HttpClient(handler, disposeHandler: false);

        // A hostname target (not an IP literal) so a TLS SNI is actually sent — proving SNI carries the original host name.
        using var response = await client.PostAsync(new Uri($"https://localhost:{port}/hook"), new StringContent("{}"), timeout.Token);
        await serve;

        ((int)response.StatusCode).ShouldBe(200);
        observedSni.ShouldBe("localhost"); // TLS/SNI use the original hostname; only the transport IP was pinned
    }

    private static X509Certificate2 SelfSignedLoopbackCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
