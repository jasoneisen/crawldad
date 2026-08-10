namespace Crawldad.Web.Infrastructure.Browser;

/// <summary>How to reach a browser backend for one run: the adapter id, a reference to a credential (resolved to a live
/// secret only at connect time, tenant-scoped; never persisted or logged), an opaque provider-options bag passed
/// straight through to the adapter, and the run's <see cref="Tenant"/> — the scope the credentialRef resolves within.</summary>
public sealed record BackendBinding(
    string Adapter,
    string? CredentialRef = null,
    IReadOnlyDictionary<string, object?>? Options = null,
    string? Tenant = null);
