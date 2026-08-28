using System.Security.Claims;
using Crawldad.Contracts.Runs;
using Crawldad.Contracts.Tenancy;
using Crawldad.Portal.Infrastructure.Security;
using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Client;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Crawldad.Tests.Portal;

/// <summary>Unit tests for <see cref="PortalTenantContext"/> in isolation (fake store + fake HttpContext + fake
/// HttpClient factory): a linked user gets a client authenticated as their tenant; an unauthenticated, unlinked, or
/// claim-less request is a clean not-linked state (never a client with an empty key); resolution is cached.</summary>
public class PortalTenantContextTests
{
    private const string _apiKey = "sk_tenant_LEAKME_0123456789";
    private static readonly EphemeralDataProtectionProvider _protection = new();

    private static PortalTenantLink LinkFor(string email, string tenantId) => new()
    {
        Email = email,
        TenantId = tenantId,
        ProtectedApiKey = PortalTenancy.ApiKeyProtector(_protection).Protect(_apiKey),
    };

    // A console-mode verify-then-discard link (no stored key) and a link whose ciphertext no longer decrypts (the DP ring
    // rotated) — both console-mode shapes where the stored key is unavailable but the console credential still authenticates.
    private static PortalTenantLink KeylessLinkFor(string email, string tenantId) =>
        new() { Email = email, TenantId = tenantId, ProtectedApiKey = null };

    private static PortalTenantLink UndecryptableLinkFor(string email, string tenantId) =>
        new() { Email = email, TenantId = tenantId, ProtectedApiKey = "not-valid-dp-ciphertext" };

