using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Crawldad.Contracts.Runs;

namespace Crawldad.Client;

/// <summary>Runs surface: create (sync + async), poll, cancel, erase, replay, timeline, drift, screenshot fetch, and
/// the tenant queue stats. The SSE trace stream lives in the events partial.</summary>
public sealed partial class CrawldadClient
{
    private const string _screenshotsPrefix = "screenshots/";
    private static readonly JsonElement _emptyObject = ParseEmptyObject();

    /// <summary>Starts a run (<c>POST /runs</c>). A default (synchronous) request returns a terminal
    /// <see cref="StartRunResult.Completed"/> on <c>200</c>; an <c>async</c> request — or a synchronous run auto-upgraded
    /// past the sync window, or one queued behind the concurrency cap — returns <see cref="StartRunResult.Accepted"/> on
    /// <c>202</c>. Any <see cref="StartRunRequest.Payload"/>/<see cref="StartRunRequest.Inputs"/> left as a default
    /// (<c>Undefined</c>) <see cref="JsonElement"/> is sent as an empty object.</summary>
    /// <param name="request">The run request — an inline payload, or a pinned managed payload by id/revision.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The synchronous terminal response or the accepted async state.</returns>
    /// <exception cref="CrawldadRunRejectedException">An unrunnable pinned reference (<c>400</c>) or the queue is full (<c>429</c>).</exception>
    public Task<StartRunResult> CreateRunAsync(StartRunRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = request with { Payload = OrEmptyObject(request.Payload), Inputs = OrEmptyObject(request.Inputs) };
        return StartRunCoreAsync("runs", normalized, ct);
    }

    /// <summary>Starts a run from an <b>inline</b> payload document (<c>POST /runs</c>).</summary>
    /// <param name="payload">The inline Crawldad payload document.</param>
    /// <param name="inputs">The run inputs (backend binding, parameters); omit for none.</param>
    /// <param name="async">True to return immediately (<c>202</c>) and run in the background.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The synchronous terminal response or the accepted async state.</returns>
    public Task<StartRunResult> CreateInlineRunAsync(JsonElement payload, JsonElement? inputs = null, bool async = false, CancellationToken ct = default) =>
        CreateRunAsync(new StartRunRequest(payload, inputs ?? default, PayloadId: null, Revision: null, async), ct);

    /// <summary>Starts a run from a <b>pinned managed payload</b> (<c>POST /runs</c>): the exact stored revision (or the
    /// head when <paramref name="revision"/> is null) is executed with the supplied inputs.</summary>
    /// <param name="payloadId">The managed payload id.</param>
    /// <param name="revision">The pinned revision, or null for the current head.</param>
    /// <param name="inputs">The run inputs; omit for none.</param>
    /// <param name="async">True to return immediately (<c>202</c>) and run in the background.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The synchronous terminal response or the accepted async state.</returns>
    /// <exception cref="CrawldadRunRejectedException">The payload/revision is unknown or archived (<c>400</c>), or the queue is full (<c>429</c>).</exception>
    public Task<StartRunResult> CreatePinnedRunAsync(Guid payloadId, int? revision = null, JsonElement? inputs = null, bool async = false, CancellationToken ct = default) =>
        CreateRunAsync(new StartRunRequest(default, inputs ?? default, payloadId, revision, async), ct);

    /// <summary>Polls a run's state (<c>GET /runs/{id}</c>): <c>queued</c> (with a live 1-based <c>position</c>),
    /// <c>running</c>, then the terminal disposition with the scrubbed result/failure/partial and stats. A purely
    /// synchronous run writes no progress row, so it is a <see cref="CrawldadNotFoundException"/>.</summary>
    /// <param name="runId">The run id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The run's current state.</returns>
    /// <exception cref="CrawldadNotFoundException">No such run for this tenant (<c>404</c>).</exception>
    public Task<RunStateResponse> GetRunAsync(Guid runId, CancellationToken ct = default) =>
        GetAsync<RunStateResponse>($"runs/{runId}", ct);

    /// <summary>Cancels a background run (<c>POST /runs/{id}/cancel</c>): a running run gets a cooperative cancel, a
    /// queued run is dequeued straight to <c>cancelled</c>. Returns the pre-cancel acknowledgement.</summary>
    /// <param name="runId">The run id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The acknowledged run state.</returns>
    /// <exception cref="CrawldadNotFoundException">No such run for this tenant (<c>404</c>).</exception>
    public Task<RunStateResponse> CancelRunAsync(Guid runId, CancellationToken ct = default) =>
        PostAsync<RunStateResponse>($"runs/{runId}/cancel", ct);

    /// <summary>Erases a <b>finished</b> run (<c>DELETE /runs/{id}</c>): its stored result, derived read models, and
    /// event stream, in one transaction — the right-to-erasure path. Idempotent (a repeat is a
    /// <see cref="CrawldadNotFoundException"/>).</summary>
    /// <param name="runId">The run id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <exception cref="CrawldadNotFoundException">No such (terminal) run for this tenant (<c>404</c>).</exception>
    /// <exception cref="CrawldadRunRejectedException">The run is still <c>running</c>/<c>queued</c> (<c>409 run_still_active</c>) — cancel it first.</exception>
    public Task EraseRunAsync(Guid runId, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"runs/{runId}", ct);

