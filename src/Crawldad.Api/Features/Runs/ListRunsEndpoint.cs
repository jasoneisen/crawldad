using System.Globalization;
using Crawldad.Contracts.Runs;
using Marten;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace Crawldad.Api.Features.Runs;

/// <summary><c>GET /runs</c>: the tenant's runs, newest first, offset-paginated and filterable. Reads the lightweight
/// <see cref="RunSummary"/> listing projection (lag-tolerant, like every cross-run dashboard read), never the per-run
/// progress or the full timeline. Tenant isolation holds by construction — the request's Marten session is scoped to the
/// authenticated tenant, so the query only ever sees this tenant's rows.
///
/// <para>Filters (all optional, all AND-combined): <c>?status=</c> (a run disposition name — an unknown value is a
/// <c>400 invalid_status</c>, so a typo never silently returns the wrong set), <c>?payloadId=</c> (a managed payload's
/// UUID — a malformed value is a <c>400 invalid_payload_id</c>), and <c>?from=</c>/<c>?to=</c> (an inclusive ISO-8601
/// <c>startedAt</c> range — an unparseable bound is ignored rather than rejected, so a dashboard's date picker degrades
/// gracefully). Paging is <c>?page=</c> (1-based, default 1) and <c>?size=</c> (default 25, clamped to 1..100); a stray
/// value clamps to the default. The response carries the filtered <c>total</c> and a <c>hasMore</c> flag.</para></summary>
public static class ListRunsEndpoint
{
    /// <summary>The default page size when none is supplied.</summary>
    public const int DefaultPageSize = 25;

    /// <summary>The largest page size honoured; a larger request clamps to this.</summary>
    public const int MaxPageSize = 100;

    /// <summary>Handles <c>GET /runs</c>.</summary>
    [WolverineGet("/runs")]
    public static async Task<IResult> Handle(IQuerySession session, HttpContext http, CancellationToken ct)
    {
        var query = http.Request.Query;

        // A missing OR blank filter value reads as "no filter" (query[key] is "" when absent or empty) — a single branch,
        // so an omitted and an explicit ?status= behave the same.
        RunStatus? status = null;
        var rawStatus = query["status"].ToString();
        if (!string.IsNullOrEmpty(rawStatus))
        {
            if (!TryParseStatus(rawStatus, out var parsed))
            {
                return Results.BadRequest(new RunRejection("invalid_status", $"'{rawStatus}' is not a run status (running/queued/succeeded/failed/cancelled)"));
            }

            status = parsed;
        }

        Guid? payloadId = null;
        var rawPayloadId = query["payloadId"].ToString();
        if (!string.IsNullOrEmpty(rawPayloadId))
        {
            if (!Guid.TryParse(rawPayloadId, out var parsed))
            {
                return Results.BadRequest(new RunRejection("invalid_payload_id", $"'{rawPayloadId}' is not a payload id (UUID)"));
            }

            payloadId = parsed;
        }

        var page = Math.Max(1, ParseInt(query, "page", 1));
        var size = Math.Clamp(ParseInt(query, "size", DefaultPageSize), 1, MaxPageSize);

        var filtered = Filter(session.Query<RunSummary>(), status, payloadId, ParseInstant(query, "from"), ParseInstant(query, "to"));
        var total = await filtered.CountAsync(ct);

        // Long math so an extreme ?page= cannot overflow int into a negative Skip (a 500) — a page past the end simply
        // returns empty. total is an int count, so once the skip clears it the (int) cast below is in range.
        var skip = (long)(page - 1) * size;
        IReadOnlyList<RunSummary> rows = skip >= total
            ? []
            : await filtered
                .OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id)
                .Skip((int)skip).Take(size)
                .ToListAsync(ct);

        return Results.Ok(new RunListResponse([.. rows.Select(ToItem)], page, size, total, (long)page * size < total));
    }

    // Applies the optional filters, AND-combined. from/to bound startedAt inclusively.
    private static IQueryable<RunSummary> Filter(IQueryable<RunSummary> query, RunStatus? status, Guid? payloadId, DateTimeOffset? from, DateTimeOffset? to)
    {
        if (status is { } s)
        {
            query = query.Where(r => r.Status == s);
        }

        if (payloadId is { } pid)
        {
            query = query.Where(r => r.PayloadId == pid);
        }

        if (from is { } f)
        {
            query = query.Where(r => r.StartedAt >= f);
        }

        if (to is { } t)
        {
            query = query.Where(r => r.StartedAt <= t);
        }

        return query;
    }

    /// <summary>Maps a stored summary to its list row: the pinned payload identity (or the inline marker) and the
    /// terminal-only fields (duration, headline stats, failure class/code) that a running/queued row simply omits.</summary>
    internal static RunListItem ToItem(RunSummary summary) => new(
        summary.Id,
        summary.Status,
        summary.StartedAt,
        summary.DurationMs,
        summary.Failure is null ? null : new RunListFailure(summary.Failure.Class, summary.Failure.Code),
        summary.PayloadName,
        summary.PayloadId,
        summary.PayloadRevision,
        summary.PayloadId is null,
        summary.Region,
        summary.Stats is null ? null : new RunListStats(summary.Stats.Steps, summary.Stats.Requests, summary.Stats.SelectorMisses));

    // A run-status name (case-insensitive), never its ordinal — "?status=3" is rejected so a numeric typo can't select a
    // status by accident. An unknown name returns false, which the endpoint surfaces as 400 invalid_status.
    private static bool TryParseStatus(string raw, out RunStatus status)
    {
        // Reject a numeric first (Enum.TryParse would otherwise accept an ordinal like "3"), then match a status name
        // case-insensitively. A non-numeric that isn't a defined name fails Enum.TryParse, so no separate IsDefined guard is needed.
        status = default;
        return !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            && Enum.TryParse(raw, ignoreCase: true, out status);
    }

    // A tolerant ISO-8601 instant filter bound: a present-but-unparseable value reads as absent (no filter) rather than
    // rejecting the whole request — a dashboard's date range should degrade to "unbounded", not 400.
    private static DateTimeOffset? ParseInstant(IQueryCollection query, string key) =>
        query.TryGetValue(key, out var raw) && DateTimeOffset.TryParse(raw.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
            ? value
            : null;

    // A tolerant positive-integer query value: absent, non-numeric, or out of range falls back to the default (the caller
    // clamps size to its bounds and floors page at 1).
    private static int ParseInt(IQueryCollection query, string key, int fallback) =>
        query.TryGetValue(key, out var raw) && int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
}
