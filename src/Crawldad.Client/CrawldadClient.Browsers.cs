using Crawldad.Contracts.Browsers;

namespace Crawldad.Client;

/// <summary>Browser connect-credential surface: register (or replace), list, and unregister. The connect URL / api key
/// is write-only — sent on register, never returned by any endpoint.</summary>
public sealed partial class CrawldadClient
{
    /// <summary>Registers or replaces a browser connect credential (<c>PUT /browsers/{name}</c>). The name becomes the
    /// <c>credentialRef</c> payloads use. The response is the stored metadata only — never the secret.</summary>
    /// <param name="name">The credential name (a slug).</param>
    /// <param name="request">The adapter, mode, secret, and optional options.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The stored browser metadata.</returns>
    /// <exception cref="CrawldadValidationException">Invalid name slug, or an inert adapter/mode combination (<c>400</c>).</exception>
    public Task<BrowserSummary> RegisterBrowserAsync(string name, RegisterBrowserRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(request);
        return SendJsonAsync<BrowserSummary>(HttpMethod.Put, $"browsers/{Uri.EscapeDataString(name)}", request, ct);
    }

    /// <summary>Lists the tenant's registered browsers (<c>GET /browsers</c>) — metadata only, never the secret.</summary>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The browser listing.</returns>
    public Task<BrowserListResponse> ListBrowsersAsync(CancellationToken ct = default) =>
        GetAsync<BrowserListResponse>("browsers", ct);

    /// <summary>Unregisters a browser credential (<c>DELETE /browsers/{name}</c>).</summary>
    /// <param name="name">The credential name.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <exception cref="CrawldadNotFoundException">No such browser for this tenant (<c>404</c>).</exception>
    public Task UnregisterBrowserAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return SendNoContentAsync(HttpMethod.Delete, $"browsers/{Uri.EscapeDataString(name)}", ct);
    }
}
