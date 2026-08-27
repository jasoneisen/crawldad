namespace Crawldad.Portal.Auth;

/// <summary>Open-redirect guard for the post-sign-in return URL. Only same-site absolute paths are honored;
/// anything else (absolute URLs, protocol-relative <c>//host</c>, empty) falls back to the app home.</summary>
internal static class SafeRedirect
{
    /// <summary>The authenticated landing page and the fallback when a return URL is missing or unsafe.</summary>
    internal const string AppHome = "/app";

    internal static string ToLocalOrApp(string? returnUrl) =>
        // Same-site only: must start with a single '/' that is NOT followed by '/' or '\'. This rejects absolute
        // URLs, protocol-relative "//host", the "/\host" backslash variant, and a leading backslash — the guard
        // itself is correct, not merely by luck of downstream redirect resolution.
        !string.IsNullOrEmpty(returnUrl)
        && returnUrl[0] == '/'
        && (returnUrl.Length == 1 || (returnUrl[1] != '/' && returnUrl[1] != '\\'))
            ? returnUrl
            : AppHome;
}
