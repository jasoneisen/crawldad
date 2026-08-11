using Crawldad.Web.Infrastructure.Security;
using Crawldad.Web.Infrastructure.Storage;
using Marten;
using Microsoft.Extensions.Options;

namespace Crawldad.Web.Features.Runs;

/// <summary>The result-retention sweep (issue #71): ages an async run's stored result body out of the executor-owned
/// <see cref="RunProgress"/> document — the one PII surface the blob janitor never reached, since <c>RunProgress</c> is a
/// Marten row, not a blob. Registered as an <see cref="IRetentionSweep"/> so the shared <see cref="RetentionJanitor"/>
/// drives it on the same cadence/policy as the blob stores.
///
/// <para><b>What expires:</b> the terminal run's <see cref="RunProgress.ResultJson"/>/<see cref="RunProgress.PartialJson"/>
/// body is nulled and a <see cref="RunProgress.ResultExpiredAt"/> marker stamped — <b>not</b> the whole document. The run's
/// status/stats stay queryable via <c>GET /runs/{id}</c> (a coherent "result aged out" shape, not a surprise 404), and the
/// immutable event timeline is left untouched (its bulk-PII lives in the result body, not the ref-only trace; timeline
/// archival at expiry is issue #46's concern). On-demand full erasure is the separate <c>DELETE /runs/{id}</c> path.</para>
///
/// <para><b>Tenant-correct:</b> under conjoined multi-tenancy every row is tenant-qualified, so the sweep fans out over
/// each configured tenant under its own session (the same pattern as <c>RunRecoveryService</c>), never a cross-tenant
/// query. <b>Bounded per pass:</b> at most <see cref="BatchSize"/> rows per tenant per sweep, so a tenant with a large
/// backlog drains over successive passes rather than in one unbounded delete transaction.</para></summary>
public sealed class RunResultRetentionSweep(IDocumentStore store, TenantRegistry tenants, IOptions<StorageOptions> options) : IRetentionSweep
{
    /// <summary>The most rows one sweep expires per tenant per pass — the bound that keeps each sweep transaction small; a
    /// tenant with more due is drained over the next passes (the janitor sweeps again next interval).</summary>
    public const int BatchSize = 500;

    /// <inheritdoc />
    public async Task<int> SweepAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (options.Value.Retention.ResultTtlOrNull is not { } ttl)
        {
            return 0; // result retention disabled (TTL ≤ 0) — keep stored results indefinitely
        }

        var cutoff = now - ttl;
        var expired = 0;
        foreach (var tenantId in tenants.TenantIds)
        {
            expired += await SweepTenantAsync(tenantId, now, cutoff, ct);
        }

        return expired;
    }

    // Expires one tenant's aged results under its own tenant-scoped session: load at most BatchSize terminal rows whose
    // stored body finished before the cutoff, null the body + stamp the marker, commit once. A row with no body — a
    // failure, a queue-terminal cancel/timeout, or an already-swept result — is never selected (the body predicate), so a
    // swept row is not re-touched on the next pass and its FinishedAt clock is never disturbed.
    private async Task<int> SweepTenantAsync(string tenantId, DateTimeOffset now, DateTimeOffset cutoff, CancellationToken ct)
    {
        await using var session = store.LightweightSession(tenantId);
        var due = await session.Query<RunProgress>()
            .Where(p => (p.ResultJson != null || p.PartialJson != null) && p.FinishedAt != null && p.FinishedAt <= cutoff)
            .OrderBy(p => p.FinishedAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        foreach (var progress in due)
        {
            progress.ResultJson = null;
            progress.PartialJson = null;
            progress.ResultExpiredAt = now;
            session.Store(progress);
        }

        await session.SaveChangesAsync(ct); // a no-op commit when nothing was due
        return due.Count;
    }
}
