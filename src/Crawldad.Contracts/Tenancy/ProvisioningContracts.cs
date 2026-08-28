namespace Crawldad.Contracts.Tenancy;

/// <summary>The body of <c>POST /provisioning/tenants</c> — the self-serve free-tier workspace-creation call (issue #119
/// PR7). Everything is defaulted: the acting user is the console selector header (never a body field, so a caller can only
/// ever provision for its own verified identity), the tier/slot allowance are the free-tier defaults, and no API key is
/// minted. The one optional field is a human <see cref="DisplayName"/> for the new workspace; blank/absent falls back to a
/// generic default server-side. The response is the created <see cref="WorkspaceSummary"/> (tenant id, display name, and the
/// creator's <c>Owner</c> role) — the same shape <c>GET /workspaces</c> returns, so the portal can select it immediately.</summary>
/// <param name="DisplayName">An optional human name for the new workspace (at most 200 chars). Blank/absent → a default.</param>
public sealed record ProvisionTenantRequest(string? DisplayName = null);
