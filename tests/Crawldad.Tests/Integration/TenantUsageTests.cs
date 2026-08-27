using System.Text.Json;
using Alba;
using Crawldad.Api.Features.Runs;
using Crawldad.Contracts;
using Crawldad.Contracts.Runs;
using Crawldad.Contracts.Tenancy;
using Crawldad.Tests.Support;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Crawldad.Tests.Integration;

/// <summary>GET /tenant and GET /usage: the tenant's own profile (id, display name, tier, allowances) and its live
/// capacity/consumption. The primary tenant carries overrides + a tier; the secondary stays on the global defaults, so
/// both resolution paths are covered. Usage is seeded deterministically — a held gate slot, queued rows, recorded queue
/// waits, and terminal run summaries with known event counts.</summary>
[Collection(DashboardCollection.Name)]
public sealed class TenantUsageTests(DashboardFixture fixture) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions _wire = Wire();
    private static readonly DateTimeOffset _t0 = FakeClock.Fixed;

    private IAlbaHost Host => fixture.Host;
    private IDocumentStore Store => Host.Services.GetRequiredService<IDocumentStore>();

    public Task InitializeAsync() => Host.ResetAllMartenDataAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static JsonSerializerOptions Wire()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ContractsJson.Configure(options);
        return options;
    }

    private async Task<T> GetAsync<T>(string url, string? apiKey = null)
    {
        var result = await Host.Scenario(x =>
        {
            if (apiKey is not null)
            {
                x.RemoveRequestHeader("Authorization");
                x.WithRequestHeader("Authorization", TestTenants.Bearer(apiKey));
            }

            x.Get.Url(url);
            x.StatusCodeShouldBeOk();
        });
        return JsonSerializer.Deserialize<T>(await result.ReadAsTextAsync(), _wire)!;
    }

    [Fact]
    public async Task Tenant_reports_its_actor_tier_and_overridden_allowances()
    {
        var tenant = await GetAsync<TenantProfileResponse>("/tenant");
        tenant.TenantId.ShouldBe(TestTenants.PrimaryId);
        tenant.DisplayName.ShouldBe(TestTenants.PrimaryActor);
        tenant.Tier.ShouldBe("pro");
        tenant.SlotAllowance.ShouldBe(5);       // the tenant-alpha override
        tenant.QueueDepthAllowance.ShouldBe(20); // the tenant-alpha override
    }

    [Fact]
    public async Task Tenant_falls_back_to_global_defaults_when_unset()
    {
        var limits = Host.Services.GetRequiredService<IOptions<RunLimitsOptions>>().Value;

        var tenant = await GetAsync<TenantProfileResponse>("/tenant", apiKey: TestTenants.SecondaryKey);
        tenant.TenantId.ShouldBe(TestTenants.SecondaryId);
        tenant.DisplayName.ShouldBe(TestTenants.SecondaryActor);
        tenant.Tier.ShouldBeNull(); // no tier configured — omitted
        tenant.SlotAllowance.ShouldBe(limits.MaxConcurrentRunsPerTenant);
        tenant.QueueDepthAllowance.ShouldBe(limits.MaxQueueDepthPerTenant);
    }

    [Fact]
    public async Task Usage_reports_slot_occupancy_now_under_a_held_slot()
    {
        var gate = Host.Services.GetRequiredService<IRunAdmissionGate>();
        var held = Guid.NewGuid();
        gate.Occupy(TestTenants.PrimaryId, held);
        try
        {
            var usage = await GetAsync<UsageResponse>("/usage");
            usage.Slots.InUse.ShouldBe(1);
            usage.Slots.Allowance.ShouldBe(5); // the tenant-alpha override
        }
        finally
        {
            gate.Release(TestTenants.PrimaryId, held); // never leak the in-memory slot into a sibling test
        }
    }

    [Fact]
    public async Task Usage_reports_queue_depth_and_p95_wait()
    {
        await using (var session = Store.LightweightSession(TestTenants.PrimaryId))
        {
            session.Store(Queued(1), Queued(2)); // two waiting → depth 2
            // Three recorded per-run waits → nearest-rank p95 = the 3rd smallest.
            session.Store(
                new RunProgress { Id = Guid.NewGuid(), Status = RunStatus.Succeeded, QueueWaitMs = 100 },
                new RunProgress { Id = Guid.NewGuid(), Status = RunStatus.Succeeded, QueueWaitMs = 200 },
                new RunProgress { Id = Guid.NewGuid(), Status = RunStatus.Succeeded, QueueWaitMs = 300 });
            await session.SaveChangesAsync();
        }

        var usage = await GetAsync<UsageResponse>("/usage");
        usage.Queue.Depth.ShouldBe(2);
        usage.Queue.Sampled.ShouldBe(3);
        usage.Queue.P95WaitMs.ShouldBe(300);
    }

    [Fact]
    public async Task Usage_counts_runs_this_month_and_averages_events_per_run()
    {
        var limits = Host.Services.GetRequiredService<IOptions<RunLimitsOptions>>().Value;

        // Three terminal runs this month with known event-stream lengths: 2, 3, 4 → avg 3, max 4.
        await SeedTerminalAsync(2);
        await SeedTerminalAsync(3);
        await SeedTerminalAsync(4);

        var usage = await GetAsync<UsageResponse>("/usage");
        usage.RunsStartedThisMonth.ShouldBe(3);
        usage.Events.Guardrail.ShouldBe(limits.MaxEventsPerRun);
        usage.Events.Sampled.ShouldBe(3);
        usage.Events.Avg.ShouldBe(3);
        usage.Events.Max.ShouldBe(4);
    }

    [Fact]
    public async Task Usage_is_all_zero_for_a_tenant_with_no_activity()
    {
        var usage = await GetAsync<UsageResponse>("/usage", apiKey: TestTenants.SecondaryKey);
        usage.Slots.InUse.ShouldBe(0);
        usage.Queue.Depth.ShouldBe(0);
        usage.Queue.Sampled.ShouldBe(0);
        usage.Queue.P95WaitMs.ShouldBe(0);
        usage.RunsStartedThisMonth.ShouldBe(0);
        usage.Events.Sampled.ShouldBe(0);
        usage.Events.Avg.ShouldBe(0);
        usage.Events.Max.ShouldBe(0);
    }

    // A terminal run whose event stream is exactly `events` long (RunStarted + (events-2) session-opens + RunSucceeded),
    // so its recorded EventCount (the terminal event's stream version) is deterministic.
    private async Task SeedTerminalAsync(int events)
    {
        var stream = new List<object> { new RunStarted("u.run", "h", _t0, [], null, null) };
        for (var i = 0; i < events - 2; i++)
        {
            stream.Add(new RunSessionOpened("us-east", _t0.AddSeconds(i + 1)));
        }

        stream.Add(new RunSucceeded(new RunStats(0, 0, 0, 0, 0, 0), _t0.AddSeconds(events)));

        await using var session = Store.LightweightSession(TestTenants.PrimaryId);
        session.Events.StartStream<Run>(Guid.NewGuid(), [.. stream]);
        await session.SaveChangesAsync();
    }

    private static QueuedRun Queued(int sequence) => new()
    {
        Id = Guid.NewGuid(),
        Sequence = sequence,
        PayloadName = "u.queued",
        ScriptHash = "h",
        Script = "{}",
        Inputs = "{}",
        DeadlineMs = 30_000,
        QueuedAt = _t0,
    };
}
