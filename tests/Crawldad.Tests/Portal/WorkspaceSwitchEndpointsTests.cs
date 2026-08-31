using System.Net;
using System.Security.Claims;
using Crawldad.Client;
using Crawldad.Contracts.Tenancy;
using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Client;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Portal;

/// <summary>The workspace switcher's form handler (issue #119): it persists the active-workspace selection only for a
/// workspace the account is actually a member of (consulting the authoritative GET /workspaces list — console is the only
/// mode), then redirects to the dashboard — and redirects to the account page (persisting nothing) when there is no active
/// workspace or no target. A non-member target is silently ignored (the API's membership gate is the real authority).</summary>
public class WorkspaceSwitchEndpointsTests
{
    private static readonly IServiceProvider _services = new ServiceCollection().AddLogging().BuildServiceProvider();

    [Fact]
    public async Task Switches_to_a_workspace_the_user_is_a_member_of()
    {
        var selections = new RecordingSelectionStore();
        var tenant = Linked(WorkspacesReturning("tenant-a", "tenant-b"));

        var result = await WorkspaceSwitchEndpoints.SwitchAsync(Http(), Context(tenant), selections, "tenant-b");

        (await RunAsync(result)).Location.ShouldBe(WorkspaceSwitchEndpoints.DashboardPath);
        selections.Last.ShouldBe(("u@x.test", "tenant-b")); // persisted for the signed-in user
    }

    [Fact]
    public async Task A_non_member_target_is_ignored_but_still_redirects()
    {
        var selections = new RecordingSelectionStore();
        var tenant = Linked(WorkspacesReturning("tenant-a"));

        var result = await WorkspaceSwitchEndpoints.SwitchAsync(Http(), Context(tenant), selections, "tenant-zzz");

        (await RunAsync(result)).Location.ShouldBe(WorkspaceSwitchEndpoints.DashboardPath);
        selections.Last.ShouldBeNull(); // never persisted — the target is not one of the user's workspaces
    }

    [Fact]
    public async Task A_console_list_failure_leaves_the_selection_unchanged()
    {
        var selections = new RecordingSelectionStore();
        var tenant = Linked(Failing());

        var result = await WorkspaceSwitchEndpoints.SwitchAsync(Http(), Context(tenant), selections, "tenant-b");

        (await RunAsync(result)).Location.ShouldBe(WorkspaceSwitchEndpoints.DashboardPath);
        selections.Last.ShouldBeNull();
    }

    [Fact]
    public async Task A_malformed_workspaces_body_is_treated_as_no_membership()
    {
        // A 2xx body missing its workspaces list (null Workspaces) must not crash — it is treated as "no membership", so the
        // switch is silently ignored (nothing persisted).
        var selections = new RecordingSelectionStore();
        var tenant = Linked(new StubHttpMessageHandler(_ => ClientTestHarness.JsonRaw(HttpStatusCode.OK, "{}")));

        var result = await WorkspaceSwitchEndpoints.SwitchAsync(Http(), Context(tenant), selections, "tenant-b");

        (await RunAsync(result)).Location.ShouldBe(WorkspaceSwitchEndpoints.DashboardPath);
        selections.Last.ShouldBeNull();
    }

    [Fact]
    public async Task No_email_or_no_workspace_redirects_to_the_account_without_persisting()
    {
        var selections = new RecordingSelectionStore();
        var tenant = Linked(WorkspacesReturning("tenant-a"));

        (await RunAsync(await WorkspaceSwitchEndpoints.SwitchAsync(Http(email: null), Context(tenant), selections, "tenant-a"))).Location
            .ShouldBe(WorkspaceSwitchEndpoints.AccountPath);
        (await RunAsync(await WorkspaceSwitchEndpoints.SwitchAsync(Http(), Context(tenant), selections, workspace: ""))).Location
            .ShouldBe(WorkspaceSwitchEndpoints.AccountPath);
        selections.Last.ShouldBeNull();
    }

    [Fact]
    public async Task Not_linked_redirects_to_the_account()
    {
        var selections = new RecordingSelectionStore();

        var result = await WorkspaceSwitchEndpoints.SwitchAsync(Http(), Context(tenant: null), selections, "tenant-a");

        (await RunAsync(result)).Location.ShouldBe(WorkspaceSwitchEndpoints.AccountPath);
        selections.Last.ShouldBeNull();
    }

    private static DefaultHttpContext Http(string? email = "u@x.test")
    {
        Claim[] claims = email is null ? [] : [new Claim(ClaimTypes.Email, email)];
        return new DefaultHttpContext { RequestServices = _services, User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestCookie")) };
    }

    private static FakeContext Context(PortalTenant? tenant) => new(tenant);

    private static PortalTenant Linked(StubHttpMessageHandler handler) =>
        new("tenant-alpha", new CrawldadClient(
            new HttpClient(handler) { BaseAddress = ClientTestHarness.BaseUrl },
            new CrawldadClientOptions { BaseUrl = ClientTestHarness.BaseUrl, ApiKey = ClientTestHarness.ApiKey }));

    private static StubHttpMessageHandler WorkspacesReturning(params string[] tenantIds) =>
        new(_ => ClientTestHarness.Json(new WorkspaceList([.. tenantIds.Select(id => new WorkspaceSummary(id, id, MembershipRole.Owner))])));

    private static StubHttpMessageHandler Failing() =>
        new(_ => ClientTestHarness.JsonRaw(HttpStatusCode.ServiceUnavailable, """{"title":"down","status":503}"""));

    private static async Task<(int Status, string? Location)> RunAsync(IResult result)
    {
        var http = new DefaultHttpContext { RequestServices = _services };
        await result.ExecuteAsync(http);
        return (http.Response.StatusCode, http.Response.Headers.Location);
    }

    private sealed class FakeContext(PortalTenant? tenant) : IPortalTenantContext
    {
        public bool ConsoleConfigured => true;

        public Task<PortalTenant?> TryResolveAsync(CancellationToken cancellationToken = default) => Task.FromResult(tenant);

        public Task<PortalTenant> RequireAsync(CancellationToken cancellationToken = default) => Task.FromResult(tenant!);
    }

    private sealed class RecordingSelectionStore : IPortalWorkspaceSelectionStore
    {
        public (string Email, string TenantId)? Last { get; private set; }

        public void Reset() => Last = null;

        public Task<PortalWorkspaceSelection?> GetAsync(string email, CancellationToken cancellationToken = default) =>
            Task.FromResult<PortalWorkspaceSelection?>(null);

        public Task SetAsync(string email, string tenantId, CancellationToken cancellationToken = default)
        {
            Last = (email, tenantId);
            return Task.CompletedTask;
        }
    }
}
