using System.Text;
using Marten;

namespace Crawldad.Api.Infrastructure.Security;

/// <summary>Serializes a tenant's competing guarded writes (issue #119 PR5) so a count-based invariant — "never the tenant's
/// last active key" / "never the tenant's last active Owner" — is enforced <b>atomically</b>, not check-then-act across two
/// sessions. It takes a Postgres <b>transaction-scoped advisory lock</b> on the session's connection (the same
/// re-read-under-lock idiom <see cref="Crawldad.Api.Features.Runs.RunQueue"/> uses via Marten's stream lock, applied here to
/// plain documents): concurrent revokes for one tenant queue on the lock, and each re-reads the live count only after the
/// prior one has committed, so two racing revokes can never both pass a guard that only one should. The lock is released when
/// the session's transaction commits (on <c>SaveChangesAsync</c>) or rolls back (on dispose without a save).
///
/// <para>Distinct lock <b>classes</b> per resource keep a key-revoke and a membership-revoke on the same tenant from
/// contending needlessly — they guard independent invariants.</para></summary>
internal static class TenantWriteLock
{
    /// <summary>The advisory-lock class for a tenant's API-key revocations (the last-active-key guard).</summary>
    public const int KeyRevocationClass = 0x0119_0501;

    /// <summary>The advisory-lock class for a tenant's membership revocations (the last-Owner guard).</summary>
    public const int MembershipRevocationClass = 0x0119_0502;

    /// <summary>Begins the session's transaction and takes the transaction-scoped advisory lock for
    /// <paramref name="tenantId"/> under <paramref name="lockClass"/>, blocking until it is held. Every read and write the
    /// caller then performs on <paramref name="session"/> runs inside that transaction, and committing it (or disposing the
    /// session) releases the lock.</summary>
    public static async Task AcquireAsync(IDocumentSession session, int lockClass, string tenantId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);

        // BeginTransaction opens the connection and starts the transaction the advisory lock (and the later SaveChanges) ride
        // on; the lock is xact-scoped, so it lives exactly as long as this transaction.
        await session.BeginTransactionAsync(ct);
        await using var command = session.Connection.CreateCommand();
        command.CommandText = "select pg_advisory_xact_lock(@class, @key)";
        command.Parameters.AddWithValue("class", lockClass);
        command.Parameters.AddWithValue("key", StableKey(tenantId));
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>A stable 32-bit advisory-lock key for a tenant id (FNV-1a over its UTF-8 bytes), so the same tenant maps to
    /// the same lock slot across every process — unlike <see cref="string.GetHashCode()"/>, which is per-run randomized.</summary>
    internal static int StableKey(string tenantId)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        unchecked
        {
            const uint offsetBasis = 2166136261;
            const uint prime = 16777619;
            var hash = offsetBasis;
            foreach (var b in Encoding.UTF8.GetBytes(tenantId))
            {
                hash ^= b;
                hash *= prime;
            }

            return (int)hash;
        }
    }
}
