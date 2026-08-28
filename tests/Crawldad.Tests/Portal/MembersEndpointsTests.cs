using System.Net;
using Crawldad.Client;
using Crawldad.Contracts.Tenancy;
using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Client;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Portal;

/// <summary>The account Members card's form handlers (issue #119 PR6): add / change-role / remove, each resolving the tenant
/// and calling the SDK, then redirecting back (PRG) — surfacing a refusal as a <c>?memberError=</c> and treating an
/// already-gone member (404) as done. Owner-only enforcement is on the API; these just map outcomes to redirects.</summary>
public class MembersEndpointsTests
{
    private static readonly IServiceProvider _services = new ServiceCollection().AddLogging().BuildServiceProvider();
    private static readonly Guid _id = Guid.NewGuid();

    [Fact]
    public async Task Add_succeeds_and_redirects_to_the_account()
    {
        var result = await MembersEndpoints.AddAsync(Http(), Linked(MemberReturning()), "teammate@x.test", "member");

        (await RunAsync(result)).Location.ShouldBe(MembersEndpoints.AccountPath);
    }

    [Fact]
    public async Task Add_with_a_missing_email_or_bad_role_surfaces_an_error()
    {
        (await RunAsync(await MembersEndpoints.AddAsync(Http(), Linked(MemberReturning()), email: "", "member"))).Location
            .ShouldStartWith(MembersEndpoints.AccountPath + "?memberError=");
        (await RunAsync(await MembersEndpoints.AddAsync(Http(), Linked(MemberReturning()), "teammate@x.test", role: "bogus"))).Location
            .ShouldStartWith(MembersEndpoints.AccountPath + "?memberError=");
    }

    [Fact]
    public async Task Change_role_succeeds_and_redirects()
    {
        var result = await MembersEndpoints.ChangeRoleAsync(Http(), Linked(MemberReturning()), _id, "owner");

        (await RunAsync(result)).Location.ShouldBe(MembersEndpoints.AccountPath);
    }

    [Fact]
    public async Task Change_role_with_a_bad_role_surfaces_an_error()
    {
        var result = await MembersEndpoints.ChangeRoleAsync(Http(), Linked(MemberReturning()), _id, "");

        (await RunAsync(result)).Location.ShouldStartWith(MembersEndpoints.AccountPath + "?memberError=");
    }

    [Fact]
    public async Task Remove_succeeds_and_redirects()
    {
        var result = await MembersEndpoints.RemoveAsync(Http(), Linked(NoContent()), _id);

        (await RunAsync(result)).Location.ShouldBe(MembersEndpoints.AccountPath);
    }

    [Fact]
    public async Task Removing_an_already_gone_member_is_treated_as_done()
    {
        var result = await MembersEndpoints.RemoveAsync(Http(), Linked(Status(HttpStatusCode.NotFound)), _id);

        (await RunAsync(result)).Location.ShouldBe(MembersEndpoints.AccountPath); // 404 → done, no error
    }

    [Fact]
    public async Task Removing_the_last_owner_surfaces_the_guidance()
    {
        var result = await MembersEndpoints.RemoveAsync(Http(), Linked(Conflict()), _id);

        (await RunAsync(result)).Location.ShouldStartWith(MembersEndpoints.AccountPath + "?memberError=");
    }

    [Fact]
    public async Task An_api_error_surfaces_a_generic_message()
    {
        var result = await MembersEndpoints.AddAsync(Http(), Linked(Status(HttpStatusCode.BadRequest)), "teammate@x.test", "member");

        (await RunAsync(result)).Location.ShouldStartWith(MembersEndpoints.AccountPath + "?memberError=");
    }

    [Fact]
    public async Task A_transport_failure_surfaces_a_retry_message()
    {
        var result = await MembersEndpoints.RemoveAsync(Http(), Linked(new ThrowingHandler()), _id);

        (await RunAsync(result)).Location.ShouldStartWith(MembersEndpoints.AccountPath + "?memberError=");
    }

    [Fact]
    public async Task Not_linked_redirects_to_the_account_for_every_action()
    {
        (await RunAsync(await MembersEndpoints.AddAsync(Http(), Context(null), "t@x.test", "member"))).Location.ShouldBe(MembersEndpoints.AccountPath);
        (await RunAsync(await MembersEndpoints.RemoveAsync(Http(), Context(null), _id))).Location.ShouldBe(MembersEndpoints.AccountPath);
        (await RunAsync(await MembersEndpoints.ChangeRoleAsync(Http(), Context(null), _id, "owner"))).Location.ShouldBe(MembersEndpoints.AccountPath);
    }

    private static DefaultHttpContext Http() => new() { RequestServices = _services };

    private static FakeContext Context(PortalTenant? tenant) => new(tenant);

    private static FakeContext Linked(StubHttpMessageHandler handler) =>
        new(new PortalTenant("tenant-alpha", new CrawldadClient(
            new HttpClient(handler) { BaseAddress = ClientTestHarness.BaseUrl },
            new CrawldadClientOptions { BaseUrl = ClientTestHarness.BaseUrl, ApiKey = ClientTestHarness.ApiKey })));

    private static FakeContext Linked(HttpMessageHandler handler) =>
        new(new PortalTenant("tenant-alpha", new CrawldadClient(
            new HttpClient(handler) { BaseAddress = ClientTestHarness.BaseUrl },
            new CrawldadClientOptions { BaseUrl = ClientTestHarness.BaseUrl, ApiKey = ClientTestHarness.ApiKey })));

    private static StubHttpMessageHandler MemberReturning() =>
        new(_ => ClientTestHarness.Json(new TenantMembershipInfo(_id, "m@x.test", MembershipRole.Member, DateTimeOffset.UnixEpoch, null, true)));

    private static StubHttpMessageHandler NoContent() => new(_ => ClientTestHarness.Empty(HttpStatusCode.NoContent));

    private static StubHttpMessageHandler Conflict() =>
        new(_ => ClientTestHarness.JsonRaw(HttpStatusCode.Conflict, """{"title":"last_owner","status":409}"""));

    private static StubHttpMessageHandler Status(HttpStatusCode status) => new(_ => ClientTestHarness.Empty(status));

    private static async Task<(int Status, string? Location)> RunAsync(IResult result)
    {
        var http = new DefaultHttpContext { RequestServices = _services };
        await result.ExecuteAsync(http);
        return (http.Response.StatusCode, http.Response.Headers.Location);
    }

    private sealed class FakeContext(PortalTenant? tenant) : IPortalTenantContext
    {
        public Task<PortalTenant?> TryResolveAsync(CancellationToken cancellationToken = default) => Task.FromResult(tenant);

        public Task<PortalTenant> RequireAsync(CancellationToken cancellationToken = default) => Task.FromResult(tenant!);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("api down");
    }
}
