using System.Security.Claims;
using Crawldad.Contracts.Runs;
using Crawldad.Contracts.Tenancy;
using Crawldad.Portal.Infrastructure.Security;
using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Client;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Crawldad.Tests.Portal;

/// <summary>Unit tests for <see cref="PortalTenantContext"/> in isolation (fake selection store + fake HttpContext + fake
/// console client factory). The portal is console-mode only (issue #119): a signed-in account with an active-workspace
/// selection gets a <see cref="CrawldadClient"/> authenticated as the portal console identity for that workspace; an
/// unauthenticated, claim-less, or no-selection request is a clean not-linked state; an unconfigured console is a distinct
/// state (<see cref="IPortalTenantContext.ConsoleConfigured"/> false). Resolution is cached per request.</summary>
public class PortalTenantContextTests
{
    private static (PortalTenantContext Context, CapturingHandler Handler) ConsoleContextFor(
        IHttpContextAccessor accessor, FakeSelectionStore selections)
    {
        var capture = new CapturingHandler(_ => ClientTestHarness.Json(new QueueStatsResponse(3, 0, 0)));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal) { ["Crawldad:Api:BaseUrl"] = "https://api.crawldad.test/" })
            .Build();
        var consoleClients = new ConsoleClientFactory(new StubHandlerFactory(capture), new FakeTokenSource("entra-token"), config);
        return (new PortalTenantContext(accessor, selections, consoleClients), capture);
    }

    // An unconfigured portal: no ConsoleClientFactory registered (Crawldad:ConsoleAuth absent).
    private static PortalTenantContext UnconfiguredContextFor(IHttpContextAccessor accessor, FakeSelectionStore selections) =>
        new(accessor, selections, consoleClients: null);

    private static FakeSelectionStore SelectedWorkspace(string email, string tenantId) =>
        new() { Selection = new PortalWorkspaceSelection { Email = email, TenantId = tenantId } };

    private static FakeHttpContextAccessor AuthenticatedAs(string? email)
    {
        Claim[] claims = email is null ? [] : [new Claim(ClaimTypes.Email, email)];
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "TestCookie")) };
        return new FakeHttpContextAccessor(http);
    }

    private static FakeHttpContextAccessor Anonymous() =>
        new(new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) });

    private static FakeHttpContextAccessor NoRequest() => new(httpContext: null);

    [Fact]
    public async Task Resolves_the_active_workspace_with_a_console_client_bearing_the_token_and_selectors()
    {
        var selections = SelectedWorkspace("user@example.com", "tenant-42");
        var (context, capture) = ConsoleContextFor(AuthenticatedAs("User@Example.com"), selections);

        var tenant = await context.RequireAsync();

        context.ConsoleConfigured.ShouldBeTrue();
        tenant.TenantId.ShouldBe("tenant-42");
        (await tenant.Client.GetQueueStatsAsync()).Queued.ShouldBe(3); // the console client calls the API...
        capture.Authorization.ShouldBe("Bearer entra-token");           // ...bearing the first-party token...
        capture.ConsoleUser.ShouldBe("user@example.com");               // ...and the normalized user selector...
        capture.Workspace.ShouldBe("tenant-42");                        // ...and the active-workspace selector.
    }

    [Fact]
    public async Task Scopes_to_the_active_workspace_selection()
    {
        // The account has switched its active workspace to tenant-99 (a different workspace it is a member of). Resolution
        // scopes to it; the API's membership gate backstops a stale selection.
        var selections = SelectedWorkspace("user@example.com", "tenant-99");
        var (context, capture) = ConsoleContextFor(AuthenticatedAs("user@example.com"), selections);

        var tenant = await context.RequireAsync();

        tenant.TenantId.ShouldBe("tenant-99");
        await tenant.Client.GetQueueStatsAsync();
        capture.Workspace.ShouldBe("tenant-99"); // the selector names the active workspace
    }

    [Fact]
    public async Task Authenticated_with_no_active_workspace_is_not_linked()
    {
        var (context, _) = ConsoleContextFor(AuthenticatedAs("nolink@example.com"), new FakeSelectionStore { Selection = null });

        (await context.TryResolveAsync()).ShouldBeNull();
        await Should.ThrowAsync<NotLinkedException>(async () => await context.RequireAsync());
    }

    [Fact]
    public async Task Unconfigured_console_is_not_linked_and_reports_not_configured()
    {
        // No ConsoleClientFactory (Crawldad:ConsoleAuth unset): the portal cannot reach the API for data at all. Even with a
        // selection present it resolves null, and ConsoleConfigured is false so the pages render the honest unconfigured state.
        var selections = SelectedWorkspace("user@example.com", "tenant-42");
        var context = UnconfiguredContextFor(AuthenticatedAs("user@example.com"), selections);

        context.ConsoleConfigured.ShouldBeFalse();
        (await context.TryResolveAsync()).ShouldBeNull();
        await Should.ThrowAsync<NotLinkedException>(async () => await context.RequireAsync());
    }

    [Fact]
    public async Task Anonymous_request_is_not_linked_and_never_touches_the_selection_store()
    {
        var selections = SelectedWorkspace("x@example.com", "t");
        var (context, _) = ConsoleContextFor(Anonymous(), selections);

        (await context.TryResolveAsync()).ShouldBeNull();
        selections.GetCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Request_without_an_http_context_is_not_linked()
    {
        var (context, _) = ConsoleContextFor(NoRequest(), SelectedWorkspace("x@example.com", "t"));

        await Should.ThrowAsync<NotLinkedException>(async () => await context.RequireAsync());
    }

    [Fact]
    public async Task Authenticated_without_an_email_claim_is_not_linked()
    {
        var selections = SelectedWorkspace("x@example.com", "t");
        var (context, _) = ConsoleContextFor(AuthenticatedAs(email: null), selections);

        (await context.TryResolveAsync()).ShouldBeNull();
        selections.GetCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Resolution_is_cached_for_the_request()
    {
        var selections = SelectedWorkspace("cache@example.com", "tenant-9");
        var (context, _) = ConsoleContextFor(AuthenticatedAs("cache@example.com"), selections);

        var first = await context.TryResolveAsync();
        var second = await context.TryResolveAsync();

        first.ShouldBeSameAs(second);
        selections.GetCalls.ShouldBe(1); // resolved once, then served from the per-request cache
    }

    private sealed class FakeSelectionStore : IPortalWorkspaceSelectionStore
    {
        public PortalWorkspaceSelection? Selection { get; set; }
        public int GetCalls { get; private set; }

        public Task<PortalWorkspaceSelection?> GetAsync(string email, CancellationToken cancellationToken = default)
        {
            GetCalls++;
            return Task.FromResult(Selection);
        }

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
