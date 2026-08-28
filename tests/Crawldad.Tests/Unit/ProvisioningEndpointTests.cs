using Crawldad.Api.Features.Tenancy;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Tenancy;
using Crawldad.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Crawldad.Tests.Unit;

/// <summary>Branch coverage for the provisioning endpoint handler (issue #119 PR7) in isolation — fake store / audit / rate
/// limiter, so every path is deterministic: a missing console-user selector (400), an over-long display name (400), the rate
/// limit (429), a fresh provision (201), an already-provisioned refusal (409 + the existing tenant), and the best-effort audit
/// (recorded on success/refusal; a store fault is swallowed, never failing the create). The live auth wiring + the real Marten
/// store are exercised end-to-end in the integration suite.</summary>
public class ProvisioningEndpointTests
{
    private static readonly IServiceProvider _services = new ServiceCollection().AddLogging().BuildServiceProvider();
    private const string _email = "new-user@crawldad.test";

    [Fact]
    public async Task Missing_console_user_selector_is_a_400_actor_required()
    {
        var store = new FakeProvisioningStore();
        var (status, _) = await RunAsync(await InvokeAsync(consoleUser: null, store: store));

        status.ShouldBe(StatusCodes.Status400BadRequest);
        store.Calls.ShouldBe(0); // rejected before any DB work
    }

