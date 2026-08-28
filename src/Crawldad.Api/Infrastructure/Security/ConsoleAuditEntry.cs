using Marten;

namespace Crawldad.Api.Infrastructure.Security;

/// <summary>A lightweight audit record for a single <b>console-authenticated write</b> (issue #119 PR5). One row is
/// appended after each mutation the portal performs through the console credential — never for a read, and never for a
/// programmatic <c>ApiKey</c> write (attribution follows the channel: the console path carries the human email). It is
/// deliberately minimal — who, what, when, and how it turned out — and holds <b>no payload bodies and no secrets</b>: just
/// the tenant, the actor email, the HTTP operation + route template, the response status, and the timestamp. Stored
/// single-tenanted in the <c>crawldad</c> schema (like <see cref="TenantMembership"/>), tenant-scoped by the
/// <see cref="TenantId"/> field.</summary>
public sealed class ConsoleAuditEntry
{
    /// <summary>The audit record id (document id).</summary>
    public Guid Id { get; set; }

    /// <summary>The tenant the console write acted on (<see cref="RegistryTenant.Id"/>).</summary>
    public string TenantId { get; set; } = "";

    /// <summary>The authenticated human's normalized email (the console actor) — never a credential.</summary>
    public string Email { get; set; } = "";

    /// <summary>The HTTP operation (verb) — e.g. <c>POST</c>, <c>PUT</c>, <c>DELETE</c>.</summary>
    public string Operation { get; set; } = "";

    /// <summary>The route <b>template</b> the write hit — e.g. <c>/payloads/{id}/revise</c>. The template, not the concrete
    /// path, so no id or name is recorded (low-cardinality, and nothing that could be sensitive).</summary>
    public string Route { get; set; } = "";

    /// <summary>The response status code — the write's outcome (a <c>2xx</c> success, or the <c>4xx</c> it was refused with).</summary>
    public int StatusCode { get; set; }

    /// <summary>When the write completed (UTC).</summary>
    public DateTimeOffset At { get; set; }
}

/// <summary>The persistence seam over the <see cref="ConsoleAuditEntry"/> documents. Split out from Marten (mirroring the
/// registry/membership stores) so the middleware is unit-testable against a fake and the Marten wiring is exercised end to
/// end. Appending is fire-and-forget from the caller's perspective — a store fault must never fail the write it records.</summary>
public interface IConsoleAuditStore
{
    /// <summary>Appends one audit row for a completed console write.</summary>
    Task RecordAsync(ConsoleAuditEntry entry, CancellationToken ct);

    /// <summary>Every audit row for the tenant, newest first — for a tenant's console-activity view (and the tests).</summary>
    Task<IReadOnlyList<ConsoleAuditEntry>> ListForTenantAsync(string tenantId, CancellationToken ct);
}

/// <summary>The Marten-backed <see cref="IConsoleAuditStore"/>. The audit documents are single-tenanted (they record
/// console authority decisions that resolve before any tenant scope), opened on the default tenant via the shared
/// <see cref="IDocumentStore"/> — the same singleton-store, session-per-call shape as <see cref="MartenTenantRegistryStore"/>.</summary>
internal sealed class MartenConsoleAuditStore(IDocumentStore store) : IConsoleAuditStore
{
    public async Task RecordAsync(ConsoleAuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var session = store.LightweightSession();
        session.Store(entry);
        await session.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ConsoleAuditEntry>> ListForTenantAsync(string tenantId, CancellationToken ct)
    {
        await using var session = store.QuerySession();
        return await session.Query<ConsoleAuditEntry>()
            .Where(e => e.TenantId == tenantId)
            .OrderByDescending(e => e.At)
            .ToListAsync(ct);
    }
}