    private static (PortalTenantContext Context, CapturingHandler Handler) ConsoleContextFor(IHttpContextAccessor accessor, FakeLinkStore store, FakeSelectionStore? selections = null)
    {
        var capture = new CapturingHandler(_ => ClientTestHarness.Json(new QueueStatsResponse(3, 0, 0)));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal) { ["Crawldad:Api:BaseUrl"] = "https://api.crawldad.test/" })
            .Build();
        var consoleClients = new ConsoleClientFactory(new StubHandlerFactory(capture), new FakeTokenSource("entra-token"), config);
        var context = new PortalTenantContext(
            accessor, store, selections ?? new FakeSelectionStore(), _protection, new StubHttpClientFactory(capture, new Uri("https://api.crawldad.test/")), consoleClients);
        return (context, capture);
    }

    private static FakeHttpContextAccessor AuthenticatedAs(string? email)
    {
        Claim[] claims = email is null ? [] : [new Claim(ClaimTypes.Email, email)];
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "TestCookie")) };
        return new FakeHttpContextAccessor(http);
    }

    private static FakeHttpContextAccessor Anonymous() =>
        new(new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) });

    private static FakeHttpContextAccessor NoRequest() => new(httpContext: null);

    private static (PortalTenantContext Context, StubHttpMessageHandler Handler) ContextFor(IHttpContextAccessor accessor, FakeLinkStore store, FakeSelectionStore? selections = null)
    {
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Json(new QueueStatsResponse(7, 0, 0)));
        var factory = new StubHttpClientFactory(handler, new Uri("https://api.crawldad.test/"));
        return (new PortalTenantContext(accessor, store, selections ?? new FakeSelectionStore(), _protection, factory), handler);
    }

    [Fact]
    public async Task Resolves_a_linked_user_with_a_client_authenticated_as_the_tenant()
    {
        var store = new FakeLinkStore { Link = LinkFor("user@example.com", "tenant-42") };
        var (context, handler) = ContextFor(AuthenticatedAs("user@example.com"), store);

        var tenant = await context.RequireAsync();

        tenant.TenantId.ShouldBe("tenant-42");
        tenant.AuthMode.ShouldBe(PortalAuthMode.Key);                   // unconfigured (no console factory) → stored-key mode
        (await tenant.Client.GetQueueStatsAsync()).Queued.ShouldBe(7); // the client actually calls the API...
        handler.Last.Authorization.ShouldBe($"Bearer {_apiKey}");       // ...bearing the tenant's decrypted key
    }

    [Fact]
    public async Task Console_mode_resolves_a_console_client_bearing_the_token_and_selectors()
    {
        var store = new FakeLinkStore { Link = LinkFor("user@example.com", "tenant-42") };
        var (context, capture) = ConsoleContextFor(AuthenticatedAs("User@Example.com"), store);

        var tenant = await context.RequireAsync();

        tenant.AuthMode.ShouldBe(PortalAuthMode.Console);
        tenant.StoredKeyRetained.ShouldBeTrue();                        // a stored key remains → the transition read-fallback
        (await tenant.Client.GetQueueStatsAsync()).Queued.ShouldBe(3); // the console client calls the API...
        capture.Authorization.ShouldBe("Bearer entra-token");           // ...bearing the first-party token...
        capture.ConsoleUser.ShouldBe("user@example.com");               // ...and the normalized user selector...
        capture.Workspace.ShouldBe("tenant-42");                        // ...and the active-workspace selector.
    }

    [Fact]
    public async Task Console_mode_scopes_to_the_active_workspace_selection()
    {
        // The account's link tenant is tenant-42, but it has switched its active workspace to tenant-99 (a different
        // workspace it is a member of). Resolution must scope to tenant-99, and — since that is not the link tenant — carry
        // no stored-key fallback (only the link tenant's key can back a fallback).
        var store = new FakeLinkStore { Link = LinkFor("user@example.com", "tenant-42") };
        var selections = new FakeSelectionStore { Selection = new PortalWorkspaceSelection { Email = "user@example.com", TenantId = "tenant-99" } };
        var (context, capture) = ConsoleContextFor(AuthenticatedAs("user@example.com"), store, selections);

        var tenant = await context.RequireAsync();

        tenant.TenantId.ShouldBe("tenant-99");            // the active workspace, not the link tenant
        tenant.StoredKeyRetained.ShouldBeFalse();          // a non-link workspace has no stored-key fallback
        await tenant.Client.GetQueueStatsAsync();
        capture.Workspace.ShouldBe("tenant-99");           // the selector names the active workspace
    }

    [Fact]
    public async Task Stored_key_mode_ignores_a_stray_selection_and_uses_the_single_link()
    {
        // Stored-key mode is single-workspace: a selection left over from a console-mode session must not repoint it.
        var store = new FakeLinkStore { Link = LinkFor("user@example.com", "tenant-42") };
        var selections = new FakeSelectionStore { Selection = new PortalWorkspaceSelection { Email = "user@example.com", TenantId = "tenant-99" } };
        var (context, _) = ContextFor(AuthenticatedAs("user@example.com"), store, selections);

        (await context.RequireAsync()).TenantId.ShouldBe("tenant-42"); // the single stored link, not the stray selection
    }

    [Fact]
    public async Task Console_mode_with_a_keyless_link_is_pure_console_with_no_stored_key()
    {
        var store = new FakeLinkStore { Link = KeylessLinkFor("user@example.com", "tenant-42") };
        var (context, capture) = ConsoleContextFor(AuthenticatedAs("user@example.com"), store);

        var tenant = await context.RequireAsync();

        tenant.AuthMode.ShouldBe(PortalAuthMode.Console);
        tenant.StoredKeyRetained.ShouldBeFalse();                       // the key was discarded — no fallback
        (await tenant.Client.GetQueueStatsAsync()).Queued.ShouldBe(3); // the console client still works with no stored key
        capture.Authorization.ShouldBe("Bearer entra-token");
    }

    [Fact]
    public async Task Console_mode_drops_an_undecryptable_fallback_key_and_still_works()
    {
        var store = new FakeLinkStore { Link = UndecryptableLinkFor("user@example.com", "tenant-42") };
        var (context, _) = ConsoleContextFor(AuthenticatedAs("user@example.com"), store);

        // Must NOT throw CryptographicException — a rotated/garbage ciphertext is just dropped as a fallback; console works.
        var tenant = await context.RequireAsync();

        tenant.AuthMode.ShouldBe(PortalAuthMode.Console);
        tenant.StoredKeyRetained.ShouldBeFalse();
    }

    [Fact]
    public async Task A_keyless_link_outside_console_mode_is_not_linked()
    {
        // A console-mode link seen after console-mode was turned off has no key and no console — it cannot authenticate, so
        // it resolves to a clean not-linked state (re-attach with a key).
        var store = new FakeLinkStore { Link = KeylessLinkFor("user@example.com", "tenant-42") };
        var (context, _) = ContextFor(AuthenticatedAs("user@example.com"), store);

        (await context.TryResolveAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task Authenticated_but_unlinked_user_is_not_linked()
    {
        var (context, _) = ContextFor(AuthenticatedAs("nolink@example.com"), new FakeLinkStore { Link = null });

        (await context.TryResolveAsync()).ShouldBeNull();
        await Should.ThrowAsync<NotLinkedException>(async () => await context.RequireAsync());
    }

    [Fact]
    public async Task Anonymous_request_is_not_linked_and_never_touches_the_store()
    {
        var store = new FakeLinkStore { Link = LinkFor("x@example.com", "t") };
        var (context, _) = ContextFor(Anonymous(), store);

        (await context.TryResolveAsync()).ShouldBeNull();
        store.GetCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Request_without_an_http_context_is_not_linked()
    {
        var (context, _) = ContextFor(NoRequest(), new FakeLinkStore { Link = LinkFor("x@example.com", "t") });

        await Should.ThrowAsync<NotLinkedException>(async () => await context.RequireAsync());
    }

    [Fact]
    public async Task Authenticated_without_an_email_claim_is_not_linked()
    {
        var store = new FakeLinkStore { Link = LinkFor("x@example.com", "t") };
        var (context, _) = ContextFor(AuthenticatedAs(email: null), store);

        (await context.TryResolveAsync()).ShouldBeNull();
        store.GetCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Resolution_is_cached_for_the_request()
    {
        var store = new FakeLinkStore { Link = LinkFor("cache@example.com", "tenant-9") };
        var (context, _) = ContextFor(AuthenticatedAs("cache@example.com"), store);

        var first = await context.TryResolveAsync();
        var second = await context.TryResolveAsync();

        first.ShouldBeSameAs(second);
        store.GetCalls.ShouldBe(1); // resolved once, then served from the per-request cache
    }

    private sealed class FakeLinkStore : IPortalTenantLinkStore
    {
        public PortalTenantLink? Link { get; set; }
        public int GetCalls { get; private set; }

        public Task<PortalTenantLink?> GetAsync(string email, CancellationToken cancellationToken = default)
        {
            GetCalls++;
            return Task.FromResult(Link);
        }

        public Task<PortalTenantLink> UpsertAsync(string email, string tenantId, string apiKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PortalTenantLink> UpsertKeylessAsync(string email, string tenantId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeSelectionStore : IPortalWorkspaceSelectionStore
    {
        public PortalWorkspaceSelection? Selection { get; set; }

        public Task<PortalWorkspaceSelection?> GetAsync(string email, CancellationToken cancellationToken = default) => Task.FromResult(Selection);

        public Task SetAsync(string email, string tenantId, CancellationToken cancellationToken = default)
        {
            Selection = new PortalWorkspaceSelection { Email = email, TenantId = tenantId };
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHttpContextAccessor(HttpContext? httpContext) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = httpContext;
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler, Uri baseAddress) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = baseAddress };
    }

    private sealed class StubHandlerFactory(HttpMessageHandler handler) : IHttpMessageHandlerFactory
    {
        public HttpMessageHandler CreateHandler(string name) => handler;
    }

    private sealed class FakeTokenSource(string token) : IConsoleTokenSource
    {
        public ValueTask<string> GetTokenAsync(CancellationToken cancellationToken) => ValueTask.FromResult(token);
    }

    // Captures the exact credential headers the console client stamps on the outgoing request (Authorization + selectors).
    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public string? Authorization { get; private set; }
        public string? ConsoleUser { get; private set; }
        public string? Workspace { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            ConsoleUser = request.Headers.TryGetValues(ConsoleAuthHeaders.ConsoleUser, out var user) ? user.Single() : null;
            Workspace = request.Headers.TryGetValues(ConsoleAuthHeaders.Workspace, out var workspace) ? workspace.Single() : null;
            return Task.FromResult(responder(request));
        }
    }
}
