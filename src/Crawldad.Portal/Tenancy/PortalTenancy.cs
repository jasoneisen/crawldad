using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

namespace Crawldad.Portal.Tenancy;

/// <summary>Shared constants and small helpers for the portal→tenant link + Crawldad.Client wiring. Centralizes the
/// Data-Protection purpose (so the write side in <see cref="MartenPortalTenantLinkStore"/> and the read side in
/// <see cref="PortalTenantContext"/> bind the exact same key ring), the named <c>HttpClient</c> the SDK rides on,
/// and the configuration keys — all referenced by <c>PortalHost</c> at wiring time.</summary>
internal static class PortalTenancy
{
    /// <summary>The Data-Protection purpose the tenant-API-key protector is bound to. Purpose isolation means this
    /// key ring cannot decrypt (or be decrypted by) any other protector in the app. Pinned as a durable string:
    /// it is not tied to the type's namespace and must not change once any link ciphertext exists, or
    /// <c>Unprotect</c> breaks.</summary>
    internal const string ApiKeyProtectorPurpose = "Crawldad.Portal.Auth.TenantApiKey.v1";

    /// <summary>The name of the pooled <see cref="System.Net.Http.HttpClient"/> the tenant's <c>CrawldadClient</c>
    /// rides on. Registered with the API base address; the per-request API key is applied by the context.</summary>
    internal const string ApiHttpClientName = "Crawldad.Api";

    /// <summary>Configuration key for the Crawldad API base URL the portal calls (e.g.
    /// <c>https://api.crawldad.io/</c>).</summary>
    internal const string ApiBaseUrlConfigKey = "Crawldad:Api:BaseUrl";

    /// <summary>Configuration section (Development only) that seeds/updates a single tenant link at startup.</summary>
    internal const string DevTenantLinkSection = "Portal:DevTenantLink";

    /// <summary>Creates the purpose-bound protector both the store (protect on write) and the context (unprotect on
    /// read) use, so a key protected by one is always readable by the other.</summary>
    internal static IDataProtector ApiKeyProtector(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider.CreateProtector(ApiKeyProtectorPurpose);
    }

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
