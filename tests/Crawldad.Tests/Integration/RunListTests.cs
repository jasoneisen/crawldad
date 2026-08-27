using System.Text.Json;
using Alba;
using Crawldad.Api;
using Crawldad.Api.Features.Runs;
using Crawldad.Contracts;
using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>A shared Alba host for the dashboard read suite (its own Marten schema), configured so the primary tenant
/// carries slot/queue/tier overrides and the secondary tenant keeps the global defaults — so GET /tenant and GET /usage
/// exercise both the override and default resolution paths. A frozen clock keeps seeded run timestamps deterministic.</summary>
public sealed class DashboardFixture : IAsyncLifetime
{
    public IAlbaHost Host { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Host = (await AlbaHost.For<Program>(builder =>
        {
            builder.UseCrawldadTestDefaults("crawldad_dashboard");

            // The primary tenant (index 0 = tenant-alpha) gets explicit tier + slot/queue overrides; the secondary stays
            // on the global defaults. Layered AFTER the defaults so these keys win.
            builder.UseSetting("Crawldad:Tenants:0:Tier", "pro");
            builder.UseSetting("Crawldad:Tenants:0:MaxConcurrentRuns", "5");
            builder.UseSetting("Crawldad:Tenants:0:MaxQueueDepth", "20");

            builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(new FakeClock()));
        })).AuthenticatedAsPrimaryTenant();

        await Host.ResetAllMartenDataAsync();
    }

    public Task DisposeAsync() => Host.DisposeAsync().AsTask();
}

/// <summary>The dashboard read suite shares one host, sequentially, each test resetting Marten data first.</summary>
[CollectionDefinition(Name)]
public sealed class DashboardCollection : ICollectionFixture<DashboardFixture>
{
    public const string Name = "dashboard";
}

