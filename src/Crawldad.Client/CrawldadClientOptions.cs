namespace Crawldad.Client;

/// <summary>Configuration for a <see cref="CrawldadClient"/>: where the API lives and the per-tenant API key that
/// authenticates every request. Bound by <see cref="CrawldadClientServiceCollectionExtensions.AddCrawldadClient"/>
/// or passed directly to the client constructor.</summary>
public sealed class CrawldadClientOptions
{
    /// <summary>The API base address, e.g. <c>https://api.crawldad.io/</c>. Required. A trailing slash is added if
    /// missing so relative request paths resolve correctly.</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>The per-tenant API key. Sent as <c>Authorization: Bearer &lt;key&gt;</c> on every request (the API's
    /// primary convention). Required. Write-only in spirit — keep it out of logs.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Validates that the required options are present, throwing <see cref="InvalidOperationException"/>
    /// otherwise, and returns the base URL normalized to end with a slash. Called eagerly by the DI extension so a
    /// misconfiguration fails fast at startup rather than on the first request.</summary>
    /// <returns>The <see cref="BaseUrl"/> guaranteed to end with a trailing slash.</returns>
    /// <exception cref="InvalidOperationException"><see cref="BaseUrl"/> or <see cref="ApiKey"/> is missing.</exception>
    public Uri Validate()
    {
        if (BaseUrl is null)
        {
            throw new InvalidOperationException($"{nameof(CrawldadClientOptions)}.{nameof(BaseUrl)} must be set to the Crawldad API base address.");
        }

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new InvalidOperationException($"{nameof(CrawldadClientOptions)}.{nameof(ApiKey)} must be set to a tenant API key.");
        }

        return NormalizeBaseUrl(BaseUrl);
    }

    /// <summary>Returns <paramref name="baseUrl"/> with a guaranteed trailing slash, so a relative path like
    /// <c>runs</c> resolves under it rather than replacing its last segment.</summary>
    internal static Uri NormalizeBaseUrl(Uri baseUrl) =>
        baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");
}
