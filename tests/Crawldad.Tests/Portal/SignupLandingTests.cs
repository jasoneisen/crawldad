using System.Net;
using Crawldad.Contracts.Tenancy;
using Crawldad.Portal.Auth;
using Crawldad.Portal.Infrastructure.Security;
using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Client;
using Microsoft.Extensions.Configuration;

namespace Crawldad.Tests.Portal;

/// <summary>The post-verification landing decision for the public signup flow (issue #119). Signup shares the login page's
/// enumeration-safe OTP steps; the ONLY thing it does differently is this: a returning account (one that already has an
/// active workspace) lands exactly like a /login sign-in (ReturnUrl via SafeRedirect), while a zero-workspace account is
/// provisioned its one free workspace and lands on the first-run dashboard — or, honestly, on the account page when console
/// access is unconfigured (nothing to provision with) or with a safe error on a transient failure. The console arms drive
/// the REAL <see cref="PortalProvisioningService"/> over a stub API, so "zero-workspace user lands and provisions" and the
/// one-per-email 409 recovery are exercised end-to-end through the landing.</summary>
public class SignupLandingTests
{
    private const string _email = "new@example.com";

    [Theory]
    [InlineData("/app/payloads", "/app/payloads")] // a same-site return url is honoured, exactly like /login
    [InlineData(null, "/app")]                       // no return url → the app home
    [InlineData("//evil.example", "/app")]           // an open-redirect attempt is rejected by SafeRedirect
    public async Task A_returning_account_lands_like_a_login_and_is_never_provisioned(string? returnPath, string expected)
    {
        var existing = new PortalWorkspaceSelection { Email = _email, TenantId = "ws-existing" };
        var (landing, selections) = LandingFor(handler: null, consoleMode: false, existingSelection: existing);

        var destination = await landing.ResolveAsync(_email, returnPath, CancellationToken.None);

        destination.ShouldBe(expected);
        selections.SetCount.ShouldBe(0); // a returning account is never re-provisioned / re-selected
    }

    [Fact]
    public async Task A_zero_workspace_console_account_provisions_and_lands_on_the_first_run_dashboard()
    {
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new WorkspaceSummary("ws-created", "My workspace", MembershipRole.Owner), HttpStatusCode.Created));
        var (landing, selections) = LandingFor(handler);

        var destination = await landing.ResolveAsync(_email, returnUrl: null, CancellationToken.None);

        destination.ShouldBe(SignupLanding.FirstRunDashboard);
        selections.Last.ShouldBe((_email, "ws-created")); // provisioned + made the active workspace
    }

    [Fact]
    public async Task A_one_per_email_conflict_recovers_the_existing_workspace_and_still_lands_on_the_dashboard()
    {
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.JsonRaw(HttpStatusCode.Conflict, """{"title":"free_tenant_exists","status":409,"tenantId":"ws-existing"}"""));
        var (landing, selections) = LandingFor(handler);

        var destination = await landing.ResolveAsync(_email, returnUrl: null, CancellationToken.None);

        destination.ShouldBe(SignupLanding.FirstRunDashboard);
        selections.Last.ShouldBe((_email, "ws-existing")); // recovered + selected the existing workspace
    }

    [Fact]
    public async Task An_unconfigured_console_signup_lands_on_the_account_page_and_provisions_nothing()
    {
        // No console identity is configured → the service reports Unavailable → the honest account-page landing, where the
        // zero-workspace state explains it and offers the "claim an existing workspace" action.
        var (landing, selections) = LandingFor(handler: null, consoleMode: false);

        var destination = await landing.ResolveAsync(_email, returnUrl: null, CancellationToken.None);

        destination.ShouldBe(SignupLanding.AccountPath); // "/app/account"
        selections.Last.ShouldBeNull();
    }

    [Fact]
    public async Task A_failed_provision_lands_on_the_account_page_carrying_the_safe_error()
    {
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Empty(HttpStatusCode.TooManyRequests));
        var (landing, _) = LandingFor(handler);

        var destination = await landing.ResolveAsync(_email, returnUrl: null, CancellationToken.None);

        destination.ShouldStartWith($"{SignupLanding.AccountPath}?provisionError=");
        // The safe, non-sensitive message is URL-encoded onto the redirect for the account page to surface (and retry).
        destination.ShouldContain(Uri.EscapeDataString("We couldn't create your workspace just now. Please try again in a moment."));
    }

    private static (SignupLanding Landing, RecordingSelectionStore Selections) LandingFor(
        HttpMessageHandler? handler,
        bool consoleMode = true,
        PortalWorkspaceSelection? existingSelection = null)
    {
        var selections = new RecordingSelectionStore(existingSelection);
        ConsoleClientFactory? consoleClients = null;
        if (consoleMode)
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal) { ["Crawldad:Api:BaseUrl"] = "https://api.crawldad.test/" })
                .Build();
            consoleClients = new ConsoleClientFactory(new StubHandlerFactory(handler!), new FakeTokenSource("entra-token"), config);
        }

        var provisioning = new PortalProvisioningService(selections, consoleClients);
        return (new SignupLanding(selections, provisioning), selections);
    }

    private sealed class StubHandlerFactory(HttpMessageHandler handler) : IHttpMessageHandlerFactory
    {
        public HttpMessageHandler CreateHandler(string name) => handler;
    }

    private sealed class FakeTokenSource(string token) : IConsoleTokenSource
    {
        public ValueTask<string> GetTokenAsync(CancellationToken cancellationToken) => ValueTask.FromResult(token);
    }

    // GetAsync returns the preset selection (a returning account) or null (a zero-workspace account); SetAsync records the
    // provision's selection so a test can assert the account was made active on its workspace.
    private sealed class RecordingSelectionStore(PortalWorkspaceSelection? existing) : IPortalWorkspaceSelectionStore
    {
        public (string Email, string TenantId)? Last { get; private set; }
        public int SetCount { get; private set; }

        public Task<PortalWorkspaceSelection?> GetAsync(string email, CancellationToken cancellationToken = default) =>
            Task.FromResult(existing);

        public Task SetAsync(string email, string tenantId, CancellationToken cancellationToken = default)
        {
            Last = (email, tenantId);
            SetCount++;
            return Task.CompletedTask;
        }
    }
}
