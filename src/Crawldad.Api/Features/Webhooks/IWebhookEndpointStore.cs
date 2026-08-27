using Crawldad.Contracts.Webhooks;
using Marten;
using Microsoft.AspNetCore.DataProtection;

namespace Crawldad.Api.Features.Webhooks;

/// <summary>A resolved endpoint for delivery: the target URL, the decrypted signing secret (in-memory only, to compute
/// the HMAC), and the subscribed event set. Never persisted, never logged.</summary>
public sealed record ResolvedWebhookEndpoint(string Url, string Secret, IReadOnlyList<string> Events);

/// <summary>The tenant-scoped webhook-endpoint store behind the webhooks API and delivery. The CRUD methods take the
/// tenant explicitly (the authenticated principal's) and open their own tenant-scoped session; the delivery-side methods
/// take an already-tenant-scoped session (the Wolverine handler's). A tenant can only ever register, list, delete, or
/// resolve its own endpoints. Signing secrets are encrypted at rest and never returned.</summary>
public interface IWebhookEndpointStore
{
    /// <summary>Registers (or replaces) <paramref name="name"/> for <paramref name="tenant"/>, encrypting the secret at
    /// rest; a replace keeps the original <c>createdAt</c>. Returns the stored metadata — never the secret.</summary>
    Task<WebhookSummary> RegisterAsync(string tenant, string name, string url, string secret, IReadOnlyList<string> events, CancellationToken ct);

    /// <summary>Lists the tenant's registered endpoints (secrets omitted), ordered by name for a deterministic response.</summary>
    Task<IReadOnlyList<WebhookSummary>> ListAsync(string tenant, CancellationToken ct);

    /// <summary>Lists the tenant's endpoints on an already-tenant-scoped <paramref name="session"/> (the fan-out handler's).</summary>
    Task<IReadOnlyList<WebhookSummary>> ListAsync(IQuerySession session, CancellationToken ct);

    /// <summary>Deletes the tenant's <paramref name="name"/> registration; <see langword="false"/> when it did not exist.</summary>
    Task<bool> DeleteAsync(string tenant, string name, CancellationToken ct);

    /// <summary>Resolves <paramref name="name"/> for delivery on an already-tenant-scoped <paramref name="session"/> —
    /// the URL, the decrypted secret, and the event set — or <see langword="null"/> when the endpoint was deregistered
    /// since the delivery was enqueued (a drop, not an error).</summary>
    Task<ResolvedWebhookEndpoint?> ResolveAsync(IQuerySession session, string name, CancellationToken ct);
}

/// <summary>The Marten-backed <see cref="IWebhookEndpointStore"/>. Opens a tenant-scoped session per CRUD call via the
/// shared <see cref="IDocumentStore"/>, and protects the signing secret with a purpose-bound <see cref="IDataProtector"/>
/// so the stored document never holds plaintext — the same at-rest scheme the browsers slice uses.</summary>
internal sealed class MartenWebhookEndpointStore : IWebhookEndpointStore
{
    /// <summary>The Data-Protection purpose this store's protector is bound to. Purpose isolation means this key ring
    /// cannot decrypt (or be decrypted by) any other protector in the app.</summary>
    internal const string ProtectorPurpose = "Crawldad.Web.Features.Webhooks.WebhookEndpointStore.v1";

    private readonly IDocumentStore _store;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _clock;

    public MartenWebhookEndpointStore(IDocumentStore store, IDataProtectionProvider protection, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(protection);
        _store = store;
        _protector = protection.CreateProtector(ProtectorPurpose);
        _clock = clock;
    }

    public async Task<WebhookSummary> RegisterAsync(string tenant, string name, string url, string secret, IReadOnlyList<string> events, CancellationToken ct)
    {
        await using var session = _store.LightweightSession(tenant);
        var existing = await session.LoadAsync<WebhookEndpoint>(name, ct);
        var now = _clock.GetUtcNow();
        var doc = new WebhookEndpoint
        {
            Id = name,
            Url = url,
            ProtectedSecret = _protector.Protect(secret),
            Events = events,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
        };
        session.Store(doc);
        await session.SaveChangesAsync(ct);
        return Summary(doc);
    }

    public async Task<IReadOnlyList<WebhookSummary>> ListAsync(string tenant, CancellationToken ct)
    {
        await using var session = _store.QuerySession(tenant);
        return await ListAsync(session, ct);
    }

    public async Task<IReadOnlyList<WebhookSummary>> ListAsync(IQuerySession session, CancellationToken ct)
    {
        var docs = await session.Query<WebhookEndpoint>().OrderBy(static w => w.Id).ToListAsync(ct);
        return [.. docs.Select(Summary)];
    }

    public async Task<bool> DeleteAsync(string tenant, string name, CancellationToken ct)
    {
        await using var session = _store.LightweightSession(tenant);
        if (await session.LoadAsync<WebhookEndpoint>(name, ct) is null)
        {
            return false;
        }

        session.Delete<WebhookEndpoint>(name);
        await session.SaveChangesAsync(ct);
        return true;
    }

    public async Task<ResolvedWebhookEndpoint?> ResolveAsync(IQuerySession session, string name, CancellationToken ct)
    {
        var doc = await session.LoadAsync<WebhookEndpoint>(name, ct);
        return doc is null ? null : new ResolvedWebhookEndpoint(doc.Url, _protector.Unprotect(doc.ProtectedSecret), doc.Events);
    }

    private static WebhookSummary Summary(WebhookEndpoint doc) =>
        new(doc.Id, doc.Url, doc.Events, doc.CreatedAt, doc.UpdatedAt);
}
