namespace Crawldad.Web.Infrastructure.Browser;

/// <summary>How to reach a browser backend for one run: the adapter id, a reference to a credential in the secret
/// store (resolved to a live secret only at connect time; never persisted or logged), and an opaque provider-options
/// bag passed straight through to the adapter.</summary>
public sealed record BackendBinding(
    string Adapter,
    string? CredentialRef = null,
    IReadOnlyDictionary<string, object?>? Options = null);
