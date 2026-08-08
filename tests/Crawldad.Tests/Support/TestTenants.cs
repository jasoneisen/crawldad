using Alba;
using Microsoft.AspNetCore.Http;

namespace Crawldad.Tests.Support;

/// <summary>
/// The two tenants every integration host is configured with (CD-1). The <b>primary</b> tenant is the default identity every
/// Alba scenario presents (via <see cref="AuthenticatedAsPrimaryTenant"/>), so the existing suite keeps passing unchanged
/// with an authenticated caller; the <b>secondary</b> tenant is a second valid key the cross-tenant isolation test uses to
/// prove one tenant cannot reach another's data. Keys are ≥ the registry's minimum length. This mirrors the config a real
/// deployment binds under <c>Crawldad:Tenants</c>.
/// </summary>
internal static class TestTenants
{
    public const string PrimaryId = "tenant-alpha";
    public const string PrimaryKey = "alpha-key-0123456789abcdef";
    public const string PrimaryActor = "alpha@crawldad.test";

    public const string SecondaryId = "tenant-beta";
    public const string SecondaryKey = "beta-key-0123456789abcdef";
    public const string SecondaryActor = "beta@crawldad.test";

    /// <summary>The tenant white-box interpreter harnesses run under (its value is immaterial to those tests — it only
    /// scopes the storage-fake partition).</summary>
    public const string InterpreterTenant = PrimaryId;

    /// <summary>The <c>Crawldad:Tenants</c> settings injected into every test host so the registry admits both tenants.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Configuration { get; } =
    [
        new("Crawldad:Tenants:0:Id", PrimaryId),
        new("Crawldad:Tenants:0:ApiKey", PrimaryKey),
        new("Crawldad:Tenants:0:Actor", PrimaryActor),
        new("Crawldad:Tenants:1:Id", SecondaryId),
        new("Crawldad:Tenants:1:ApiKey", SecondaryKey),
        new("Crawldad:Tenants:1:Actor", SecondaryActor),
    ];

    /// <summary>The bearer header value presenting a tenant's key (what an Alba scenario or a raw client sets).</summary>
    /// <param name="apiKey">The tenant's API key.</param>
    public static string Bearer(string apiKey) => $"Bearer {apiKey}";

    /// <summary>Makes every Alba scenario on <paramref name="host"/> present the primary tenant's key by default, so the
    /// existing request-based tests authenticate without change; individual scenarios override with their own header (a
    /// different tenant, or none) since a scenario's setup runs after this BeforeEach.</summary>
    /// <param name="host">The built Alba host.</param>
    /// <returns>The same host, for chaining.</returns>
    public static IAlbaHost AuthenticatedAsPrimaryTenant(this IAlbaHost host) =>
        host.BeforeEach(context => context.Request.Headers.Authorization = Bearer(PrimaryKey));
}
