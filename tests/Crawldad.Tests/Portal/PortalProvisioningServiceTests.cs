using System.Net;
using Crawldad.Client;
using Crawldad.Contracts.Tenancy;
using Crawldad.Portal.Infrastructure.Security;
using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Client;
using Microsoft.Extensions.Configuration;

namespace Crawldad.Tests.Portal;

/// <summary>The portal-side free-workspace provisioning service (issue #119 PR7): it calls the API's console-only provisioning
/// endpoint through a workspace-less console client and, on success, records the account's keyless link + active selection so
/// the next request resolves to the new workspace. A one-per-email 409 that carries the existing workspace is a clean recovery
/// (link + select it); a 409 with no recoverable id, any other API error, and stored-key mode (no console identity) each leave
/// nothing linked. Exercised in isolation with a stub API handler + recording stores.</summary>
public class PortalProvisioningServiceTests
{
    private const string _email = "new@example.com";

    [Fact]
    public async Task Provisions_then_links_and_selects_the_new_workspace()
    {
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new WorkspaceSummary("t-created", "My workspace", MembershipRole.Owner), HttpStatusCode.Created));
        var (service, links, selections) = ServiceFor(handler);

        var result = await service.ProvisionAsync(_email, "My workspace");

        result.Outcome.ShouldBe(PortalProvisionOutcome.Provisioned);
        result.TenantId.ShouldBe("t-created");
        handler.Last.Path.ShouldBe("/provisioning/tenants");
        links.KeylessUpserts.ShouldHaveSingleItem().ShouldBe((_email, "t-created")); // keyless — console mode
        selections.Last.ShouldBe((_email, "t-created"));
    }

    [Fact]
    public async Task The_email_is_normalized_for_the_link_and_selection()
    {
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new WorkspaceSummary("t-x", "n", MembershipRole.Owner), HttpStatusCode.Created));
        var (service, links, selections) = ServiceFor(handler);

        await service.ProvisionAsync("  NEW@Example.COM  ", null);

        links.KeylessUpserts.ShouldHaveSingleItem().Email.ShouldBe("new@example.com");
        selections.Last!.Value.Email.ShouldBe("new@example.com");
    }

    [Fact]
    public async Task An_already_provisioned_409_recovers_by_linking_the_existing_workspace()
    {
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.JsonRaw(HttpStatusCode.Conflict, """{"title":"free_tenant_exists","status":409,"tenantId":"t-existing"}"""));
        var (service, links, selections) = ServiceFor(handler);

        var result = await service.ProvisionAsync(_email, null);

        result.Outcome.ShouldBe(PortalProvisionOutcome.AlreadyProvisioned);
        result.TenantId.ShouldBe("t-existing");
        links.KeylessUpserts.ShouldHaveSingleItem().ShouldBe((_email, "t-existing")); // recovered the link to the existing workspace
        selections.Last.ShouldBe((_email, "t-existing"));
    }

    [Theory]
    [InlineData("""{"title":"free_tenant_exists","status":409}""")] // no tenantId property
    [InlineData("""{"tenantId":""}""")]                              // blank tenantId
    [InlineData("""{"tenantId":123}""")]                             // non-string tenantId
    [InlineData("123")]                                              // valid JSON but not an object
    [InlineData("not-json")]                                          // unparseable body
    [InlineData("")]                                                  // empty body
    public async Task A_409_with_no_recoverable_tenant_id_is_a_clean_failure(string body)
    {
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.JsonRaw(HttpStatusCode.Conflict, body));
        var (service, links, selections) = ServiceFor(handler);

        var result = await service.ProvisionAsync(_email, null);

        result.Outcome.ShouldBe(PortalProvisionOutcome.Failed);
        result.TenantId.ShouldBeNull();
        links.KeylessUpserts.ShouldBeEmpty(); // nothing linked when we can't identify the workspace
        selections.Last.ShouldBeNull();
    }

    [Fact]
    public async Task Any_other_api_error_is_a_clean_failure()
    {
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Empty(HttpStatusCode.TooManyRequests));
        var (service, links, _) = ServiceFor(handler);

        (await service.ProvisionAsync(_email, null)).Outcome.ShouldBe(PortalProvisionOutcome.Failed);
        links.KeylessUpserts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Stored_key_mode_reports_unavailable_and_links_nothing()
    {
        var (service, links, selections) = ServiceFor(handler: null, consoleMode: false);

        var result = await service.ProvisionAsync(_email, null);

        result.Outcome.ShouldBe(PortalProvisionOutcome.Unavailable);
        result.TenantId.ShouldBeNull();
        links.KeylessUpserts.ShouldBeEmpty();
        selections.Last.ShouldBeNull();
    }

    [Fact]
    public async Task A_blank_email_is_rejected() =>
        await Should.ThrowAsync<ArgumentException>(() =>
            ServiceFor(handler: null, consoleMode: false).Service.ProvisionAsync("  ", null));

    private static (PortalProvisioningService Service, RecordingLinkStore Links, RecordingSelectionStore Selections) ServiceFor(
        HttpMessageHandler? handler,
        bool consoleMode = true)
    {
        var links = new RecordingLinkStore();
        var selections = new RecordingSelectionStore();
        ConsoleClientFactory? consoleClients = null;
        if (consoleMode)
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal) { ["Crawldad:Api:BaseUrl"] = "https://api.crawldad.test/" })
                .Build();
            consoleClients = new ConsoleClientFactory(new StubHandlerFactory(handler!), new FakeTokenSource("entra-token"), config);
        }

        return (new PortalProvisioningService(links, selections, consoleClients), links, selections);
    }

    private sealed class StubHandlerFactory(HttpMessageHandler handler) : IHttpMessageHandlerFactory
    {
        public HttpMessageHandler CreateHandler(string name) => handler;
    }

    private sealed class FakeTokenSource(string token) : IConsoleTokenSource
    {
        public ValueTask<string> GetTokenAsync(CancellationToken cancellationToken) => ValueTask.FromResult(token);
    }

    private sealed class RecordingLinkStore : IPortalTenantLinkStore
    {
        public List<(string Email, string TenantId)> KeylessUpserts { get; } = [];

        public Task<PortalTenantLink?> GetAsync(string email, CancellationToken cancellationToken = default) =>
            Task.FromResult<PortalTenantLink?>(null);

        public Task<PortalTenantLink> UpsertAsync(string email, string tenantId, string apiKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("provisioning is keyless (console-mode)");

        public Task<PortalTenantLink> UpsertKeylessAsync(string email, string tenantId, CancellationToken cancellationToken = default)
        {
            KeylessUpserts.Add((email, tenantId));
            return Task.FromResult(new PortalTenantLink { Email = email, TenantId = tenantId, ProtectedApiKey = null });
        }
    }

    private sealed class RecordingSelectionStore : IPortalWorkspaceSelectionStore
    {
        public (string Email, string TenantId)? Last { get; private set; }

        public Task<PortalWorkspaceSelection?> GetAsync(string email, CancellationToken cancellationToken = default) =>
            Task.FromResult<PortalWorkspaceSelection?>(null);

        public Task SetAsync(string email, string tenantId, CancellationToken cancellationToken = default)
        {
            Last = (email, tenantId);
            return Task.CompletedTask;
        }
    }
}
