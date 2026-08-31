using Microsoft.Extensions.Configuration;

namespace Crawldad.Portal.Tenancy;

/// <summary>Shared constants and small helpers for the portal→API console wiring. The portal reaches Crawldad only through
/// the typed <c>Crawldad.Client</c> SDK over one pooled <c>HttpClient</c>; the per-request credential is the portal's
/// first-party console identity (there is no stored tenant key, so no Data-Protection tenant-key purpose lives here — the
/// portal's Data-Protection ring backs only its auth/antiforgery cookies now). All referenced by <c>PortalHost</c> at
/// wiring time.</summary>
internal static class PortalTenancy
{
    /// <summary>The name of the pooled <see cref="System.Net.Http.HttpClient"/> the console <c>CrawldadClient</c> rides on.
    /// Registered with the API base address; the per-request console credential is applied by the client.</summary>
    internal const string ApiHttpClientName = "Crawldad.Api";

    /// <summary>Configuration key for the Crawldad API base URL the portal calls (e.g.
    /// <c>https://api.crawldad.io/</c>).</summary>
    internal const string ApiBaseUrlConfigKey = "Crawldad:Api:BaseUrl";

    /// <summary>Reads and validates <see cref="ApiBaseUrlConfigKey"/> at wiring time, so a missing or malformed base
    /// URL fails the boot loudly rather than surfacing as an opaque error on the first API call.</summary>
    /// <param name="configuration">The host configuration.</param>
    /// <returns>The API base URL as an absolute <see cref="Uri"/>.</returns>
    /// <exception cref="InvalidOperationException">The key is unset, blank, or not an absolute URL.</exception>
    internal static Uri ResolveApiBaseUrl(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var raw = configuration[ApiBaseUrlConfigKey];
        if (string.IsNullOrWhiteSpace(raw) || !Uri.TryCreate(raw, UriKind.Absolute, out var baseUrl))
        {
            throw new InvalidOperationException(
                $"Configuration '{ApiBaseUrlConfigKey}' must be set to the absolute Crawldad API base URL (e.g. https://api.crawldad.io/).");
        }

        return baseUrl;
    }
}
