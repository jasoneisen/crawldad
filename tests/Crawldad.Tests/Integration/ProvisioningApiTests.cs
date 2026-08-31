using System.Text.Json;
using Alba;
using Crawldad.Api.Features.Tenancy;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Tenancy;
using Crawldad.Tests.Support;
using Marten;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>The self-serve free-tier provisioning surface end-to-end (issue #119 PR7) on the console-enabled host: a
/// console user with NO membership can create their one free workspace (the exception surface), the free defaults land and are
/// reported as they are enforced, an Owner membership + a lifetime marker + an audit row are written, one free tenant per email
/// EVER holds even after the created membership is removed and under a concurrent double-submit, and a key-authed caller is
/// rejected (this endpoint is the console scheme only).</summary>
[Collection(ProvisioningCollection.Name)]
public sealed class ProvisioningApiTests(ProvisioningFixture fixture) : IAsyncLifetime
{
    private IAlbaHost Host => fixture.Host;
    private static readonly CancellationToken _ct = CancellationToken.None;

    public Task InitializeAsync() => Host.ResetAllMartenDataAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private ITenantRegistryStore Registry => Host.Services.GetRequiredService<ITenantRegistryStore>();

    private ITenantMembershipStore Memberships => Host.Services.GetRequiredService<ITenantMembershipStore>();

    private IFreeTenantProvisioningStore Provisioning => Host.Services.GetRequiredService<IFreeTenantProvisioningStore>();

    private IConsoleAuditStore Audit => Host.Services.GetRequiredService<IConsoleAuditStore>();

    // A provisioning call: the console bearer token + the console-user selector, but NO workspace selector (there is none yet).
    private Task<IScenarioResult> ProvisionAsync(string? email, object? body = null) =>
        Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", $"Bearer {ConsoleAuthTestHarness.MintToken()}");
            if (email is not null)
            {
                x.WithRequestHeader(ConsoleAuthHeaders.ConsoleUser, email);
            }

            x.Post.Json(body ?? new { }).ToUrl("/provisioning/tenants");
            x.IgnoreStatusCode();
        });

    [Fact]
    public async Task A_console_user_with_no_membership_provisions_a_free_workspace()
    {
        const string email = "founder@crawldad.test";

        var result = await ProvisionAsync(email);
        result.Context.Response.StatusCode.ShouldBe(StatusCodes.Status201Created);

        // The response is the workspace shape: an API-assigned BARE GUID id (no 't-' prefix — ids are opaque), the default
        // name, and the creator's Owner role.
        var workspace = await result.ReadAsJsonAsync<WorkspaceSummary>();
        workspace.TenantId.ShouldNotStartWith("t-");
        Guid.TryParse(workspace.TenantId, out _).ShouldBeTrue();
        workspace.DisplayName.ShouldBe(ProvisioningEndpoint.DefaultDisplayName);
        workspace.Role.ShouldBe(MembershipRole.Owner);

        // Registry consistency: the free-tier tenant exists with the free defaults and no minted key.
        var tenant = await Registry.FindAsync(workspace.TenantId, _ct);
        tenant.ShouldNotBeNull();
        tenant.Tier.ShouldBe(ProvisioningEndpoint.FreeTier);
        tenant.SlotAllowance.ShouldBe(ProvisioningEndpoint.FreeSlotAllowance);
        tenant.Status.ShouldBe(TenantStatus.Active);
        (await Registry.ListKeysAsync(workspace.TenantId, _ct)).ShouldBeEmpty(); // console users need no key

        // The creator is recorded as Owner, and the lifetime marker points at this workspace.
        var membership = await Memberships.FindActiveAsync(workspace.TenantId, email, _ct);
        membership.ShouldNotBeNull();
        membership.Role.ShouldBe(MembershipRole.Owner);
        (await Provisioning.FindMarkerAsync(email, _ct))!.TenantId.ShouldBe(workspace.TenantId);

        // The provision is audited (attribution) with the human email + the outcome.
        var row = (await Audit.ListForTenantAsync(workspace.TenantId, _ct)).ShouldHaveSingleItem();
        row.Email.ShouldBe(email);
        row.Operation.ShouldBe("POST");
        row.Route.ShouldBe("/provisioning/tenants");
        row.StatusCode.ShouldBe(StatusCodes.Status201Created);
    }

    [Fact]
    public async Task The_free_defaults_are_reported_as_they_are_enforced()
    {
        const string email = "reporter@crawldad.test";
        var workspace = await (await ProvisionAsync(email)).ReadAsJsonAsync<WorkspaceSummary>();

        // GET /tenant via the console (now a member of the new workspace) reports the SAME slot allowance the admission gate
        // enforces — report == enforcement, via TenantProfileResolution (registry-first).
        var profile = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", $"Bearer {ConsoleAuthTestHarness.MintToken()}");
            x.WithRequestHeader(ConsoleAuthHeaders.ConsoleUser, email);
            x.WithRequestHeader(ConsoleAuthHeaders.Workspace, workspace.TenantId);
            x.Get.Url("/tenant");
            x.StatusCodeShouldBeOk();
        });

        var tenant = await profile.ReadAsJsonAsync<TenantProfileResponse>();
        tenant.SlotAllowance.ShouldBe(ProvisioningEndpoint.FreeSlotAllowance);
        tenant.Tier.ShouldBe(ProvisioningEndpoint.FreeTier);
    }

    [Fact]
    public async Task A_supplied_display_name_is_used()
    {
        var workspace = await (await ProvisionAsync("named@crawldad.test", new { displayName = "Acme Automations" }))
            .ReadAsJsonAsync<WorkspaceSummary>();
        workspace.DisplayName.ShouldBe("Acme Automations");
    }

    [Fact]
    public async Task One_free_tenant_per_email_ever()
    {
        const string email = "repeat@crawldad.test";
        var first = await (await ProvisionAsync(email)).ReadAsJsonAsync<WorkspaceSummary>();

        // The second attempt is refused with a 409 and the EXISTING workspace id as the tenantId extension (for portal recovery).
        var second = await ProvisionAsync(email);
        second.Context.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        var problem = await second.ReadAsJsonAsync<JsonElement>();
        problem.GetProperty("title").GetString().ShouldBe(ProvisioningProblems.AlreadyProvisionedTitle);
        problem.GetProperty("tenantId").GetString().ShouldBe(first.TenantId);

        // Exactly one workspace exists for the email — the second created nothing.
        (await Provisioning.FindMarkerAsync(email, _ct))!.TenantId.ShouldBe(first.TenantId);
    }

    [Fact]
    public async Task The_entitlement_survives_removal_of_the_created_membership()
    {
        const string email = "leaver@crawldad.test";
        var workspace = await (await ProvisionAsync(email)).ReadAsJsonAsync<WorkspaceSummary>();

        // Add a second Owner, then revoke the founder's membership — allowed (not the last Owner). The founder now holds NO
        // active membership in the workspace, but the LIFETIME marker is keyed by email and never removed.
        await Memberships.CreateAsync(workspace.TenantId, "cofounder@crawldad.test", MembershipRole.Owner, DateTimeOffset.UnixEpoch, _ct);
        var founder = (await Memberships.FindActiveAsync(workspace.TenantId, email, _ct))!;
        (await Memberships.RevokeAsync(workspace.TenantId, founder.Id, DateTimeOffset.UnixEpoch, _ct)).ShouldBe(MembershipRevokeOutcome.Revoked);
        (await Memberships.FindActiveAsync(workspace.TenantId, email, _ct)).ShouldBeNull(); // membership gone…

        // …yet a re-provision is still refused: the entitlement is "ever provisioned", not "has an active membership".
        (await ProvisionAsync(email)).Context.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task A_concurrent_double_submit_creates_exactly_one_workspace()
    {
        const string email = "racer@crawldad.test";
        var tenantA = FreeTenantFor("t-" + Guid.NewGuid());
        var tenantB = FreeTenantFor("t-" + Guid.NewGuid());

        // Two concurrent provisions for the SAME email, each with its own candidate tenant. The per-email advisory lock
        // serialises them, so exactly one commits and the other sees the marker.
        var results = await Task.WhenAll(
            Provisioning.ProvisionAsync(email, tenantA, DateTimeOffset.UnixEpoch, _ct),
            Provisioning.ProvisionAsync(email, tenantB, DateTimeOffset.UnixEpoch, _ct));

        results.Count(r => r.Outcome == FreeProvisionOutcome.Provisioned).ShouldBe(1);
        results.Count(r => r.Outcome == FreeProvisionOutcome.AlreadyProvisioned).ShouldBe(1);

        var winner = results.Single(r => r.Outcome == FreeProvisionOutcome.Provisioned).TenantId;
        (await Provisioning.FindMarkerAsync(email, _ct))!.TenantId.ShouldBe(winner);

        // Only the winner's tenant document exists; the loser's candidate was never stored.
        var created = new[] { tenantA.Id, tenantB.Id }.Where(id => Registry.FindAsync(id, _ct).GetAwaiter().GetResult() is not null).ToList();
        created.ShouldHaveSingleItem().ShouldBe(winner);
    }

    [Fact]
    public async Task A_key_authenticated_caller_is_rejected()
    {
        // Provisioning is the console scheme ONLY — a valid tenant API key never authenticates here.
        var result = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(TestTenants.PrimaryKey));
            x.Post.Json(new { }).ToUrl("/provisioning/tenants");
            x.IgnoreStatusCode();
        });
        result.Context.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task A_console_token_with_no_console_user_selector_is_a_400()
    {
        var result = await ProvisionAsync(email: null); // token validates as the portal, but names no acting user
        result.Context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        (await result.ReadAsJsonAsync<JsonElement>()).GetProperty("title").GetString().ShouldBe("actor_required");
    }

    private static RegistryTenant FreeTenantFor(string id) => new()
    {
        Id = id,
        DisplayName = ProvisioningEndpoint.DefaultDisplayName,
        Actor = "racer@crawldad.test",
        Tier = ProvisioningEndpoint.FreeTier,
        SlotAllowance = ProvisioningEndpoint.FreeSlotAllowance,
        Status = TenantStatus.Active,
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };
}

/// <summary>The console-enabled host for the provisioning suite: its own isolated schema, a frozen clock, and the test-key
/// swap so the REAL console validator runs against test-issued tokens.</summary>
public sealed class ProvisioningFixture : IAsyncLifetime
{
    public IAlbaHost Host { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Host = await AlbaHost.For<Program>(builder =>
        {
            builder.UseCrawldadTestDefaults("crawldad_iso_pr7");
            builder.UseSetting($"{ConsoleAuthOptions.Section}:TenantId", ConsoleAuthTestHarness.TenantId);
            builder.UseSetting($"{ConsoleAuthOptions.Section}:Audience", ConsoleAuthTestHarness.Audience);

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(new FakeClock());
                services.Configure<JwtBearerOptions>(ConsoleAuthModule.Scheme, ConsoleAuthTestHarness.InjectTestKey);
            });
        });

        await Host.ResetAllMartenDataAsync();
    }

    public Task DisposeAsync() => Host.DisposeAsync().AsTask();
}

/// <summary>The xUnit collection for the provisioning host (its own schema, isolated from the other console hosts).</summary>
[CollectionDefinition(Name)]
public sealed class ProvisioningCollection : ICollectionFixture<ProvisioningFixture>
{
    public const string Name = "provisioning";
}