/// <summary>GET /runs: the tenant-scoped, filtered, offset-paginated runs list off the RunSummary projection. Seeds run
/// event streams directly (inline projections build the summary synchronously) with explicit timestamps, so ordering,
/// paging, filtering, the pinned/inline distinction, and tenant isolation are all deterministic.</summary>
[Collection(DashboardCollection.Name)]
public sealed class RunListTests(DashboardFixture fixture) : IAsyncLifetime
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

    private async Task<Guid> SeedAsync(string tenant, params object[] events)
    {
        var runId = Guid.NewGuid();
        await using var session = Store.LightweightSession(tenant);
        session.Events.StartStream<Run>(runId, events);
        await session.SaveChangesAsync();
        return runId;
    }

    // A pinned, succeeded run (with a region + stats) started at _t0 + the given offset.
    private Task<Guid> SeedPinnedSucceededAsync(string tenant, Guid payloadId, int minute, string region = "us-east", int steps = 5, int requests = 12, int selectorMisses = 2) =>
        SeedAsync(
            tenant,
            new RunStarted("payload.pinned", "hashp", _t0.AddMinutes(minute), [], payloadId, 3),
            new RunSessionOpened(region, _t0.AddMinutes(minute).AddSeconds(1)),
            new RunSucceeded(new RunStats(1500, steps, requests, 0, 0, selectorMisses), _t0.AddMinutes(minute).AddSeconds(10)));

    private Task<Guid> SeedInlineFailedAsync(string tenant, int minute) =>
        SeedAsync(
            tenant,
            new RunStarted("inline.run", "hashx", _t0.AddMinutes(minute), [], null, null),
            new RunFailed(new RunFailureDetail("terminal", "nav_failed", "boom", new RunStepRef(1, "goto")), new RunStats(500, 2, 3, 0, 0, 0), _t0.AddMinutes(minute).AddSeconds(5)));

    private async Task<JsonElement> ListAsync(string query, string? apiKey = null)
    {
        var result = await Host.Scenario(x =>
        {
            if (apiKey is not null)
            {
                x.RemoveRequestHeader("Authorization");
                x.WithRequestHeader("Authorization", TestTenants.Bearer(apiKey));
            }

            x.Get.Url("/runs" + query);
            x.StatusCodeShouldBeOk();
        });
        return (await result.ReadAsJsonAsync<JsonElement>())!;
    }

    private async Task<RunListResponse> ListTypedAsync(string query)
    {
        var result = await Host.Scenario(x =>
        {
            x.Get.Url("/runs" + query);
            x.StatusCodeShouldBeOk();
        });
        return JsonSerializer.Deserialize<RunListResponse>(await result.ReadAsTextAsync(), _wire)!;
    }

    [Fact]
    public async Task Lists_runs_newest_first_paginated_with_a_total()
    {
        var payload = Guid.NewGuid();
        await SeedPinnedSucceededAsync(TestTenants.PrimaryId, payload, minute: 1);
        var mid = await SeedPinnedSucceededAsync(TestTenants.PrimaryId, payload, minute: 2);
        var newest = await SeedPinnedSucceededAsync(TestTenants.PrimaryId, payload, minute: 3);

        var page1 = await ListTypedAsync("?size=2&page=1");
        page1.Total.ShouldBe(3);
        page1.Page.ShouldBe(1);
        page1.Size.ShouldBe(2);
        page1.HasMore.ShouldBeTrue();
        page1.Runs.Select(r => r.RunId).ShouldBe([newest, mid]); // newest first

        var page2 = await ListTypedAsync("?size=2&page=2");
        page2.Total.ShouldBe(3);
        page2.HasMore.ShouldBeFalse();
        page2.Runs.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_pinned_succeeded_row_carries_the_list_view_fields()
    {
        var payload = Guid.NewGuid();
        await SeedPinnedSucceededAsync(TestTenants.PrimaryId, payload, minute: 1, region: "eu-west", steps: 7, requests: 9, selectorMisses: 1);

        var row = (await ListTypedAsync("")).Runs.ShouldHaveSingleItem();
        row.Status.ShouldBe(RunStatus.Succeeded);
        row.StartedAt.ShouldBe(_t0.AddMinutes(1));
        row.DurationMs.ShouldBe(1500);
        row.Region.ShouldBe("eu-west");
        row.PayloadName.ShouldBe("payload.pinned");
        row.PayloadId.ShouldBe(payload);
        row.PayloadRevision.ShouldBe(3);
        row.Inline.ShouldBeFalse();
        row.Failure.ShouldBeNull();
        row.Stats.ShouldNotBeNull();
        row.Stats!.Steps.ShouldBe(7);
        row.Stats.Requests.ShouldBe(9);
        row.Stats.SelectorMisses.ShouldBe(1);
    }

    [Fact]
    public async Task An_inline_failed_row_is_marked_inline_and_carries_the_failure_class_and_code()
    {
        await SeedInlineFailedAsync(TestTenants.PrimaryId, minute: 1);

        var row = (await ListTypedAsync("")).Runs.ShouldHaveSingleItem();
        row.Status.ShouldBe(RunStatus.Failed);
        row.Inline.ShouldBeTrue();
        row.PayloadId.ShouldBeNull();
        row.PayloadRevision.ShouldBeNull();
        row.Failure.ShouldNotBeNull();
        row.Failure!.Class.ShouldBe("terminal");
        row.Failure.Code.ShouldBe("nav_failed");
    }

    [Fact]
    public async Task A_queued_row_omits_the_terminal_only_fields()
    {
        // A queued run: RunQueued opener, no terminal — status queued, no duration/stats/failure/region.
        await SeedAsync(TestTenants.PrimaryId, new RunQueued("payload.q", "hashq", _t0.AddMinutes(1), [], Guid.NewGuid(), 1));

        var row = (await ListTypedAsync("")).Runs.ShouldHaveSingleItem();
        row.Status.ShouldBe(RunStatus.Queued);
        row.DurationMs.ShouldBeNull();
        row.Stats.ShouldBeNull();
        row.Failure.ShouldBeNull();
        row.Region.ShouldBeNull();
    }

    [Fact]
    public async Task A_promoted_run_stamps_its_execution_start_and_region()
    {
        // RunQueued → RunDequeued (execution start overwrites the enqueue instant) → session open → succeeded.
        await SeedAsync(
            TestTenants.PrimaryId,
            new RunQueued("payload.p", "hashp", _t0.AddMinutes(1), [], null, null),
            new RunDequeued(_t0.AddMinutes(4), QueueWaitMs: 180_000),
            new RunSessionOpened("ap-south", _t0.AddMinutes(4).AddSeconds(1)),
            new RunSucceeded(new RunStats(2000, 3, 4, 0, 0, 0), _t0.AddMinutes(4).AddSeconds(30)));

        var row = (await ListTypedAsync("")).Runs.ShouldHaveSingleItem();
        row.Status.ShouldBe(RunStatus.Succeeded);
        row.StartedAt.ShouldBe(_t0.AddMinutes(4)); // the promotion instant, not the enqueue instant
        row.Region.ShouldBe("ap-south");
    }

    [Fact]
    public async Task A_cancelled_run_lists_as_cancelled()
    {
        await SeedAsync(
            TestTenants.PrimaryId,
            new RunStarted("payload.c", "hashc", _t0.AddMinutes(1), [], null, null),
            new RunCancelled(new RunStats(300, 1, 1, 0, 0, 0), _t0.AddMinutes(1).AddSeconds(5)));

        var row = (await ListTypedAsync("?status=cancelled")).Runs.ShouldHaveSingleItem();
        row.Status.ShouldBe(RunStatus.Cancelled);
    }

    [Fact]
    public async Task Filters_by_status()
    {
        await SeedPinnedSucceededAsync(TestTenants.PrimaryId, Guid.NewGuid(), minute: 1);
        await SeedInlineFailedAsync(TestTenants.PrimaryId, minute: 2);

        var failed = await ListTypedAsync("?status=failed");
        failed.Runs.ShouldHaveSingleItem().Status.ShouldBe(RunStatus.Failed);

        var succeeded = await ListTypedAsync("?status=succeeded");
        succeeded.Runs.ShouldHaveSingleItem().Status.ShouldBe(RunStatus.Succeeded);
    }

    [Fact]
    public async Task Filters_by_payload_id()
    {
        var payload = Guid.NewGuid();
        await SeedPinnedSucceededAsync(TestTenants.PrimaryId, payload, minute: 1);
        await SeedInlineFailedAsync(TestTenants.PrimaryId, minute: 2); // inline: no payloadId

        var mine = await ListTypedAsync($"?payloadId={payload}");
        mine.Runs.ShouldHaveSingleItem().PayloadId.ShouldBe(payload);
    }

    [Fact]
    public async Task Filters_by_an_inclusive_time_range()
    {
        await SeedPinnedSucceededAsync(TestTenants.PrimaryId, Guid.NewGuid(), minute: 1);
        await SeedPinnedSucceededAsync(TestTenants.PrimaryId, Guid.NewGuid(), minute: 5);
        await SeedPinnedSucceededAsync(TestTenants.PrimaryId, Guid.NewGuid(), minute: 9);

        var from = Uri.EscapeDataString(_t0.AddMinutes(3).ToString("O"));
        var to = Uri.EscapeDataString(_t0.AddMinutes(7).ToString("O"));
        var windowed = await ListTypedAsync($"?from={from}&to={to}");
        windowed.Runs.ShouldHaveSingleItem().StartedAt.ShouldBe(_t0.AddMinutes(5));
    }

    [Fact]
    public async Task An_unparseable_time_bound_is_ignored_rather_than_rejected()
    {
        await SeedPinnedSucceededAsync(TestTenants.PrimaryId, Guid.NewGuid(), minute: 1);

        var all = await ListTypedAsync("?from=not-a-date&to=also-bad");
        all.Runs.Count.ShouldBe(1); // both bounds ignored → unbounded
    }

    [Fact]
    public async Task Clamps_paging_to_its_bounds()
    {
        await SeedPinnedSucceededAsync(TestTenants.PrimaryId, Guid.NewGuid(), minute: 1);

        var clamped = await ListTypedAsync("?size=9999&page=0"); // size clamps to 100, page floors to 1
        clamped.Size.ShouldBe(ListRunsEndpoint.MaxPageSize);
        clamped.Page.ShouldBe(1);

        var defaulted = await ListTypedAsync("?size=notanumber&page=notanumber"); // stray → defaults
        defaulted.Size.ShouldBe(ListRunsEndpoint.DefaultPageSize);
        defaulted.Page.ShouldBe(1);
    }

    [Fact]
    public async Task An_extreme_page_returns_an_empty_list_without_overflowing()
    {
        await SeedPinnedSucceededAsync(TestTenants.PrimaryId, Guid.NewGuid(), minute: 1);

        // (page - 1) * size overflows int (→ negative Skip → 500) if computed in int; long math returns an empty page instead.
        var page = await ListTypedAsync("?page=2000000000&size=100");
        page.Runs.ShouldBeEmpty();
        page.HasMore.ShouldBeFalse();
        page.Total.ShouldBe(1);
        page.Page.ShouldBe(2000000000);
    }

    [Fact]
    public async Task Rejects_an_unknown_status_filter()
    {
        var result = await Host.Scenario(x =>
        {
            x.Get.Url("/runs?status=nope");
            x.StatusCodeShouldBe(StatusCodes.Status400BadRequest);
        });
        (await result.ReadAsJsonAsync<JsonElement>()).GetProperty("code").GetString().ShouldBe("invalid_status");
    }

    [Fact]
    public async Task Rejects_a_numeric_status_filter()
    {
        // A run-status ordinal must not select a status by accident — names only.
        await Host.Scenario(x =>
        {
            x.Get.Url("/runs?status=1");
            x.StatusCodeShouldBe(StatusCodes.Status400BadRequest);
        });
    }

    [Fact]
    public async Task Rejects_a_malformed_payload_id_filter()
    {
        var result = await Host.Scenario(x =>
        {
            x.Get.Url("/runs?payloadId=not-a-guid");
            x.StatusCodeShouldBe(StatusCodes.Status400BadRequest);
        });
        (await result.ReadAsJsonAsync<JsonElement>()).GetProperty("code").GetString().ShouldBe("invalid_payload_id");
    }

    [Fact]
    public async Task Is_tenant_scoped()
    {
        await SeedPinnedSucceededAsync(TestTenants.PrimaryId, Guid.NewGuid(), minute: 1);
        await SeedInlineFailedAsync(TestTenants.SecondaryId, minute: 1);

        var alpha = await ListAsync("");
        alpha.GetProperty("runs").GetArrayLength().ShouldBe(1);
        alpha.GetProperty("total").GetInt32().ShouldBe(1);

        var beta = await ListAsync("", apiKey: TestTenants.SecondaryKey);
        beta.GetProperty("runs").GetArrayLength().ShouldBe(1);
        beta.GetProperty("runs")[0].GetProperty("status").GetString().ShouldBe("failed"); // beta's own run, not alpha's
    }
}
