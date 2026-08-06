namespace Crawldad.Web.Infrastructure.Browser;

/// <summary>
/// How to reach a browser backend for one run: the adapter id (e.g. <c>"browserless"</c>, <c>"browserbase"</c>,
/// <c>"fake"</c>), a <em>reference</em> to a credential in the secret store (never the raw secret — §12), and an
/// opaque provider-options bag passed straight through to the adapter (<c>backendOptions</c>, §9.1). The engine
/// resolves <see cref="CredentialRef"/> to a live secret only at connect time; it is never persisted or logged.
/// </summary>
/// <param name="Adapter">The backend adapter id selecting which <see cref="IBrowserBackend"/> handles the connect.</param>
/// <param name="CredentialRef">An id into the secret store, resolved at connect time. Null for credential-free backends (the fake).</param>
/// <param name="Options">Opaque provider passthrough (proxy, stealth routes, launch args, …). Not interpreted by the engine.</param>
public sealed record BackendBinding(
    string Adapter,
    string? CredentialRef = null,
    IReadOnlyDictionary<string, object?>? Options = null);
