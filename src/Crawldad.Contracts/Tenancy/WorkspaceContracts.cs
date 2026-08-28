namespace Crawldad.Contracts.Tenancy;

/// <summary>One workspace the signed-in user can act as (issue #119 PR6): a tenant they hold an active membership in,
/// with the display name for the switcher and their role there. Returned by <c>GET /workspaces</c> — the cross-tenant
/// "which workspaces are mine" read that backs the portal's workspace switcher (multi-workspace, decision addendum #3).
/// "Workspace" is the customer-facing term; the API/infra term is "tenant" (the <see cref="TenantId"/>).</summary>
/// <param name="TenantId">The workspace's tenant id (the value the <c>X-Crawldad-Workspace</c> selector carries).</param>
/// <param name="DisplayName">The workspace's human name, for the switcher label.</param>
/// <param name="Role">The signed-in user's role in this workspace.</param>
public sealed record WorkspaceSummary(string TenantId, string DisplayName, MembershipRole Role);

/// <summary>The <c>GET /workspaces</c> response: every workspace the authenticated user is an active member of, newest
/// membership first. The switcher lists these; an empty list means the console principal has no memberships.</summary>
/// <param name="Workspaces">The user's workspaces.</param>
public sealed record WorkspaceList(IReadOnlyList<WorkspaceSummary> Workspaces);
