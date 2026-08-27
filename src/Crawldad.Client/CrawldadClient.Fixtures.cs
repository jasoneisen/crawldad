using Crawldad.Contracts.Fixtures;

namespace Crawldad.Client;

/// <summary>Fixture-set surface: record, list, get (with manifest), and delete. A recorded set lets a later run replay
/// against banked page states deterministically.</summary>
public sealed partial class CrawldadClient
{
    /// <summary>Records a fixture set (<c>POST /fixtures/{name}/record</c>): executes the payload against its configured
    /// backend while banking each page state into the named set. Shaped like a run — a failed record run is still a
    /// <c>200</c> response carrying the classified failure (and no set persisted).</summary>
    /// <param name="name">The fixture-set name (a slug); replaces any prior set of that name on success.</param>
    /// <param name="request">The inline payload and inputs to record.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The record run's disposition, the recorded summary (on success), and the run result/failure.</returns>
    /// <exception cref="CrawldadValidationException">Invalid name slug (<c>400</c>).</exception>
    public Task<RecordFixtureResponse> RecordFixtureAsync(string name, RecordFixtureRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(request);
        var normalized = request with { Payload = OrEmptyObject(request.Payload), Inputs = OrEmptyObject(request.Inputs) };
        return SendJsonAsync<RecordFixtureResponse>(HttpMethod.Post, $"fixtures/{Uri.EscapeDataString(name)}/record", normalized, ct);
    }

    /// <summary>Lists the tenant's recorded fixture sets (<c>GET /fixtures</c>) — counts and metadata, never page HTML.</summary>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The fixture listing.</returns>
    public Task<FixtureListResponse> ListFixturesAsync(CancellationToken ct = default) =>
        GetAsync<FixtureListResponse>("fixtures", ct);

    /// <summary>Fetches a fixture set's summary plus its recorded manifest (<c>GET /fixtures/{name}</c>). Page HTML is
    /// referenced by content hash only, never surfaced.</summary>
    /// <param name="name">The fixture-set name.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The set summary and manifest.</returns>
    /// <exception cref="CrawldadNotFoundException">No such fixture set for this tenant (<c>404</c>).</exception>
    public Task<FixtureDetailResponse> GetFixtureAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return GetAsync<FixtureDetailResponse>($"fixtures/{Uri.EscapeDataString(name)}", ct);
    }

    /// <summary>Deletes a fixture set (<c>DELETE /fixtures/{name}</c>): the manifest and all its page HTML in one
    /// transaction.</summary>
    /// <param name="name">The fixture-set name.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <exception cref="CrawldadNotFoundException">No such fixture set for this tenant (<c>404</c>).</exception>
    public Task DeleteFixtureAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return SendNoContentAsync(HttpMethod.Delete, $"fixtures/{Uri.EscapeDataString(name)}", ct);
    }
}
