namespace Crawldad.Contracts;

/// <summary>The single, shared email normalizer (issue #119 PR4, the review's binding finding #2). One canonical form —
/// trimmed and lower-invariant — used byte-for-byte on <b>every</b> side of an identity comparison: the portal writes a
/// <c>PortalUser</c>/<c>PortalTenantLink</c> under it, the portal selector-producer sends the console-user header under
/// it, and the API's membership store <b>writes and looks up</b> under it. Promoted here so those call sites share one
/// implementation across the assembly boundary (the API references only <c>Contracts</c>): if the portal and the API
/// normalized differently, a membership written under one form and looked up under another would silently <c>403</c> a
/// legitimate user — an availability failure that reads as an auth bug.</summary>
public static class EmailAddress
{
    /// <summary>Case- and whitespace-normalize an email to its canonical stored/compared form. Exactly
    /// <c>email.Trim().ToLowerInvariant()</c> — the historical <c>PortalAuthService.NormalizeEmail</c> behaviour, preserved
    /// byte-for-byte so existing <c>PortalUser</c>/<c>PortalTenantLink</c> ids keep resolving.</summary>
    /// <param name="email">The raw email (from a form, a claim, or a selector header).</param>
    /// <returns>The trimmed, lower-invariant form.</returns>
    public static string Normalize(string email) => email.Trim().ToLowerInvariant();
}
