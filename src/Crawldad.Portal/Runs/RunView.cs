using System.Globalization;
using Crawldad.Contracts.Runs;

namespace Crawldad.Portal.Runs;

/// <summary>Pure presentation helpers for the runs list + run-detail pages: value formatting (short id, duration),
/// status → Tabler colour mapping, the screenshot-proxy and filtered-list URL builders, and the tolerant query-string
/// parsers. Kept out of the <c>.razor</c> markup so every branch is unit-testable directly (the pages stay thin and the
/// 100% coverage gate is met without threading dozens of render permutations through bUnit).</summary>
internal static class RunView
{
    private const string _screenshotsPrefix = "screenshots/";

    /// <summary>The short run id shown in a list row / detail header — the first 8 hex chars of the run's UUID (its
    /// first group), enough to eyeball while the full id stays a click away.</summary>
    public static string ShortId(Guid runId) => runId.ToString()[..8];

    /// <summary>Formats a run's wall-clock duration as seconds — two decimals under 10s, one at or above — or an em dash
    /// when absent (a running/queued run has no duration yet).</summary>
    public static string Duration(long? durationMs)
    {
        if (durationMs is not { } ms)
        {
            return "—";
        }

        var seconds = ms / 1000.0;
        return seconds < 10
            ? $"{seconds.ToString("0.00", CultureInfo.InvariantCulture)}s"
            : $"{seconds.ToString("0.0", CultureInfo.InvariantCulture)}s";
    }

    /// <summary>Formats a run instant as a fixed-width UTC wall-clock stamp (<c>yyyy-MM-dd HH:mm:ss UTC</c>) — an
    /// offset-carrying value is normalized to UTC first, so the column reads consistently regardless of the source offset.</summary>
    public static string Timestamp(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>Same as <see cref="Timestamp"/>, but an em dash when the instant is absent — a run that has not finished
    /// yet has no finish time.</summary>
    public static string TimestampOrDash(DateTimeOffset? instant) =>
        instant is { } value ? Timestamp(value) : "—";

    /// <summary>The Tabler <c>status-*</c> colour class for a run disposition: green/red/azure/yellow, with cancelled
    /// (and any future, unmapped status) falling to a neutral secondary.</summary>
    public static string StatusCss(RunStatus status) => status switch
    {
        RunStatus.Succeeded => "status-green",
        RunStatus.Failed => "status-red",
        RunStatus.Running => "status-azure",
        RunStatus.Queued => "status-yellow",
        _ => "status-secondary",
    };

    /// <summary>The nav-active class for a status-filter link: <c>active</c> when the link's <paramref name="option"/>
    /// matches the currently-applied <paramref name="current"/> filter (both null = the "All" link is active).</summary>
    public static string NavActiveCss(RunStatus? current, RunStatus? option) =>
        current == option ? "active" : "";

    /// <summary>The metric class for the selector-miss counter — <c>warn</c>-flavoured once any selector missed (the soft
    /// drift signal), plain otherwise.</summary>
    public static string MissesCss(int selectorMisses) => selectorMisses > 0 ? "m warn" : "m";

    /// <summary>The one-line payload identity for a run's meta row: <c>name · rN</c> for a pinned managed payload (or the
    /// bare name if somehow revision-less), and <c>name · inline</c> for an inline run.</summary>
    public static string PayloadLabel(Guid? payloadId, string payloadName, int? revision)
    {
        if (payloadId is null)
        {
            return $"{payloadName} · inline";
        }

        return revision is { } r ? $"{payloadName} · r{r}" : payloadName;
    }

    /// <summary>The Tabler step class for a timeline row: <c>fail</c> for the step the run failed on (matched by top-level
    /// index), <c>ok</c> for every other step.</summary>
    public static string StepCss(RunTimelineFailure? failure, RunTimelineStep step) =>
        failure is not null && failure.AtStep.Index == step.Index ? "tl-step fail" : "tl-step ok";

    /// <summary>The portal proxy URL that streams a run's screenshot to the browser (which holds no API key). Accepts the
    /// timeline's <c>screenshotRef</c> either bare (<c>{sha}.png</c>) or with its <c>screenshots/</c> prefix — the prefix
    /// is dropped so the bare ref rides the route segment.</summary>
    public static string ScreenshotUrl(Guid runId, string screenshotRef)
    {
        var bare = screenshotRef.StartsWith(_screenshotsPrefix, StringComparison.Ordinal)
            ? screenshotRef[_screenshotsPrefix.Length..]
            : screenshotRef;
        return $"/app/runs/{runId}/screenshots/{bare}";
    }

    /// <summary>Builds a <c>/app/runs</c> URL carrying the given filter + paging state, omitting any absent parameter, so
    /// a status-filter or pager link preserves the rest of the query. Values are URL-encoded.</summary>
    public static string ListUrl(RunStatus? status, Guid? payloadId, string? from, string? to, int? page, int? size)
    {
        var parts = new List<string>(6);
        Add("status", status?.ToString());
        Add("payloadId", payloadId?.ToString());
        Add("from", from);
        Add("to", to);
        Add("page", page?.ToString(CultureInfo.InvariantCulture));
        Add("size", size?.ToString(CultureInfo.InvariantCulture));
        return parts.Count == 0 ? "/app/runs" : $"/app/runs?{string.Join('&', parts)}";

        void Add(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                parts.Add($"{key}={Uri.EscapeDataString(value)}");
            }
        }
    }

    /// <summary>Parses a <c>status</c> query value into a run disposition, tolerantly: an absent, unknown, or numeric
    /// value (an ordinal like <c>3</c> is rejected, matching the API) reads as "no filter".</summary>
    public static RunStatus? ParseStatus(string? raw) =>
        !string.IsNullOrEmpty(raw)
        && !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
        && Enum.TryParse<RunStatus>(raw, ignoreCase: true, out var status)
            ? status
            : null;

    /// <summary>Parses a <c>payloadId</c> query value into a payload UUID, or null when absent/malformed.</summary>
    public static Guid? ParsePayloadId(string? raw) =>
        Guid.TryParse(raw, out var payloadId) ? payloadId : null;

    /// <summary>Parses a <c>from</c>/<c>to</c> ISO-8601 query bound into a UTC instant, or null when absent/unparseable
    /// (an unparseable bound degrades to unbounded, exactly as the API treats it).</summary>
    public static DateTimeOffset? ParseInstant(string? raw) =>
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant)
            ? instant
            : null;

    /// <summary>Parses a <c>page</c> query value into a 1-based page number, flooring at 1 — an absent, non-numeric, or
    /// non-positive value reads as the first page (mirroring the API).</summary>
    public static int ParsePage(string? raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var page) && page > 0 ? page : 1;

    /// <summary>Parses a <c>size</c> query value into a page size clamped to 1..100, or null when absent/non-numeric so
    /// the API applies its default (25).</summary>
    public static int? ParseSize(string? raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? Math.Clamp(size, 1, 100) : null;

    /// <summary>Whether any list filter is applied — drives the empty-state copy ("no runs match" vs "no runs yet"). A
    /// status, payload, or either time bound counts.</summary>
    public static bool HasFilter(RunStatus? status, Guid? payloadId, DateTimeOffset? from, DateTimeOffset? to) =>
        status is not null || payloadId is not null || from is not null || to is not null;
}
