namespace Crawldad.Api.Features.Fixtures;

/// <summary>A tenant's recorded fixture set, stored as a plain tenant-scoped Marten document (the shared
/// <c>AllDocumentsAreMultiTenanted</c> policy qualifies every row by tenant, so a set name is unique per tenant and
/// isolation holds by construction). Holds the recorded manifest (the replayable state machine) and the
/// content-addressed page HTML inline — a bounded, host-side, server-readable asset the <c>fixture</c> replay backend
/// reads deterministically with zero live traffic. A managed resource with an explicit lifecycle (record/delete), not a
/// retention-aged artifact.</summary>
public sealed class FixtureSet
{
    /// <summary>The set name (the document id, and the value a replay names via <c>options.fixtureSet</c>). Unique per tenant.</summary>
    public string Id { get; set; } = "";

    /// <summary>The recorded manifest as JSON (initial state, each state's URL + content-hash, the transition graph) —
    /// parsed by the replay backend into the same engine the internal fixtures use.</summary>
    public string ManifestJson { get; set; } = "";

    /// <summary>The content-addressed page HTML: SHA-256 hex → serialised document. A state's manifest <c>html</c> is its
    /// key here; identical pages dedupe to one entry.</summary>
    public IReadOnlyDictionary<string, string> Pages { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The number of distinct recorded pages (states) — surfaced in listings.</summary>
    public int PageCount { get; set; }

    /// <summary>The number of recorded transitions (clicks) — the interaction coverage a replay can follow.</summary>
    public int TransitionCount { get; set; }

    /// <summary>The total recorded HTML byte size.</summary>
    public long TotalBytes { get; set; }

    /// <summary>The record run that produced this set (for provenance; that run is not persisted as a queryable run).</summary>
    public Guid RunId { get; set; }

    /// <summary>When the set was recorded (a record overwrite re-stamps it — a set is immutable except by re-record/delete).</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
