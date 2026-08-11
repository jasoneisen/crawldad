using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Runs;
using Crawldad.Web.Infrastructure.Browser.Fake;
using Crawldad.Web.Infrastructure.Security;
using Crawldad.Web.Infrastructure.Storage;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Crawldad.Tests.Integration;

/// <summary>Result retention (issue #71): the <see cref="RunResultRetentionSweep"/> ages an async run's stored
/// <c>result</c>/<c>partial</c> body out of <see cref="RunProgress"/> once past its TTL — nulling the body and stamping a
/// <c>resultExpiredAt</c> marker while leaving the terminal status/stats queryable — tenant-correct across every
/// configured tenant, and inert when disabled. Drives the sweep with a chosen <c>now</c> (it is pure w.r.t. it), so
/// expiry is deterministic under the frozen test clock.</summary>
public class RunResultRetentionTests
{
    // A cutoff clock: runs finalise at FakeClock.Fixed, so a sweep at Fixed+10d with a 7-day TTL (cutoff Fixed+3d) ages
    // out anything finished at/near Fixed while sparing a row finished after the cutoff.
    private static readonly DateTimeOffset _sweepNow = FakeClock.Fixed.AddDays(10);

    [Fact]
    public async Task An_expired_result_is_nulled_with_a_marker_and_the_poll_stays_coherent()
    {
        await using var host = await DurableHost.BuildAsync("crawldad_result_ttl_e2e", new FakeBrowserBackend(Runner.FixturesRoot));
        var runId = await RunAsyncToSucceededAsync(host);

        // The finaliser persisted the scrubbed result body AND stamped the retention clock; nothing has expired yet.
        var before = await LoadAsync(host, TestTenants.PrimaryId, runId);
        before!.ResultJson.ShouldNotBeNull();
        before.FinishedAt.ShouldBe(FakeClock.Fixed);
        before.ResultExpiredAt.ShouldBeNull();

        var expired = await Sweep(host, TimeSpan.FromDays(7)).SweepAsync(_sweepNow, CancellationToken.None);
        expired.ShouldBe(1);

        // What expired: the body is gone, the status/stats tombstone + the marker remain (NOT the whole document).
        var after = await LoadAsync(host, TestTenants.PrimaryId, runId);
        after!.ResultJson.ShouldBeNull();
        after.PartialJson.ShouldBeNull();
        after.Status.ShouldBe(RunStatus.Succeeded);
        after.Stats.ShouldNotBeNull();
        after.ResultExpiredAt.ShouldBe(_sweepNow);

        // GET /runs/{id} stays coherent: 200 with status + stats + resultExpiredAt and NO result body (not a 404).
        var body = await GetAsync(host, runId);
        body.GetProperty("status").GetString().ShouldBe("succeeded");
        body.TryGetProperty("result", out _).ShouldBeFalse();
        body.TryGetProperty("stats", out _).ShouldBeTrue();
        body.GetProperty("resultExpiredAt").GetDateTimeOffset().ShouldBe(_sweepNow);
    }

    [Fact]
    public async Task Sweep_nulls_expired_bodies_keeps_fresh_skips_bodyless_and_spans_tenants()
    {
        await using var host = await DurableHost.BuildAsync("crawldad_result_ttl_shapes", new FakeBrowserBackend(Runner.FixturesRoot));

        // Tenant A: a succeeded result + a cancelled partial, both aged out; a fresh result (finished after the cutoff);
        // and a failed run (no body). Tenant B: its own aged-out result — proving the fan-out over every configured tenant.
        var expiredResult = Terminal(RunStatus.Succeeded, FakeClock.Fixed, resultJson: """{"owner":"Jane Doe"}""");
        var expiredPartial = Terminal(RunStatus.Cancelled, FakeClock.Fixed, partialJson: "[1,2,3]");
        var fresh = Terminal(RunStatus.Succeeded, FakeClock.Fixed.AddDays(5), resultJson: """{"x":1}""");
        var failed = Terminal(RunStatus.Failed, FakeClock.Fixed, failure: new RunFailureDetail("terminal", "boom", "it broke", new RunStepRef(0, "run")));
        var tenantB = Terminal(RunStatus.Succeeded, FakeClock.Fixed, resultJson: """{"owner":"John Roe"}""");

        await SeedAsync(host, TestTenants.PrimaryId, expiredResult, expiredPartial, fresh, failed);
        await SeedAsync(host, TestTenants.SecondaryId, tenantB);

        var expired = await Sweep(host, TimeSpan.FromDays(7)).SweepAsync(_sweepNow, CancellationToken.None);
        expired.ShouldBe(3); // A's result + A's partial + B's result

        (await LoadAsync(host, TestTenants.PrimaryId, expiredResult.Id))!.ResultJson.ShouldBeNull();
        (await LoadAsync(host, TestTenants.PrimaryId, expiredResult.Id))!.ResultExpiredAt.ShouldBe(_sweepNow);
        (await LoadAsync(host, TestTenants.PrimaryId, expiredPartial.Id))!.PartialJson.ShouldBeNull();

        // Fresh result untouched (within its window), and a bodyless failure is never selected — its failure detail stays.
        var keptFresh = await LoadAsync(host, TestTenants.PrimaryId, fresh.Id);
        keptFresh!.ResultJson.ShouldNotBeNull();
        keptFresh.ResultExpiredAt.ShouldBeNull();
        var keptFailed = await LoadAsync(host, TestTenants.PrimaryId, failed.Id);
        keptFailed!.Failure.ShouldNotBeNull();
        keptFailed.ResultExpiredAt.ShouldBeNull();

        // Tenant B's row expired under its own partition — the sweep really visited B, not just A.
        (await LoadAsync(host, TestTenants.SecondaryId, tenantB.Id))!.ResultJson.ShouldBeNull();
    }

