using System.Net;
using Crawldad.Client;
using Crawldad.Contracts.Tenancy;
using Crawldad.Portal.Auth;
using Crawldad.Portal.Infrastructure.Security;
using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Client;
using Microsoft.Extensions.Configuration;

namespace Crawldad.Tests.Portal;

/// <summary>The post-verification landing decision for the public signup flow (issue #119 PR8). Signup shares the login
/// page's enumeration-safe OTP steps; the ONLY thing it does differently is this: a returning account (already linked) lands
/// exactly like a /login sign-in (ReturnUrl via SafeRedirect), while a zero-workspace account is provisioned its one free
/// workspace and lands on the first-run dashboard — or, honestly, on the account page in stored-key mode (nothing to
/// provision with) or with a safe error on a transient failure. The console-mode arms drive the REAL
/// <see cref="PortalProvisioningService"/> over a stub API (the console harness), so "zero-workspace user lands and
/// provisions" and the one-per-email 409 recovery are exercised end-to-end through the landing.</summary>
public class SignupLandingTests
{
    private const string _email = "new@example.com";

    [Theory]
    [InlineData("/app/payloads", "/app/payloads")] // a same-site return url is honoured, exactly like /login
    [InlineData(null, "/app")]                       // no return url → the app home
    [InlineData("//evil.example", "/app")]           // an open-redirect attempt is rejected by SafeRedirect
    public async Task A_returning_account_lands_like_a_login_and_is_never_provisioned(string? returnPath, string expected)
    {
        var existing = new PortalTenantLink { Email = _email, TenantId = "t-existing", ProtectedApiKey = null };
        var (landing, links, selections) = LandingFor(handler: null, consoleMode: false, existingLink: existing);

        var destination = await landing.ResolveAsync(_email, returnPath, CancellationToken.None);

        destination.ShouldBe(expected);
        links.KeylessUpserts.ShouldBeEmpty();  // a returning account is never re-provisioned
        selections.Last.ShouldBeNull();
    }

    [Fact]
    public async Task A_zero_workspace_console_account_provisions_and_lands_on_the_first_run_dashboard()
    {
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new WorkspaceSummary("t-created", "My workspace", MembershipRole.Owner), HttpStatusCode.Created));
        var (landing, links, selections) = LandingFor(handler);

        var destination = await landing.ResolveAsync(_email, returnUrl: null, CancellationToken.None);

        destination.ShouldBe(SignupLanding.FirstRunDashboard);   // "/app?welcome=1" — the first-run on-ramp
        links.KeylessUpserts.ShouldHaveSingleItem().ShouldBe((_email, "t-created")); // provisioned + linked (keyless, console)
        selections.Last.ShouldBe((_email, "t-created"));                             // ...and made the active workspace
    }

    [Fact]
    public async Task A_one_per_email_conflict_recovers_the_existing_workspace_and_still_lands_on_the_dashboard()
    {
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.JsonRaw(HttpStatusCode.Conflict, """{"title":"free_tenant_exists","status":409,"tenantId":"t-existing"}"""));
        var (landing, links, selections) = LandingFor(handler);

        var destination = await landing.ResolveAsync(_email, returnUrl: null, CancellationToken.None);

        destination.ShouldBe(SignupLanding.FirstRunDashboard);
        links.KeylessUpserts.ShouldHaveSingleItem().ShouldBe((_email, "t-existing")); // recovered the link to the existing one
        selections.Last.ShouldBe((_email, "t-existing"));
    }

    [Fact]
    public async Task A_stored_key_signup_lands_on_the_account_page_and_provisions_nothing()
    {
        // No console identity is configured (stored-key mode) → the service reports Unavailable → the honest account-page
        // landing, where the zero-workspace state explains the operator-provisioned reality and offers the attach form.
        var (landing, links, selections) = LandingFor(handler: null, consoleMode: false);

        var destination = await landing.ResolveAsync(_email, returnUrl: null, CancellationToken.None);

        destination.ShouldBe(SignupLanding.AccountPath); // "/app/account"
        links.KeylessUpserts.ShouldBeEmpty();
        selections.Last.ShouldBeNull();
    }

    [Fact]
    public async Task A_failed_provision_lands_on_the_account_page_carrying_the_safe_error()
    {
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Empty(HttpStatusCode.TooManyRequests));
        var (landing, _, _) = LandingFor(handler);

        var destination = await landing.ResolveAsync(_email, returnUrl: null, CancellationToken.None);

        destination.ShouldStartWith($"{SignupLanding.AccountPath}?provisionError=");
        // The safe, non-sensitive message is URL-encoded onto the redirect for the account page to surface (and retry).
        destination.ShouldContain(Uri.EscapeDataString("We couldn't create your workspace just now. Please try again in a moment."));
    }

    private static (SignupLanding Landing, RecordingLinkStore Links, RecordingSelectionStore Selections) LandingFor(
        HttpMessageHandler? handler,
        bool consoleMode = true,
        PortalTenantLink? existingLink = null)
    {
        var links = new RecordingLinkStore(existingLink);
        var selections = new RecordingSelectionStore();
        ConsoleClientFactory? consoleClients = null;
        if (consoleMode)
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal) { ["Crawldad:Api:BaseUrl"] = "https://api.crawldad.test/" })
                .Build();
            consoleClients = new ConsoleClientFactory(new StubHandlerFactory(handler!), new FakeTokenSource("entra-token"), config);
        }

        var provisioning = new PortalProvisioningService(links, selections, consoleClients);
        return (new SignupLanding(links, provisioning), links, selections);
    }

    private sealed class StubHandlerFactory(HttpMessageHandler handler) : IHttpMessageHandlerFactory
    {
        public HttpMessageHandler CreateHandler(string name) => handler;
    }

    private sealed class FakeTokenSource(string token) : IConsoleTokenSource
    {
        public ValueTask<string> GetTokenAsync(CancellationToken cancellationToken) => ValueTask.FromResult(token);
    }

    // GetAsync returns the preset link (a returning account) or null (a zero-workspace account); UpsertKeylessAsync records the
    // provision's keyless link so a test can assert the account was actually linked + selected.
    private sealed class RecordingLinkStore(PortalTenantLink? existing) : IPortalTenantLinkStore
    {
        public List<(string Email, string TenantId)> KeylessUpserts { get; } = [];

        public Task<PortalTenantLink?> GetAsync(string email, CancellationToken cancellationToken = default) =>
            Task.FromResult(existing);

        public Task<PortalTenantLink> UpsertAsync(string email, string tenantId, string apiKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("signup provisioning is keyless (console-mode)");

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
