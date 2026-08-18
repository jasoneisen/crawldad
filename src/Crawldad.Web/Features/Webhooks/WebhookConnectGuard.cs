using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Crawldad.Web.Features.Webhooks;

/// <summary>Resolves a delivery host to its IP addresses. The seam lets a test drive the send-time guard with a
/// deterministic resolution — a name that "rebinds" to an internal address — with no real DNS lookup; production binds
/// it to <see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/>.</summary>
internal delegate ValueTask<IPAddress[]> ResolveHost(string host, CancellationToken ct);

/// <summary>Raised when a webhook delivery target resolves to an address it must never reach (a loopback, link-local,
/// private, or otherwise reserved range) or to no address at all. It surfaces from the connect callback as the inner
/// cause of the <see cref="HttpRequestException"/> the sender already treats as a (retryable) non-delivery, so a
/// rebinding target is refused without ever opening a connection to it.</summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors",
    Justification = "A single-message internal SSRF signal caught within delivery; the extra public constructors would be dead code the coverage gate then flags.")]
[SuppressMessage("Roslynator", "RCS1194:Implement exception constructors",
    Justification = "See CA1032 justification.")]
internal sealed class WebhookSsrfException : Exception
{
    /// <summary>Creates the SSRF-rejection signal with a caller-safe reason.</summary>
    public WebhookSsrfException(string message)
        : base(message)
    {
    }
}

/// <summary>The send-time half of the webhook SSRF guard (the registration half is <see cref="WebhookUrlPolicy"/>).
/// Registration classifies only the literal a tenant typed, so a DNS name that points — or later <b>rebinds</b> — to an
/// internal address would otherwise slip through at delivery. This guard closes that: at connect time it resolves the
/// target host, re-classifies <b>every</b> resolved address against the same reserved-range denylist, and — because it
/// hands the socket the exact addresses it just validated — <b>pins</b> the connection to them, so a second resolution
/// can never swap in an internal address between the check and the connect. It is wired as the
/// <see cref="SocketsHttpHandler.ConnectCallback"/> of the delivery client (which also refuses redirects); TLS, SNI, and
/// the Host header still use the original hostname, so a valid public certificate is required exactly as before — only
/// the transport IP is pinned.</summary>
internal sealed class WebhookConnectGuard(Func<IPAddress, bool> isBlocked, ResolveHost resolve)
{
    /// <summary>Resolves <paramref name="host"/> and returns its addresses, throwing <see cref="WebhookSsrfException"/>
    /// if it resolves to nothing or to any blocked (internal/reserved) address. Rejecting when <b>any</b> resolved
    /// address is blocked defeats a multi-record answer that pairs a public decoy with an internal target.</summary>
    public async ValueTask<IReadOnlyList<IPAddress>> ResolveAndValidateAsync(string host, CancellationToken ct)
    {
        var addresses = await resolve(host, ct);
        if (addresses.Length == 0)
        {
            throw new WebhookSsrfException($"delivery host '{host}' did not resolve to any address");
        }

        foreach (var address in addresses)
        {
            if (isBlocked(address))
            {
                throw new WebhookSsrfException("delivery target resolves to a loopback, link-local, or private address");
            }
        }

        return addresses;
    }

    /// <summary>The <see cref="SocketsHttpHandler.ConnectCallback"/>: resolve-and-validate the target, then open the TCP
    /// connection <b>pinned</b> to a validated address (no re-resolution, so nothing can rebind under it). The socket is
    /// disposed on any connect failure; on success the returned <see cref="NetworkStream"/> owns it.</summary>
    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await ResolveAndValidateAsync(context.DnsEndPoint.Host, ct);

        Socket? socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync([.. addresses], context.DnsEndPoint.Port, ct);
            var stream = new NetworkStream(socket, ownsSocket: true);
            socket = null; // ownership passed to the stream — the finally must not dispose it
            return stream;
        }
        finally
        {
            socket?.Dispose();
        }
    }
}

/// <summary>The named delivery <see cref="HttpClient"/> for webhooks and its SSRF-hardened primary handler. A named
/// client isolates the guard to webhook deliveries — other slices keep the shared default client — while still pooling
/// the handler through <see cref="IHttpClientFactory"/>.</summary>
internal static class WebhookHttpClient
{
    /// <summary>The <see cref="IHttpClientFactory"/> name of the SSRF-guarded delivery client.</summary>
    public const string Name = "webhooks";

    /// <summary>Builds the delivery handler with the production denylist (<see cref="WebhookUrlPolicy.IsBlockedAddress"/>)
    /// and the real DNS resolver.</summary>
    public static SocketsHttpHandler CreateHandler() => CreateHandler(WebhookUrlPolicy.IsBlockedAddress, ResolveWithDns);

    /// <summary>Builds the delivery handler: redirects refused (a 3xx to an internal target would bypass the guard) and
    /// every connection resolve-pinned by a <see cref="WebhookConnectGuard"/> over the given classifier/resolver, which
    /// are injectable so a test can drive the redirect and rebind behaviour deterministically.</summary>
    internal static SocketsHttpHandler CreateHandler(Func<IPAddress, bool> isBlocked, ResolveHost resolve) =>
        new()
        {
            AllowAutoRedirect = false,
            ConnectCallback = new WebhookConnectGuard(isBlocked, resolve).ConnectAsync,
        };

    private static ValueTask<IPAddress[]> ResolveWithDns(string host, CancellationToken ct) =>
        new(Dns.GetHostAddressesAsync(host, ct));
}