    [Fact]
    public async Task Blank_console_user_selector_is_a_400_actor_required()
    {
        var (status, _) = await RunAsync(await InvokeAsync(consoleUser: "   "));
        status.ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task An_over_long_display_name_is_a_400_validation_problem()
    {
        var store = new FakeProvisioningStore();
        var request = new ProvisionTenantRequest(new string('x', TenantRules.MaxDisplayNameLength + 1));

        var (status, _) = await RunAsync(await InvokeAsync(_email, request, store));

        status.ShouldBe(StatusCodes.Status400BadRequest);
        store.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_fresh_provision_is_a_201_and_is_audited()
    {
        var store = new FakeProvisioningStore(); // default: Provisioned with the passed-in tenant
        var audit = new RecordingAuditStore();

        var (status, _) = await RunAsync(await InvokeAsync(_email, store: store, audit: audit));

        status.ShouldBe(StatusCodes.Status201Created);
        var created = store.LastTenant.ShouldNotBeNull();
        created.Id.ShouldStartWith("t-");
        Guid.TryParse(created.Id["t-".Length..], out _).ShouldBeTrue();   // API-assigned GUID id
        created.Tier.ShouldBe(ProvisioningEndpoint.FreeTier);
        created.SlotAllowance.ShouldBe(ProvisioningEndpoint.FreeSlotAllowance);
        created.Actor.ShouldBe(_email);

        var row = audit.Rows.ShouldHaveSingleItem();
        row.Email.ShouldBe(_email);
        row.Operation.ShouldBe("POST");
        row.Route.ShouldBe(ProvisioningEndpoints.ProvisionRoute);
        row.StatusCode.ShouldBe(StatusCodes.Status201Created);
    }

    [Fact]
    public async Task A_blank_display_name_falls_back_to_the_default()
    {
        var store = new FakeProvisioningStore();

        await RunAsync(await InvokeAsync(_email, new ProvisionTenantRequest("   "), store));

        store.LastTenant!.DisplayName.ShouldBe(ProvisioningEndpoint.DefaultDisplayName);
    }

    [Fact]
    public async Task A_supplied_display_name_is_trimmed_onto_the_tenant()
    {
        var store = new FakeProvisioningStore();

        await RunAsync(await InvokeAsync(_email, new ProvisionTenantRequest("  Acme Corp  "), store));

        store.LastTenant!.DisplayName.ShouldBe("Acme Corp");
    }

    [Fact]
    public async Task An_already_provisioned_email_is_a_409_and_is_audited_with_the_existing_tenant()
    {
        var store = new FakeProvisioningStore { Result = new FreeProvisionResult(FreeProvisionOutcome.AlreadyProvisioned, "t-existing") };
        var audit = new RecordingAuditStore();

        var (status, body) = await RunAsync(await InvokeAsync(_email, store: store, audit: audit));

        status.ShouldBe(StatusCodes.Status409Conflict);
        body.ShouldContain(ProvisioningProblems.AlreadyProvisionedTitle);
        body.ShouldContain("t-existing"); // the existing workspace rides as the tenantId extension

        var row = audit.Rows.ShouldHaveSingleItem();
        row.TenantId.ShouldBe("t-existing");
        row.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task Over_the_rate_limit_is_a_429_before_the_store_or_audit()
    {
        // PermitLimit = 1: the first attempt provisions, the second (same email) is rate-limited before the handler touches
        // the store or the audit — so the limiter also bounds audit volume under abuse.
        var store = new FakeProvisioningStore();
        var audit = new RecordingAuditStore();
        var limiter = LimiterWithPermit(1);

        (await RunAsync(await InvokeAsync(_email, store: store, audit: audit, limiter: limiter))).Status.ShouldBe(StatusCodes.Status201Created);
        var (status, _) = await RunAsync(await InvokeAsync(_email, store: store, audit: audit, limiter: limiter));

        status.ShouldBe(StatusCodes.Status429TooManyRequests);
        store.Calls.ShouldBe(1);       // the second attempt never reached the store
        audit.Rows.Count.ShouldBe(1);  // …nor the audit
    }

    [Fact]
    public async Task A_different_email_is_not_rate_limited_by_another_accounts_attempts()
    {
        var limiter = LimiterWithPermit(1);
        (await RunAsync(await InvokeAsync("a@crawldad.test", limiter: limiter))).Status.ShouldBe(StatusCodes.Status201Created);

        // The limiter partitions per email, so a second account's first attempt is admitted.
        (await RunAsync(await InvokeAsync("b@crawldad.test", limiter: limiter))).Status.ShouldBe(StatusCodes.Status201Created);
    }

    [Fact]
    public async Task An_audit_store_fault_never_fails_the_create()
    {
        // The tenant is already committed by the store; a failing audit append is telemetry and must be swallowed.
        var (status, _) = await RunAsync(await InvokeAsync(_email, audit: new FaultingAuditStore()));
        status.ShouldBe(StatusCodes.Status201Created);
    }

    private static ConsoleWriteRateLimiter LimiterWithPermit(int permit) =>
        new(Options.Create(new ConsoleWriteOptions { PermitLimit = permit, WindowSeconds = 60 }), new FakeClock());

    private static Task<IResult> InvokeAsync(
        string? consoleUser,
        ProvisionTenantRequest? request = null,
        FakeProvisioningStore? store = null,
        IConsoleAuditStore? audit = null,
        ConsoleWriteRateLimiter? limiter = null)
    {
        var http = new DefaultHttpContext { RequestServices = _services };
        if (consoleUser is not null)
        {
            http.Request.Headers[ConsoleAuthHeaders.ConsoleUser] = consoleUser;
        }

        return ProvisioningEndpoint.Provision(
            request,
            http,
            store ?? new FakeProvisioningStore(),
            limiter ?? LimiterWithPermit(240),
            audit ?? new RecordingAuditStore(),
            new FakeClock(),
            NullLogger<ProvisioningEndpointLog>.Instance,
            CancellationToken.None);
    }

    private static async Task<(int Status, string Body)> RunAsync(IResult result)
    {
        var http = new DefaultHttpContext { RequestServices = _services };
        using var stream = new MemoryStream();
        http.Response.Body = stream;
        await result.ExecuteAsync(http);
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        return (http.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private sealed class FakeProvisioningStore : IFreeTenantProvisioningStore
    {
        public FreeProvisionResult? Result { get; init; } // null → echo a Provisioned result for the passed-in tenant

        public int Calls { get; private set; }

        public RegistryTenant? LastTenant { get; private set; }

        public Task<FreeProvisionResult> ProvisionAsync(string email, RegistryTenant tenant, DateTimeOffset now, CancellationToken ct)
        {
            Calls++;
            LastTenant = tenant;
            return Task.FromResult(Result ?? new FreeProvisionResult(FreeProvisionOutcome.Provisioned, tenant.Id));
        }

        public Task<FreeTenantEntitlement?> FindMarkerAsync(string email, CancellationToken ct) =>
            Task.FromResult<FreeTenantEntitlement?>(null);
    }

    private sealed class RecordingAuditStore : IConsoleAuditStore
    {
        public List<ConsoleAuditEntry> Rows { get; } = [];

        public Task RecordAsync(ConsoleAuditEntry entry, CancellationToken ct)
        {
            Rows.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ConsoleAuditEntry>> ListForTenantAsync(string tenantId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ConsoleAuditEntry>>([.. Rows]);
    }

    private sealed class FaultingAuditStore : IConsoleAuditStore
    {
        public Task RecordAsync(ConsoleAuditEntry entry, CancellationToken ct) =>
            throw new InvalidOperationException("simulated audit store fault");

        public Task<IReadOnlyList<ConsoleAuditEntry>> ListForTenantAsync(string tenantId, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
