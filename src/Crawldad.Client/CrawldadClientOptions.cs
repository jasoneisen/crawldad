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
    /// primary convention) when no explicit <see cref="Credential"/> is supplied. Required unless <see cref="Credential"/>
    /// is set. Write-only in spirit — keep it out of logs.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>An explicit per-request credential (issue #119). When set it authenticates every request and
    /// <see cref="ApiKey"/> is ignored — the seam the portal uses for its first-party <see cref="ConsoleCredential"/> and
    /// tests use for a <see cref="DelegateCredential"/>. When null, a non-blank <see cref="ApiKey"/> is wrapped in an
    /// <see cref="ApiKeyCredential"/>, so every existing caller keeps working unchanged.</summary>
    public ICrawldadCredential? Credential { get; set; }

    /// <summary>Validates that the required options are present, throwing <see cref="InvalidOperationException"/>
    /// otherwise, and returns the base URL normalized to end with a slash. Called eagerly by the DI extension so a
    /// misconfiguration fails fast at startup rather than on the first request.</summary>
    /// <returns>The <see cref="BaseUrl"/> guaranteed to end with a trailing slash.</returns>
    /// <exception cref="InvalidOperationException"><see cref="BaseUrl"/> is missing, or neither <see cref="ApiKey"/> nor
    /// <see cref="Credential"/> is set.</exception>
    public Uri Validate()
    {
        if (BaseUrl is null)
        {
            throw new InvalidOperationException($"{nameof(CrawldadClientOptions)}.{nameof(BaseUrl)} must be set to the Crawldad API base address.");
        }

        if (Credential is null && string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new InvalidOperationException($"{nameof(CrawldadClientOptions)}: set either {nameof(ApiKey)} (a tenant API key) or an explicit {nameof(Credential)}.");
        }

        return NormalizeBaseUrl(BaseUrl);
    }

    /// <summary>Resolves the credential the client authenticates with: the explicit <see cref="Credential"/> if set, else a
    /// <see cref="ApiKeyCredential"/> wrapping <see cref="ApiKey"/>. Throws when neither is available.</summary>
    /// <exception cref="InvalidOperationException">Neither <see cref="Credential"/> nor a non-blank <see cref="ApiKey"/> is set.</exception>
    internal ICrawldadCredential ResolveCredential() =>
        Credential
        ?? (string.IsNullOrWhiteSpace(ApiKey)
            ? throw new InvalidOperationException($"{nameof(CrawldadClientOptions)}: set either {nameof(ApiKey)} or an explicit {nameof(Credential)}.")
            : new ApiKeyCredential(ApiKey));

    /// <summary>Returns <paramref name="baseUrl"/> with a guaranteed trailing slash, so a relative path like
    /// <c>runs</c> resolves under it rather than replacing its last segment.</summary>
    internal static Uri NormalizeBaseUrl(Uri baseUrl) =>
        baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");
}