    /// <summary>Replays a run's pinned payload revision (<c>POST /runs/{id}/replay</c>) with resupplied inputs — a fresh
    /// run pinning the exact same revision, so results stay drift-comparable. Only a run that pinned a managed payload is
    /// replayable.</summary>
    /// <param name="runId">The run to replay.</param>
    /// <param name="request">The resupplied inputs and sync/async choice.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The synchronous terminal response or the accepted async state of the new run.</returns>
    /// <exception cref="CrawldadNotFoundException">No such run for this tenant (<c>404</c>).</exception>
    /// <exception cref="CrawldadRunRejectedException">The run executed an inline payload (<c>400 inline_not_replayable</c>), or the queue is full (<c>429</c>).</exception>
    public Task<StartRunResult> ReplayRunAsync(Guid runId, ReplayRunRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = request with { Inputs = OrEmptyObject(request.Inputs) };
        return StartRunCoreAsync($"runs/{runId}/replay", normalized, ct);
    }

    /// <summary>Replays a run (<c>POST /runs/{id}/replay</c>) with the supplied inputs.</summary>
    /// <param name="runId">The run to replay.</param>
    /// <param name="inputs">The resupplied inputs; omit for none.</param>
    /// <param name="async">True to return immediately (<c>202</c>) and run in the background.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The synchronous terminal response or the accepted async state of the new run.</returns>
    public Task<StartRunResult> ReplayRunAsync(Guid runId, JsonElement? inputs = null, bool async = false, CancellationToken ct = default) =>
        ReplayRunAsync(runId, new ReplayRunRequest(inputs ?? default, async), ct);

    /// <summary>Fetches a run's timeline (<c>GET /runs/{id}/timeline</c>): the ordered step list with durations,
    /// redacted input key names, extracted/blob refs, region, and the failure + its screenshot/capture refs.</summary>
    /// <param name="runId">The run id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The run timeline.</returns>
    /// <exception cref="CrawldadNotFoundException">No such run for this tenant (<c>404</c>).</exception>
    public Task<RunTimelineResponse> GetRunTimelineAsync(Guid runId, CancellationToken ct = default) =>
        GetAsync<RunTimelineResponse>($"runs/{runId}/timeline", ct);

    /// <summary>Reports a run's pinned-payload-revision drift vs. the payload's current head (<c>GET /runs/{id}/drift</c>).
    /// An inline run never drifts.</summary>
    /// <param name="runId">The run id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The drift comparison.</returns>
    /// <exception cref="CrawldadNotFoundException">No such run for this tenant (<c>404</c>).</exception>
    public Task<RunDriftResponse> GetRunDriftAsync(Guid runId, CancellationToken ct = default) =>
        GetAsync<RunDriftResponse>($"runs/{runId}/drift", ct);

    /// <summary>Fetches a captured screenshot by ref (<c>GET /runs/{id}/screenshots/{reference}</c>). The
    /// <paramref name="reference"/> is the timeline's <c>screenshotRef</c> either bare (<c>{sha}.png</c>) or with its
    /// <c>screenshots/</c> prefix — both are accepted. Authorization is by run association: a foreign or expired ref is a
    /// <see cref="CrawldadNotFoundException"/>.</summary>
    /// <param name="runId">The run the screenshot belongs to.</param>
    /// <param name="reference">The screenshot ref (bare or prefixed).</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The PNG bytes with content type and ETag.</returns>
    /// <exception cref="CrawldadNotFoundException">Unknown run/ref, or the screenshot expired (<c>404</c>).</exception>
    public async Task<ScreenshotContent> GetRunScreenshotAsync(Guid runId, string reference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);
        var bare = reference.StartsWith(_screenshotsPrefix, StringComparison.Ordinal) ? reference[_screenshotsPrefix.Length..] : reference;

        using var request = BuildRequest(HttpMethod.Get, $"runs/{runId}/screenshots/{bare}");
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateErrorAsync(response, ct);
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/png";
        var etag = response.Headers.ETag?.Tag;
        return new ScreenshotContent(bytes, contentType, etag);
    }

    /// <summary>Reads the tenant's admission-queue stats (<c>GET /runs/queue-stats</c>): current depth and the p95 queue
    /// wait.</summary>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The queue stats snapshot.</returns>
    public Task<QueueStatsResponse> GetQueueStatsAsync(CancellationToken ct = default) =>
        GetAsync<QueueStatsResponse>("runs/queue-stats", ct);

    // POSTs a run-start/replay body and resolves the 200-terminal / 202-accepted dichotomy into a StartRunResult.
    private async Task<StartRunResult> StartRunCoreAsync(string relativePath, object body, CancellationToken ct)
    {
        using var request = BuildRequest(HttpMethod.Post, relativePath, JsonBody(body));
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateErrorAsync(response, ct);
        }

        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            var state = await response.Content.ReadFromJsonAsync<RunStateResponse>(CrawldadJson.Options, ct);
            return new StartRunResult(null, state ?? throw EmptyBody(response));
        }

        var run = await response.Content.ReadFromJsonAsync<RunResponse>(CrawldadJson.Options, ct);
        return new StartRunResult(run ?? throw EmptyBody(response), null);
    }

    private static CrawldadApiException EmptyBody(HttpResponseMessage response) =>
        new((int)response.StatusCode, "The Crawldad API returned an empty response body.", null);

    private static JsonElement OrEmptyObject(JsonElement value) =>
        value.ValueKind == JsonValueKind.Undefined ? _emptyObject : value;

    private static JsonElement ParseEmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}