    [Fact]
    public async Task Result_retention_disabled_keeps_stored_results_indefinitely()
    {
        await using var host = await DurableHost.BuildAsync("crawldad_result_ttl_off", new FakeBrowserBackend(Runner.FixturesRoot));
        var row = Terminal(RunStatus.Succeeded, FakeClock.Fixed, resultJson: """{"pii":"kept"}""");
        await SeedAsync(host, TestTenants.PrimaryId, row);

        // TTL ≤ 0 disables the sweep: even a decade later, nothing is expired.
        var expired = await Sweep(host, TimeSpan.Zero).SweepAsync(FakeClock.Fixed.AddDays(3650), CancellationToken.None);

        expired.ShouldBe(0);
        var kept = await LoadAsync(host, TestTenants.PrimaryId, row.Id);
        kept!.ResultJson.ShouldNotBeNull();
        kept.ResultExpiredAt.ShouldBeNull();
    }

    // Builds a sweep over the host's real store + tenant registry with a chosen TTL, so a test drives expiry without
    // reconfiguring the host.
    private static RunResultRetentionSweep Sweep(IAlbaHost host, TimeSpan resultTtl) =>
        new(host.Services.GetRequiredService<IDocumentStore>(),
            host.Services.GetRequiredService<TenantRegistry>(),
            Options.Create(new StorageOptions { Retention = new RetentionOptions { ResultTtl = resultTtl } }));

    private static RunProgress Terminal(RunStatus status, DateTimeOffset finishedAt, string? resultJson = null, string? partialJson = null, RunFailureDetail? failure = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Status = status,
            ResultJson = resultJson,
            PartialJson = partialJson,
            Failure = failure,
            Stats = new RunStats(0, 0, 0, 0, 0, 0),
            FinishedAt = finishedAt,
        };

    private static async Task SeedAsync(IAlbaHost host, string tenantId, params RunProgress[] rows)
    {
        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenantId);
        session.Store(rows);
        await session.SaveChangesAsync();
    }

    private static async Task<RunProgress?> LoadAsync(IAlbaHost host, string tenantId, Guid id)
    {
        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(tenantId);
        return await session.LoadAsync<RunProgress>(id);
    }

    private static async Task<Guid> RunAsyncToSucceededAsync(IAlbaHost host)
    {
        var body = new JsonObject
        {
            ["payload"] = JsonNode.Parse("""{ "config": { "backend": "input.backend" }, "vars": {}, "steps": [], "result": "'ok'" }"""),
            ["inputs"] = new JsonObject { ["backend"] = new JsonObject { ["adapter"] = "fake", ["options"] = new JsonObject { ["fixture"] = "caphome-search" } } },
            ["async"] = true,
        };
        var accepted = await host.Scenario(x =>
        {
            x.Post.Json(body).ToUrl("/runs");
            x.StatusCodeShouldBe(202);
        });
        var runId = (await accepted.ReadAsJsonAsync<JsonElement>()).GetProperty("runId").GetGuid();
        (await DurableHost.PollUntilTerminalAsync(host, runId, DurableHost.PollTimeout)).GetProperty("status").GetString().ShouldBe("succeeded");
        return runId;
    }

    private static async Task<JsonElement> GetAsync(IAlbaHost host, Guid runId)
    {
        var result = await host.Scenario(x =>
        {
            x.Get.Url($"/runs/{runId}");
            x.StatusCodeShouldBeOk();
        });
        return (await result.ReadAsJsonAsync<JsonElement>()).Clone();
    }
}
