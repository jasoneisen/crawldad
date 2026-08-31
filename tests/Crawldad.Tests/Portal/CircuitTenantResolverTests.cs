using System.Security.Claims;
using Crawldad.Contracts.Runs;
using Crawldad.Contracts.Tenancy;
using Crawldad.Portal.Auth;
using Crawldad.Portal.Infrastructure.Security;
using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Client;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;

namespace Crawldad.Tests.Portal;

/// <summary>Unit tests for <see cref="CircuitTenantResolver"/> in isolation (fake auth-state provider + fake selection
/// store + fake console client factory). The portal is console-mode only (issue #119): a signed-in circuit with an active
/// workspace gets a <see cref="Crawldad.Client.CrawldadClient"/> authenticated as the portal console identity; an
/// unauthenticated, no-selection, or claim-less circuit is a clean not-linked state; an unconfigured console is a distinct
/// state; and the email is normalized byte-for-byte the same way the OTP sign-in stores it, so a circuit and a request for
/// the same user land on the same active workspace.</summary>
public class CircuitTenantResolverTests
{
    private static FakeAuthStateProvider AuthenticatedAs(string? email)
    {
        Claim[] claims = email is null ? [] : [new Claim(ClaimTypes.Email, email)];
        return new FakeAuthStateProvider(new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "TestCookie")));
    }

    private static FakeAuthStateProvider Anonymous() =>
        new(new ClaimsPrincipal(new ClaimsIdentity()));

    private static (CircuitTenantResolver Resolver, CapturingHandler Handler, FakeSelectionStore Selections) ResolverFor(
        AuthenticationStateProvider authState, string? selection)
    {
        var capture = new CapturingHandler(_ => ClientTestHarness.Json(new QueueStatsResponse(4, 0, 0)));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal) { ["Crawldad:Api:BaseUrl"] = "https://api.crawldad.test/" })
            .Build();
        var consoleClients = new ConsoleClientFactory(new StubHandlerFactory(capture), new FakeTokenSource("entra-token"), config);
        var selections = new FakeSelectionStore(selection);
        return (new CircuitTenantResolver(authState, selections, consoleClients), capture, selections);
    }

    [Fact]
    public async Task Resolves_the_active_workspace_with_a_console_client()
    {
        var (resolver, capture, _) = ResolverFor(AuthenticatedAs("user@example.com"), selection: "tenant-77");

        var tenant = await resolver.TryResolveAsync();

        resolver.ConsoleConfigured.ShouldBeTrue();
        tenant.ShouldNotBeNull();
        tenant.TenantId.ShouldBe("tenant-77");
        (await tenant.Client.GetQueueStatsAsync()).Queued.ShouldBe(4); // the console client actually calls the API...
        capture.Authorization.ShouldBe("Bearer entra-token");           // ...bearing the first-party token...
        capture.Workspace.ShouldBe("tenant-77");                        // ...and the active-workspace selector.
    }

    [Fact]
    public async Task Normalizes_the_claim_email_exactly_like_the_otp_sign_in()
    {
        // The active workspace is keyed by the OTP-normalized identity; a differently-cased/spaced claim must still find it,
        // and the console user selector carries the normalized form.
        const string StoredIdentity = "user@example.com";
        var (resolver, capture, selections) = ResolverFor(AuthenticatedAs("  User@Example.COM  "), selection: "tenant-9");

        var tenant = await resolver.TryResolveAsync();

        tenant.ShouldNotBeNull();
        await tenant.Client.GetQueueStatsAsync();
        selections.LastQueriedEmail.ShouldBe(StoredIdentity);
        selections.LastQueriedEmail.ShouldBe(PortalAuthService.NormalizeEmail("  User@Example.COM  "));
        capture.ConsoleUser.ShouldBe(StoredIdentity);
    }

    [Fact]
    public async Task Authenticated_but_no_active_workspace_is_not_linked()
    {
        var (resolver, _, _) = ResolverFor(AuthenticatedAs("nolink@example.com"), selection: null);

        (await resolver.TryResolveAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task Unconfigured_console_circuit_is_not_linked_and_reports_not_configured()
    {
        // No ConsoleClientFactory: even with a selection the circuit resolves null, and ConsoleConfigured is false.
        var selections = new FakeSelectionStore("tenant-77");
        var resolver = new CircuitTenantResolver(AuthenticatedAs("user@example.com"), selections, consoleClients: null);

        resolver.ConsoleConfigured.ShouldBeFalse();
        (await resolver.TryResolveAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task Anonymous_circuit_is_not_linked_and_never_touches_the_selection_store()
    {
        var (resolver, _, selections) = ResolverFor(Anonymous(), selection: "t");

        (await resolver.TryResolveAsync()).ShouldBeNull();
        selections.GetCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Authenticated_without_an_email_claim_is_not_linked_and_never_touches_the_store()
    {
        var (resolver, _, selections) = ResolverFor(AuthenticatedAs(email: null), selection: "t");

        (await resolver.TryResolveAsync()).ShouldBeNull();
        selections.GetCalls.ShouldBe(0);
    }

    private sealed class FakeAuthStateProvider(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(user));
    }

    private sealed class StubHandlerFactory(HttpMessageHandler handler) : IHttpMessageHandlerFactory
    {
        public HttpMessageHandler CreateHandler(string name) => handler;
    }

    private sealed class FakeTokenSource(string token) : IConsoleTokenSource
    {
        public ValueTask<string> GetTokenAsync(CancellationToken cancellationToken) => ValueTask.FromResult(token);
    }

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

    private sealed class FakeSelectionStore(string? tenantId) : IPortalWorkspaceSelectionStore
    {
        public int GetCalls { get; private set; }
        public string? LastQueriedEmail { get; private set; }

        public Task<PortalWorkspaceSelection?> GetAsync(string email, CancellationToken cancellationToken = default)
        {
            GetCalls++;
            LastQueriedEmail = email;
            return Task.FromResult(tenantId is null ? null : new PortalWorkspaceSelection { Email = email, TenantId = tenantId });
        }

        public Task SetAsync(string email, string tenantId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
