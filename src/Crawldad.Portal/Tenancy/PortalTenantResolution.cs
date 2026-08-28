using System.Security.Cryptography;
using Crawldad.Client;
using Microsoft.AspNetCore.DataProtection;

namespace Crawldad.Portal.Tenancy;

/// <summary>The one place the console-vs-key decision for a resolved <c>(email, link)</c> lives (issue #119 PR5), shared by
/// the static-SSR <see cref="PortalTenantContext"/> and the circuit <see cref="CircuitTenantResolver"/> so the two never
/// drift. Console-mode (a non-null <paramref name="consoleClients"/>) builds a first-party console client — reads console
/// -first with the stored key (if one remains) as a transition fallback, writes console-only — and a lost/rotated fallback
/// key is not fatal (the console credential authenticates regardless). Stored-key mode decrypts the key into the client
/// exactly as before; a keyless link there cannot authenticate, so it resolves to a clean not-linked state.</summary>
internal static class PortalTenantResolution
{
    /// <summary>Builds the <see cref="PortalTenant"/> for a resolved <paramref name="email"/> and <paramref name="link"/>,
    /// or null when there is nothing to authenticate with (a keyless link outside console-mode). In stored-key mode a
    /// decryption failure surfaces as <see cref="CryptographicException"/> for the caller's re-link prompt.</summary>
    public static PortalTenant? Resolve(
        string email,
        PortalTenantLink link,
        IDataProtector protector,
        IHttpClientFactory httpClientFactory,
        ConsoleClientFactory? consoleClients)
    {
        ArgumentNullException.ThrowIfNull(link);

        // Console-mode: the portal calls the API as its first-party console identity. A console-mode attach records a
        // membership and discards the key, so a link may carry no stored key — then the console credential is the only
        // authenticator and there is no fallback.
        if (consoleClients is not null)
        {
            var fallbackKey = UnprotectFallback(protector, link.ProtectedApiKey);
            var consoleClient = consoleClients.Build(email, link.TenantId, fallbackKey);
            return new PortalTenant(link.TenantId, consoleClient, PortalAuthMode.Console, storedKeyRetained: fallbackKey is not null);
        }

        // Stored-key mode (unconfigured): byte-identical to today. A keyless link cannot authenticate here → not-linked.
        if (link.ProtectedApiKey is null)
        {
            return null;
        }

        var apiKey = protector.Unprotect(link.ProtectedApiKey);
        var http = httpClientFactory.CreateClient(PortalTenancy.ApiHttpClientName); // base address preset at wiring
        var client = new CrawldadClient(http, new CrawldadClientOptions { ApiKey = apiKey });
        return new PortalTenant(link.TenantId, client);
    }

    // Decrypts a console-mode link's transition read-fallback key, or null when there is none (keyless) or the stored
    // ciphertext can no longer be decrypted (the DP ring rotated). In console-mode a lost fallback key is not fatal — the
    // console credential authenticates regardless — so this never throws, unlike the stored-key path's decrypt.
    private static string? UnprotectFallback(IDataProtector protector, string? protectedApiKey)
    {
        if (protectedApiKey is null)
        {
            return null;
        }

        try
        {
            return protector.Unprotect(protectedApiKey);
        }
        catch (CryptographicException)
        {
            return null; // DP ring rotated — drop the fallback; the console credential still works
        }
    }
}
