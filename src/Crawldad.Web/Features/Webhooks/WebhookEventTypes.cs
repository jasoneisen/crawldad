namespace Crawldad.Web.Features.Webhooks;

/// <summary>The webhook event catalog: the terminal-run dispositions a tenant can subscribe an endpoint to. The set is
/// deliberately small and stable — the same three terminal events a run's stream ever appends — and open to extension
/// (e.g. a future <c>payload.drifted</c> alert riding the same signed channel). A subscription is a subset of these;
/// an empty subscription means "all".</summary>
internal static class WebhookEventTypes
{
    /// <summary>A run completed successfully (<c>RunSucceeded</c>).</summary>
    public const string RunSucceeded = "run.succeeded";

    /// <summary>A run ended in a typed failure — including a deadline or queue-wait timeout (<c>RunFailed</c>).</summary>
    public const string RunFailed = "run.failed";

    /// <summary>A run was cancelled, whether cooperatively mid-run or while still queued (<c>RunCancelled</c>).</summary>
    public const string RunCancelled = "run.cancelled";

    private static readonly HashSet<string> _all = new(StringComparer.Ordinal) { RunSucceeded, RunFailed, RunCancelled };

    /// <summary>Whether <paramref name="eventType"/> is a recognised, subscribable event type.</summary>
    public static bool IsKnown(string eventType) => _all.Contains(eventType);

    /// <summary>The catalog as a comma-separated list, for validation messages and docs.</summary>
    public static string Catalog => string.Join(", ", _all);
}
