using System.Text.RegularExpressions;

namespace Crawldad.Api.Features.Browsers;

/// <summary>The registration validation vocabulary — known adapters/modes, the connectUrl shape check, and the name
/// slug rule — shared by the request validator and the endpoint's route-name guard so both agree by construction.</summary>
internal static partial class BrowserRegistrationRules
{
    /// <summary>The secret is the whole connect URL.</summary>
    public const string ConnectUrlMode = "connectUrl";

    /// <summary>The secret is a provider api key.</summary>
    public const string ApiKeyMode = "apiKey";

    /// <summary>The CDP adapter — connects to a caller/session connect URL, so it supports both
    /// <see cref="ConnectUrlMode"/> and <see cref="ApiKeyMode"/>.</summary>
    public const string BrowserbaseAdapter = "browserbase";

    /// <summary>The native-connect adapter — connects only by token (<c>?token=…</c>), so it supports
    /// <see cref="ApiKeyMode"/> only; it has no connectUrl connect path.</summary>
    public const string BrowserlessAdapter = "browserless";

    // Only the two credentialed adapters are registerable; local/fake need no credential.
    private static readonly HashSet<string> _adapters =
        new(StringComparer.Ordinal) { BrowserbaseAdapter, BrowserlessAdapter };

    private static readonly HashSet<string> _modes =
        new(StringComparer.Ordinal) { ConnectUrlMode, ApiKeyMode };

    /// <summary>Whether <paramref name="adapter"/> is a registerable, credentialed backend adapter.</summary>
    public static bool IsKnownAdapter(string adapter) => _adapters.Contains(adapter);

    /// <summary>Whether <paramref name="mode"/> is a recognised credential mode.</summary>
    public static bool IsKnownMode(string mode) => _modes.Contains(mode);

    /// <summary>Whether the <paramref name="adapter"/> actually has a connect path for <paramref name="mode"/>. Only
    /// <see cref="BrowserlessAdapter"/> + <see cref="ConnectUrlMode"/> is unsupported: browserless connects natively by
    /// token and has no connectUrl path, so that pairing is inert — it would register cleanly and then fail closed at
    /// connect. Rejecting it at registration turns a silent misconfiguration into a clear <c>400</c>. browserbase
    /// supports both modes; unknown adapters/modes are rejected by their own guards, so they never falsely trip here.</summary>
    public static bool AdapterSupportsMode(string adapter, string mode) =>
        !(string.Equals(adapter, BrowserlessAdapter, StringComparison.Ordinal)
            && string.Equals(mode, ConnectUrlMode, StringComparison.Ordinal));

    /// <summary>Whether a connectUrl-mode secret has a valid connect-URL scheme (wss:// or https://).</summary>
    public static bool IsConnectUrlShape(string secret) =>
        secret.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)
        || secret.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    // The name is the credentialRef and the Marten document id: a lowercase slug (alnum + hyphen, 1..64, no leading/
    // trailing hyphen). Excludes ':' so the config fallback key Secrets:{tenant}:{name} stays unambiguous.
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NameSlug();

    /// <summary>Whether <paramref name="name"/> is a valid browser name slug.</summary>
    public static bool IsValidName(string? name) => name is not null && NameSlug().IsMatch(name);
}
