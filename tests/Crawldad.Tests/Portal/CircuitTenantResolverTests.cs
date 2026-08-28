using System.Security.Claims;
using Crawldad.Contracts.Runs;
using Crawldad.Portal.Auth;
using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Client;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;

namespace Crawldad.Tests.Portal;

/// <summary>Unit tests for <see cref="CircuitTenantResolver"/> in isolation (fake auth-state provider + fake store +
/// stub HttpClient factory): a linked circuit user gets a client authenticated as their tenant; an unauthenticated,
/// unlinked, or claim-less circuit is a clean not-linked state (never a client with an empty key); and the email is
/// normalized byte-for-byte the same way the OTP sign-in stores it, so a circuit lands on the same link as a request.</summary>
public class CircuitTenantResolverTests
{
    private const string _apiKey = "sk_tenant_LEAKME_9876543210";
    private static readonly EphemeralDataProtectionProvider _protection = new();

    private static PortalTenantLink LinkFor(string email, string tenantId) => new()
    {
        Email = email,
        TenantId = tenantId,
        ProtectedApiKey = PortalTenancy.ApiKeyProtector(_protection).Protect(_apiKey),
    };

    private static FakeAuthStateProvider AuthenticatedAs(string? email)
    {
        Claim[] claims = email is null ? [] : [new Claim(ClaimTypes.Email, email)];
        return new FakeAuthStateProvider(new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "TestCookie")));
    }

    private static FakeAuthStateProvider Anonymous() =>
        new FakeAuthStateProvider(new ClaimsPrincipal(new ClaimsIdentity()));

    private static (CircuitTenantResolver Resolver, StubHttpMessageHandler Handler) ResolverFor(
        AuthenticationStateProvider authState, FakeLinkStore store)
    {
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Json(new QueueStatsResponse(4, 0, 0)));
        var factory = new StubHttpClientFactory(handler, new Uri("https://api.crawldad.test/"));
        return (new CircuitTenantResolver(authState, store, new FakeSelectionStore(), _protection, factory), handler);
    }

    [Fact]
    public async Task Resolves_a_linked_circuit_user_with_a_client_authenticated_as_the_tenant()
    {
        var store = new FakeLinkStore { Link = LinkFor("user@example.com", "tenant-77") };
        var (resolver, handler) = ResolverFor(AuthenticatedAs("user@example.com"), store);

        var tenant = await resolver.TryResolveAsync();

        tenant.ShouldNotBeNull();
        tenant.TenantId.ShouldBe("tenant-77");
        (await tenant.Client.GetQueueStatsAsync()).Queued.ShouldBe(4); // the client actually calls the API...
        handler.Last.Authorization.ShouldBe($"Bearer {_apiKey}");       // ...bearing the tenant's decrypted key
    }

    [Fact]
    public async Task Normalizes_the_claim_email_exactly_like_the_otp_sign_in()
    {
        // The link is keyed by the OTP-normalized identity; a differently-cased/spaced claim must still find it.
        const string StoredIdentity = "user@example.com";
        var store = new FakeLinkStore { Link = LinkFor(StoredIdentity, "tenant-9") };
        var (resolver, _) = ResolverFor(AuthenticatedAs("  User@Example.COM  "), store);

        var tenant = await resolver.TryResolveAsync();

        tenant.ShouldNotBeNull();
        // The store was queried with the OTP-normalized form — parity with PortalAuthService.NormalizeEmail, byte-for-byte.
        store.LastQueriedEmail.ShouldBe(StoredIdentity);
        store.LastQueriedEmail.ShouldBe(PortalAuthService.NormalizeEmail("  User@Example.COM  "));
    }

    [Fact]
    public async Task Authenticated_but_unlinked_circuit_is_not_linked()
    {
        var (resolver, _) = ResolverFor(AuthenticatedAs("nolink@example.com"), new FakeLinkStore { Link = null });

        (await resolver.TryResolveAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task Anonymous_circuit_is_not_linked_and_never_touches_the_store()
    {
        var store = new FakeLinkStore { Link = LinkFor("x@example.com", "t") };
        var (resolver, _) = ResolverFor(Anonymous(), store);

        (await resolver.TryResolveAsync()).ShouldBeNull();
        store.GetCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Authenticated_without_an_email_claim_is_not_linked_and_never_touches_the_store()
    {
        var store = new FakeLinkStore { Link = LinkFor("x@example.com", "t") };
        var (resolver, _) = ResolverFor(AuthenticatedAs(email: null), store);

        (await resolver.TryResolveAsync()).ShouldBeNull();
        store.GetCalls.ShouldBe(0);
    }

    private sealed class FakeAuthStateProvider(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(user));
    }

    private sealed class FakeLinkStore : IPortalTenantLinkStore
    {
        public PortalTenantLink? Link { get; set; }
        public int GetCalls { get; private set; }
        public string? LastQueriedEmail { get; private set; }

        public Task<PortalTenantLink?> GetAsync(string email, CancellationToken cancellationToken = default)
        {
            GetCalls++;
            LastQueriedEmail = email;
            return Task.FromResult(Link);
        }

        public Task<PortalTenantLink> UpsertAsync(string email, string tenantId, string apiKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PortalTenantLink> UpsertKeylessAsync(string email, string tenantId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler, Uri baseAddress) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = baseAddress };
    }

    private sealed class FakeSelectionStore : IPortalWorkspaceSelectionStore
    {
        public Task<PortalWorkspaceSelection?> GetAsync(string email, CancellationToken cancellationToken = default) =>
            Task.FromResult<PortalWorkspaceSelection?>(null);

        public Task SetAsync(string email, string tenantId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
