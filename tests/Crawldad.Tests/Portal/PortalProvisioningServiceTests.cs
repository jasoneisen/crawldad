using System.Net;
using Crawldad.Contracts.Tenancy;
using Crawldad.Portal.Infrastructure.Security;
using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Client;
using Microsoft.Extensions.Configuration;

namespace Crawldad.Tests.Portal;

/// <summary>The portal-side free-workspace provisioning service (issue #119): it calls the API's console-only provisioning
/// endpoint through a workspace-less console client and, on success, sets the account's active-workspace selection so the
/// next request resolves to the new workspace (the API owns the Owner membership — the portal keeps only the pointer). A
/// one-per-email 409 that carries the existing workspace is a clean recovery (select it); a 409 with no recoverable id, any
/// other API error, and an unconfigured console (no console identity) each select nothing. Exercised in isolation with a
/// stub API handler + a recording selection store.</summary>
public class PortalProvisioningServiceTests
{
    private const string _email = "new@example.com";

    [Fact]
    public async Task Provisions_then_selects_the_new_workspace()
    {
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new WorkspaceSummary("ws-created", "My workspace", MembershipRole.Owner), HttpStatusCode.Created));
        var (service, selections) = ServiceFor(handler);

        var result = await service.ProvisionAsync(_email, "My workspace");

        result.Outcome.ShouldBe(PortalProvisionOutcome.Provisioned);
        result.TenantId.ShouldBe("ws-created");
        handler.Last.Path.ShouldBe("/provisioning/tenants");
        selections.Last.ShouldBe((_email, "ws-created")); // the new workspace is now active
    }

    [Fact]
    public async Task The_email_is_normalized_for_the_selection()
    {
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new WorkspaceSummary("ws-x", "n", MembershipRole.Owner), HttpStatusCode.Created));
        var (service, selections) = ServiceFor(handler);

        await service.ProvisionAsync("  NEW@Example.COM  ", null);

        selections.Last!.Value.Email.ShouldBe("new@example.com");
    }

    [Fact]
    public async Task An_already_provisioned_409_recovers_by_selecting_the_existing_workspace()
    {
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.JsonRaw(HttpStatusCode.Conflict, """{"title":"free_tenant_exists","status":409,"tenantId":"ws-existing"}"""));
        var (service, selections) = ServiceFor(handler);

        var result = await service.ProvisionAsync(_email, null);

        result.Outcome.ShouldBe(PortalProvisionOutcome.AlreadyProvisioned);
        result.TenantId.ShouldBe("ws-existing");
        selections.Last.ShouldBe((_email, "ws-existing")); // recovered + selected the existing workspace
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
        var (service, selections) = ServiceFor(handler);

        var result = await service.ProvisionAsync(_email, null);

        result.Outcome.ShouldBe(PortalProvisionOutcome.Failed);
        result.TenantId.ShouldBeNull();
        selections.Last.ShouldBeNull(); // nothing selected when we can't identify the workspace
    }

    [Fact]
    public async Task Any_other_api_error_is_a_clean_failure()
    {
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Empty(HttpStatusCode.TooManyRequests));
        var (service, selections) = ServiceFor(handler);

        (await service.ProvisionAsync(_email, null)).Outcome.ShouldBe(PortalProvisionOutcome.Failed);
        selections.Last.ShouldBeNull();
    }

    [Fact]
    public async Task Unconfigured_console_reports_unavailable_and_selects_nothing()
    {
        var (service, selections) = ServiceFor(handler: null, consoleMode: false);

        var result = await service.ProvisionAsync(_email, null);

        result.Outcome.ShouldBe(PortalProvisionOutcome.Unavailable);
        result.TenantId.ShouldBeNull();
        selections.Last.ShouldBeNull();
    }

    [Fact]
    public async Task A_blank_email_is_rejected() =>
        await Should.ThrowAsync<ArgumentException>(() =>
            ServiceFor(handler: null, consoleMode: false).Service.ProvisionAsync("  ", null));

    private static (PortalProvisioningService Service, RecordingSelectionStore Selections) ServiceFor(
        HttpMessageHandler? handler,
        bool consoleMode = true)
    {
        var selections = new RecordingSelectionStore();
        ConsoleClientFactory? consoleClients = null;
        if (consoleMode)
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal) { ["Crawldad:Api:BaseUrl"] = "https://api.crawldad.test/" })
                .Build();
            consoleClients = new ConsoleClientFactory(new StubHandlerFactory(handler!), new FakeTokenSource("entra-token"), config);
        }

        return (new PortalProvisioningService(selections, consoleClients), selections);
    }

    private sealed class StubHandlerFactory(HttpMessageHandler handler) : IHttpMessageHandlerFactory
    {
        public HttpMessageHandler CreateHandler(string name) => handler;
    }

    private sealed class FakeTokenSource(string token) : IConsoleTokenSource
    {
        public ValueTask<string> GetTokenAsync(CancellationToken cancellationToken) => ValueTask.FromResult(token);
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
