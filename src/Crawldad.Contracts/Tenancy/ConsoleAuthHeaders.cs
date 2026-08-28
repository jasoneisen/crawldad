namespace Crawldad.Contracts.Tenancy;

/// <summary>The two selector headers the console (trusted-subsystem) auth path carries alongside the portal's first-party
/// bearer token (issue #119 PR4). Shared here so the SDK's <c>ConsoleCredential</c> stamps <b>exactly</b> the header names
/// the API's <c>ConsolePrincipal</c> handler reads — one wire contract, no drift. They are <b>selectors, not
/// capabilities</b>: the API honours them only on a request whose bearer already validated as the portal identity, and
/// only to name an <i>already-granted</i> <c>(email, workspace)</c> membership — a forged header grants nothing the
/// membership store has not already granted.</summary>
public static class ConsoleAuthHeaders
{
    /// <summary>Names the verified portal user acting on this request — the normalized email
    /// (<see cref="Crawldad.Contracts.EmailAddress.Normalize"/>). The API re-normalizes it before the membership lookup, so
    /// the header's casing is never trusted.</summary>
    public const string ConsoleUser = "X-Crawldad-Console-User";

    /// <summary>Names the active workspace — the tenant GUID id the request acts as. Explicit because one user may hold
    /// memberships in many workspaces; the API resolves the membership for exactly this <c>(user, workspace)</c> pair.</summary>
    public const string Workspace = "X-Crawldad-Workspace";
}
