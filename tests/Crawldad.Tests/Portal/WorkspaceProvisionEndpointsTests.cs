using System.Security.Claims;
using Crawldad.Portal.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Portal;

/// <summary>The account "create your free workspace" affordance's form handler (issue #119 PR7): it provisions through
/// <see cref="IPortalProvisioningService"/> and redirects — to the dashboard when the account now has a workspace
/// (provisioned OR recovered), and back to the account with a safe error message when it doesn't (unavailable / failed) or when
/// there is no signed-in identity.</summary>
public class WorkspaceProvisionEndpointsTests
{
    private static readonly IServiceProvider _services = new ServiceCollection().AddLogging().BuildServiceProvider();

    [Theory]
    [InlineData(PortalProvisionOutcome.Provisioned)]
    [InlineData(PortalProvisionOutcome.AlreadyProvisioned)]
    public async Task A_workspace_outcome_redirects_to_the_dashboard(PortalProvisionOutcome outcome)
    {
        var service = new FakeProvisioningService(new PortalProvisionResult(outcome, "t-1", "ok"));

        var result = await WorkspaceProvisionEndpoints.ProvisionAsync(Http(), service, "My workspace");

        (await RunAsync(result)).Location.ShouldBe(WorkspaceProvisionEndpoints.DashboardPath);
        service.LastEmail.ShouldBe("u@x.test");
        service.LastDisplayName.ShouldBe("My workspace");
    }

    [Theory]
    [InlineData(PortalProvisionOutcome.Unavailable)]
    [InlineData(PortalProvisionOutcome.Failed)]
    public async Task A_non_workspace_outcome_redirects_to_the_account_with_the_error(PortalProvisionOutcome outcome)
    {
        var service = new FakeProvisioningService(new PortalProvisionResult(outcome, null, "no dice"));

        var result = await WorkspaceProvisionEndpoints.ProvisionAsync(Http(), service, null);

        var location = (await RunAsync(result)).Location;
        location.ShouldNotBeNull();
        location.ShouldStartWith(WorkspaceProvisionEndpoints.AccountPath);
        location.ShouldContain("provisionError=no%20dice"); // the message is URL-encoded onto the redirect
    }

    [Fact]
    public async Task No_signed_in_identity_redirects_to_the_account_without_provisioning()
    {
        var service = new FakeProvisioningService(new PortalProvisionResult(PortalProvisionOutcome.Provisioned, "t-1", "ok"));

        var result = await WorkspaceProvisionEndpoints.ProvisionAsync(Http(email: null), service, null);

        (await RunAsync(result)).Location.ShouldBe(WorkspaceProvisionEndpoints.AccountPath);
        service.Called.ShouldBeFalse(); // never provisioned — there is no verified user to attribute it to
    }

    private static DefaultHttpContext Http(string? email = "u@x.test")
    {
        Claim[] claims = email is null ? [] : [new Claim(ClaimTypes.Email, email)];
        return new DefaultHttpContext { RequestServices = _services, User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestCookie")) };
    }

    private static async Task<(int Status, string? Location)> RunAsync(IResult result)
    {
        var http = new DefaultHttpContext { RequestServices = _services };
        await result.ExecuteAsync(http);
        return (http.Response.StatusCode, http.Response.Headers.Location);
    }

    private sealed class FakeProvisioningService(PortalProvisionResult result) : IPortalProvisioningService
    {
        public bool Called { get; private set; }

        public string? LastEmail { get; private set; }

        public string? LastDisplayName { get; private set; }

        public Task<PortalProvisionResult> ProvisionAsync(string email, string? displayName, CancellationToken cancellationToken = default)
        {
            Called = true;
            LastEmail = email;
            LastDisplayName = displayName;
            return Task.FromResult(result);
        }
    }
}
