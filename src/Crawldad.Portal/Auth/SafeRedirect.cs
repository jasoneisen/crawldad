namespace Crawldad.Portal.Auth;

/// <summary>Open-redirect guard for the post-sign-in return URL. Only same-site absolute paths are honored;
/// anything else (absolute URLs, protocol-relative <c>//host</c>, empty) falls back to the app home.</summary>
internal static class SafeRedirect
{
    /// <summary>The authenticated landing page and the fallback when a return URL is missing or unsafe.</summary>
    internal const string AppHome = "/app";

    internal static string ToLocalOrApp(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl)
        && returnUrl.StartsWith('/')
        && !returnUrl.StartsWith("//", StringComparison.Ordinal)
            ? returnUrl
            : AppHome;
}
