using System.Security.Cryptography;

namespace Crawldad.Portal.Tenancy;

/// <summary>Shared tenant-resolution helper for the static-SSR data pages. Every data page resolves the signed-in
/// tenant the same way — <see cref="IPortalTenantContext.TryResolveAsync"/>, then branch to a not-linked empty state on
/// <see langword="null"/> — and every one faces the same failure mode: when the portal's Data-Protection key ring is
/// rotated or lost, the stored API key can no longer be decrypted and <c>TryResolveAsync</c> throws
/// <see cref="CryptographicException"/> from inside <c>Unprotect</c>. Left uncaught that would 500 the whole page. This
/// folds the catch into one place so the seven data pages don't each copy it.</summary>
internal static class PortalTenantContextExtensions
{
    /// <summary>Resolves the current user's tenant for a data page: the linked tenant, or <see langword="null"/> when
    /// the request is unauthenticated, unlinked, OR its stored key can no longer be decrypted (a rotated/lost
    /// Data-Protection ring). All three null cases render the page's existing not-linked empty state, which points the
    /// user at the Account page — where the richer "re-link required" prompt and the re-link form live (the account page
    /// keeps its own distinct <see cref="CryptographicException"/> handling to show that prompt directly).</summary>
    public static async Task<PortalTenant?> TryResolveForPageAsync(
        this IPortalTenantContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await context.TryResolveAsync(cancellationToken);
        }
        catch (CryptographicException)
        {
            // The account IS linked, but its stored key is unrecoverable (the ring was rotated/lost). On a secondary
            // data page that is a not-linked-shaped empty state pointing at Account, never a 500.
            return null;
        }
    }
}
