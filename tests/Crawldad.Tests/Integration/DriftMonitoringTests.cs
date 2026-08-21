using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Drift;
using Crawldad.Web.Features.Runs;
using Crawldad.Web.Infrastructure.Browser.Fake;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>One shared background-executor host for the drift-monitoring tests, built once (lazily) — like the durable
/// gates' fixture — so schema migration happens once and the tests don't contend. Each test isolates itself by a freshly
/// drafted payload id, so seeded/observed timelines never cross between tests on the shared host.</summary>
public sealed class DriftFixture : IAsyncLifetime
{
    private IAlbaHost? _host;

    public Task InitializeAsync() => Task.CompletedTask; // built lazily on first use

    internal async Task<IAlbaHost> EnsureAsync() =>
        _host ??= await DurableHost.BuildAsync("crawldad_drift", new FakeBrowserBackend(Runner.FixturesRoot));

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class DriftCollection : ICollectionFixture<DriftFixture>
{
    public const string Name = "drift-monitoring";
}

/// <summary>Per-tenant drift monitoring (issue #47): <c>GET /payloads/{id}/drift-status</c> reports a payload canary's
/// baseline/delta selector drift, computed on read from the runs' <c>RunTimeline</c> observations. A real durable run
/// proves the <c>SelectorMiss</c> → timeline → endpoint chain; seeded timelines drive the state machine deterministically.
/// Every read is tenant-scoped — a foreign payload is a 404 with no existence oracle.</summary>
[Collection(DriftCollection.Name)]
public class DriftMonitoringTests(DriftFixture fixture)
{
    private static readonly DateTimeOffset _t0 = new(2026, 8, 12, 9, 0, 0, TimeSpan.Zero);

    // The Search-form button id: present on the caphome-* pages (matches, never a miss), absent on a capdetail record
    // page — so the SAME pinned payload extracting it misses only when the canary runs against the record fixture.
    private const string _searchButton = "#ctl00_PlaceHolderMain_btnNewSearch";

    // A pinned-canary payload: navigate, then read the Search button's text. A soft miss (absent id) still succeeds.
    private const string _canaryPayload =
        """
        { "crawldad": "1", "name": "drift.canary", "config": { "backend": "input.backend" }, "vars": {},
          "steps": [
            { "goto": { "url": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx?module=Enforcement&TabName=Enforcement" } },
            { "set": { "var": "label", "value": "text('#ctl00_PlaceHolderMain_btnNewSearch')" } }
          ],
          "result": "{ label: label }" }
        """;

    // ----- the real chain: SelectorMiss → RunTimeline → the drift endpoint -----

    [Fact]
    public async Task A_durable_run_folds_missed_selectors_into_the_timeline_and_the_drift_endpoint_reads_them()
    {
        var host = await fixture.EnsureAsync();
        var payloadId = await DraftAsync(host, _canaryPayload);

        // Run the pinned canary against the record fixture, where the Search button is absent → a soft selector miss.
        var runId = await RunPinnedAsync(host, payloadId, fixtureName: "capdetail-violations");
        (await DurableHost.PollUntilTerminalAsync(host, runId, DurableHost.PollTimeout)).GetProperty("status").GetString().ShouldBe("succeeded");

        // The miss folded into the run's timeline (the fold this issue adds; previously visible only via stats/events).
        var timeline = await GetJsonAsync(host, $"/runs/{runId}/timeline");
        timeline.GetProperty("missedSelectors").EnumerateArray().Select(s => s.GetString()).ShouldContain(_searchButton);

        // One healthy observation is not yet a baseline to judge against — the endpoint reports warming-up, not drift.
        var drift = await GetJsonAsync(host, $"/payloads/{payloadId}/drift-status");
        drift.GetProperty("state").GetString().ShouldBe("warmingUp");
        drift.GetProperty("drifted").GetBoolean().ShouldBeFalse();
        drift.GetProperty("payloadId").GetGuid().ShouldBe(payloadId);
        drift.GetProperty("observedRuns").GetInt32().ShouldBe(1);
    }

    // ----- the state machine, over seeded observations -----

    [Fact]
    public async Task An_existing_payload_with_no_runs_is_no_data()
    {
        var host = await fixture.EnsureAsync();
        var payloadId = await DraftAsync(host, _canaryPayload);

        var drift = await GetJsonAsync(host, $"/payloads/{payloadId}/drift-status");
        drift.GetProperty("state").GetString().ShouldBe("noData");
        drift.GetProperty("observedRuns").GetInt32().ShouldBe(0);
        drift.GetProperty("baselineRuns").GetInt32().ShouldBe(DriftAnalysis.DefaultBaselineRuns);
        drift.TryGetProperty("evidence", out _).ShouldBeFalse(); // omitted when null
    }

    [Fact]
    public async Task A_newly_missing_selector_drifts_with_evidence_and_the_threshold_tolerates_it()
    {
        var host = await fixture.EnsureAsync();
        var payloadId = await DraftAsync(host, _canaryPayload);

        // Three healthy baseline observations (no misses), then a later run newly missing "#title" — with capture +
        // screenshot evidence, and a run that FAILED (a required miss can bring the canary down yet still record the miss).
        await SeedAsync(host, TestTenants.PrimaryId,
            Timeline(payloadId, RunStatus.Succeeded, _t0),
            Timeline(payloadId, RunStatus.Succeeded, _t0.AddMinutes(1)),
            Timeline(payloadId, RunStatus.Succeeded, _t0.AddMinutes(2)),
            Timeline(payloadId, RunStatus.Failed, _t0.AddMinutes(9), missed: ["#title"],
                captures: ["captures/page.html"], screenshots: ["screenshots/x.png"], failureScreenshotRef: "screenshots/boom.png"));

        var drift = await GetJsonAsync(host, $"/payloads/{payloadId}/drift-status");
        drift.GetProperty("state").GetString().ShouldBe("drifted");
        drift.GetProperty("drifted").GetBoolean().ShouldBeTrue();
        drift.GetProperty("driftedSelectorCount").GetInt32().ShouldBe(1);
        drift.GetProperty("observedRuns").GetInt32().ShouldBe(4);
        drift.GetProperty("pinnedRevision").GetInt32().ShouldBe(1);

        var selector = drift.GetProperty("selectors").EnumerateArray().ShouldHaveSingleItem();
        selector.GetProperty("selector").GetString().ShouldBe("#title");
        selector.GetProperty("drifted").GetBoolean().ShouldBeTrue();
        selector.GetProperty("baselineFloor").GetBoolean().ShouldBeFalse();

        var evidence = drift.GetProperty("evidence");
        evidence.GetProperty("status").GetString().ShouldBe("failed");
        evidence.GetProperty("failureScreenshotRef").GetString().ShouldBe("screenshots/boom.png");
        evidence.GetProperty("captureRefs").EnumerateArray().Select(r => r.GetString()).ShouldBe(["captures/page.html"]);
        evidence.GetProperty("screenshotRefs").EnumerateArray().Select(r => r.GetString()).ShouldBe(["screenshots/x.png"]);

        // A per-payload threshold of 1 tolerates the single new miss — the metric is still reported, but no alarm.
        var tolerated = await GetJsonAsync(host, $"/payloads/{payloadId}/drift-status?threshold=1");
        tolerated.GetProperty("state").GetString().ShouldBe("steady");
        tolerated.GetProperty("drifted").GetBoolean().ShouldBeFalse();
        tolerated.GetProperty("threshold").GetInt32().ShouldBe(1);
        tolerated.GetProperty("driftedSelectorCount").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task A_selector_missing_since_the_baseline_is_reported_as_floor_not_drift()
    {
        var host = await fixture.EnsureAsync();
        var payloadId = await DraftAsync(host, _canaryPayload);

        // "#fallback" missed since the baseline (a legit multi-selector fallback floor) and still misses in a later,
        // post-baseline run → steady, and the selector is reported as floor, not drift.
        await SeedAsync(host, TestTenants.PrimaryId,
            Timeline(payloadId, RunStatus.Succeeded, _t0, missed: ["#fallback"]),
            Timeline(payloadId, RunStatus.Succeeded, _t0.AddMinutes(1), missed: ["#fallback"]),
            Timeline(payloadId, RunStatus.Succeeded, _t0.AddMinutes(2), missed: ["#fallback"]),
            Timeline(payloadId, RunStatus.Succeeded, _t0.AddMinutes(9), missed: ["#fallback"]));

        var drift = await GetJsonAsync(host, $"/payloads/{payloadId}/drift-status");
        drift.GetProperty("state").GetString().ShouldBe("steady");
        drift.GetProperty("driftedSelectorCount").GetInt32().ShouldBe(0);
        var selector = drift.GetProperty("selectors").EnumerateArray().ShouldHaveSingleItem();
        selector.GetProperty("baselineFloor").GetBoolean().ShouldBeTrue();
        selector.GetProperty("drifted").GetBoolean().ShouldBeFalse();
    }

    // ----- multi-revision histories: the baseline rescopes per PayloadRevision (issue #89) -----

    [Fact]
    public async Task A_new_payload_revision_rescopes_the_baseline_and_clears_false_positive_drift()
    {
        var host = await fixture.EnsureAsync();
        var payloadId = await DraftAsync(host, _canaryPayload);

        // Revision 1 ran to a clean, established baseline. Revision 2 is a payload edit that added "#newField" — a
        // selector with a legitimate steady-state miss (a coalesce fallback). Under the old revision-blind query the
        // revision-1 floor (which never saw "#newField") froze, so the new selector read as PERMANENT false-positive
        // drift. With per-revision scoping, the single revision-2 run so far only re-warms the baseline: nothing alarms.
        await SeedAsync(host, TestTenants.PrimaryId,
            Timeline(payloadId, RunStatus.Succeeded, _t0, revision: 1),
            Timeline(payloadId, RunStatus.Succeeded, _t0.AddMinutes(1), revision: 1),
            Timeline(payloadId, RunStatus.Succeeded, _t0.AddMinutes(2), revision: 1),
            Timeline(payloadId, RunStatus.Succeeded, _t0.AddMinutes(3), revision: 1),
            Timeline(payloadId, RunStatus.Succeeded, _t0.AddMinutes(10), revision: 2, missed: ["#newField"]));

        var warming = await GetJsonAsync(host, $"/payloads/{payloadId}/drift-status");
        warming.GetProperty("state").GetString().ShouldBe("warmingUp"); // NOT drifted — the revision change re-warms
        warming.GetProperty("drifted").GetBoolean().ShouldBeFalse();
        warming.GetProperty("pinnedRevision").GetInt32().ShouldBe(2);    // the canary's current revision
        warming.GetProperty("observedRuns").GetInt32().ShouldBe(1);      // rescoped to revision 2, not the 5 runs total

        // Revision 2 accumulates its own baseline window (its three earliest healthy runs, all missing "#newField" — its
        // legitimate floor), then a later revision-2 run still missing only "#newField" → steady, floor, never drift.
        await SeedAsync(host, TestTenants.PrimaryId,
            Timeline(payloadId, RunStatus.Succeeded, _t0.AddMinutes(11), revision: 2, missed: ["#newField"]),
            Timeline(payloadId, RunStatus.Succeeded, _t0.AddMinutes(12), revision: 2, missed: ["#newField"]),
            Timeline(payloadId, RunStatus.Succeeded, _t0.AddMinutes(20), revision: 2, missed: ["#newField"]));

        var steady = await GetJsonAsync(host, $"/payloads/{payloadId}/drift-status");
        steady.GetProperty("state").GetString().ShouldBe("steady");
        steady.GetProperty("drifted").GetBoolean().ShouldBeFalse();
        steady.GetProperty("driftedSelectorCount").GetInt32().ShouldBe(0);
        steady.GetProperty("observedRuns").GetInt32().ShouldBe(4);       // the four revision-2 runs, not the eight total
        var floor = steady.GetProperty("selectors").EnumerateArray().ShouldHaveSingleItem();
        floor.GetProperty("selector").GetString().ShouldBe("#newField");
        floor.GetProperty("baselineFloor").GetBoolean().ShouldBeTrue();
        floor.GetProperty("drifted").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task A_prior_revision_floor_neither_leaks_into_nor_masks_drift_in_the_new_revision()
    {
        var host = await fixture.EnsureAsync();
        var payloadId = await DraftAsync(host, _canaryPayload);

        // Revision 1's earliest runs missed "#shared" (its floor). Revision 2's own baseline matched "#shared" cleanly,
        // then a later revision-2 run newly misses it — a real drift. A revision-blind baseline would take revision 1's
        // earliest runs as the floor (they miss "#shared") and MASK the drift as steady; per-revision scoping builds the
        // floor from revision 2's clean baseline, so the newly-missing selector is correctly reported as drift.
        await SeedAsync(host, TestTenants.PrimaryId,
            Timeline(payloadId, RunStatus.Succeeded, _t0, revision: 1, missed: ["#shared"]),
            Timeline(payloadId, RunStatus.Succeeded, _t0.AddMinutes(1), revision: 1, missed: ["#shared"]),
            Timeline(payloadId, RunStatus.Succeeded, _t0.AddMinutes(2), revision: 1, missed: ["#shared"]),
            Timeline(payloadId, RunStatus.Succeeded, _t0.AddMinutes(10), revision: 2),
            Timeline(payloadId, RunStatus.Succeeded, _t0.AddMinutes(11), revision: 2),
            Timeline(payloadId, RunStatus.Succeeded, _t0.AddMinutes(12), revision: 2),
            Timeline(payloadId, RunStatus.Succeeded, _t0.AddMinutes(20), revision: 2, missed: ["#shared"]));

        var drift = await GetJsonAsync(host, $"/payloads/{payloadId}/drift-status");
        drift.GetProperty("state").GetString().ShouldBe("drifted");
        drift.GetProperty("drifted").GetBoolean().ShouldBeTrue();
        drift.GetProperty("pinnedRevision").GetInt32().ShouldBe(2);
        drift.GetProperty("driftedSelectorCount").GetInt32().ShouldBe(1);
        var selector = drift.GetProperty("selectors").EnumerateArray().ShouldHaveSingleItem();
        selector.GetProperty("selector").GetString().ShouldBe("#shared");
        selector.GetProperty("drifted").GetBoolean().ShouldBeTrue();
        selector.GetProperty("baselineFloor").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task A_rollback_to_an_already_baselined_revision_resumes_immediately_without_re_warming()
    {
        var host = await fixture.EnsureAsync();
        var payloadId = await DraftAsync(host, _canaryPayload);

        // Revision 1 was fully baselined earlier (its three earliest healthy runs are its floor). The canary then moved
        // to revision 2, and is now rolled back / re-pinned to revision 1 (a POST /runs { revision:1 } run is the latest
        // observation). Because revision 1 already carries its baseline in history, the assessment resumes against that
        // established floor at once — steady, NOT warmingUp. The "revision change → warmingUp" reset only holds for a
        // revision the canary has not yet run its baseline window of healthy times, not a rollback to a baselined one.
        await SeedAsync(host, TestTenants.PrimaryId,
            Timeline(payloadId, RunStatus.Succeeded, _t0, revision: 1),
            Timeline(payloadId, RunStatus.Succeeded, _t0.AddMinutes(1), revision: 1),
            Timeline(payloadId, RunStatus.Succeeded, _t0.AddMinutes(2), revision: 1),
            Timeline(payloadId, RunStatus.Succeeded, _t0.AddMinutes(10), revision: 2),
            Timeline(payloadId, RunStatus.Succeeded, _t0.AddMinutes(11), revision: 2),
            Timeline(payloadId, RunStatus.Succeeded, _t0.AddMinutes(12), revision: 2),
            Timeline(payloadId, RunStatus.Succeeded, _t0.AddMinutes(30), revision: 1)); // the rollback run — latest overall

        var drift = await GetJsonAsync(host, $"/payloads/{payloadId}/drift-status");
        drift.GetProperty("state").GetString().ShouldBe("steady");  // resumes revision 1's floor at once, never re-warms
        drift.GetProperty("pinnedRevision").GetInt32().ShouldBe(1);
        drift.GetProperty("observedRuns").GetInt32().ShouldBe(4);   // revision 1's four completed runs
    }

    [Fact]
    public async Task An_interleaved_head_run_transiently_masks_pinned_revision_drift_then_self_corrects()
    {
        var host = await fixture.EnsureAsync();
        var payloadId = await DraftAsync(host, _canaryPayload);

        // The canary pins revision 1: a clean baseline, and a latest run that newly misses "#realDrift" → drifted.
        await SeedAsync(host, TestTenants.PrimaryId,
            Timeline(payloadId, RunStatus.Succeeded, _t0, revision: 1),
            Timeline(payloadId, RunStatus.Succeeded, _t0.AddMinutes(1), revision: 1),
            Timeline(payloadId, RunStatus.Succeeded, _t0.AddMinutes(2), revision: 1),
            Timeline(payloadId, RunStatus.Succeeded, _t0.AddMinutes(5), revision: 1, missed: ["#realDrift"]));

        var drifted = await GetJsonAsync(host, $"/payloads/{payloadId}/drift-status");
        drifted.GetProperty("state").GetString().ShouldBe("drifted");
        drifted.GetProperty("pinnedRevision").GetInt32().ShouldBe(1);

        // An ad-hoc run at head (revision 2) lands as the newest observation. The current revision follows the latest
        // completed run, so this one poll now assesses revision 2 (warming up) and transiently MASKS the revision-1
        // drift — the accepted, self-correcting one-cycle flap of pinning to the latest observation (issue #89 review).
        await SeedAsync(host, TestTenants.PrimaryId,
            Timeline(payloadId, RunStatus.Succeeded, _t0.AddMinutes(6), revision: 2));

        var masked = await GetJsonAsync(host, $"/payloads/{payloadId}/drift-status");
        masked.GetProperty("state").GetString().ShouldBe("warmingUp");
        masked.GetProperty("drifted").GetBoolean().ShouldBeFalse();
        masked.GetProperty("pinnedRevision").GetInt32().ShouldBe(2);

        // The canary's next scheduled run at its pinned revision 1 (still drifting) self-corrects the signal next poll.
        await SeedAsync(host, TestTenants.PrimaryId,
            Timeline(payloadId, RunStatus.Succeeded, _t0.AddMinutes(7), revision: 1, missed: ["#realDrift"]));

        var recovered = await GetJsonAsync(host, $"/payloads/{payloadId}/drift-status");
        recovered.GetProperty("state").GetString().ShouldBe("drifted");
        recovered.GetProperty("pinnedRevision").GetInt32().ShouldBe(1);
        recovered.GetProperty("selectors").EnumerateArray().ShouldHaveSingleItem()
            .GetProperty("selector").GetString().ShouldBe("#realDrift");
    }

    // ----- tenant scoping + not-found -----

    [Fact]
    public async Task Drift_status_for_an_unknown_payload_is_404()
    {
        var host = await fixture.EnsureAsync();
        await host.Scenario(x =>
        {
            x.Get.Url($"/payloads/{Guid.NewGuid()}/drift-status");
            x.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task Drift_status_is_tenant_scoped()
    {
        var host = await fixture.EnsureAsync();
        var payloadId = await DraftAsync(host, _canaryPayload); // drafted + observed under the primary tenant
        await SeedAsync(host, TestTenants.PrimaryId, Timeline(payloadId, RunStatus.Succeeded, _t0, missed: ["#title"]));

        // The secondary tenant cannot see the primary's payload at all — a 404, not a filtered-empty drift report.
        await host.Scenario(x =>
        {
            x.Get.Url($"/payloads/{payloadId}/drift-status");
            x.WithRequestHeader("Authorization", TestTenants.Bearer(TestTenants.SecondaryKey));
            x.StatusCodeShouldBe(404);
        });
    }

    private static RunTimeline Timeline(
        Guid payloadId,
        RunStatus status,
        DateTimeOffset startedAt,
        int revision = 1,
        string[]? missed = null,
        string[]? captures = null,
        string[]? screenshots = null,
        string? failureScreenshotRef = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            PayloadId = payloadId,
            PayloadName = "drift.canary",
            ScriptHash = "hash",
            PayloadRevision = revision,
            Status = status,
            StartedAt = startedAt,
            FinishedAt = startedAt.AddSeconds(1),
            MissedSelectors = missed ?? [],
            Captures = [.. (captures ?? []).Select(reference => new RunTimelineCapture(reference, 1, "sha"))],
            Screenshots = [.. (screenshots ?? []).Select(reference => new RunTimelineScreenshot(reference, null, 1))],
            Failure = failureScreenshotRef is null ? null : new RunTimelineFailure("selector_miss", "gone", new RunStepRef(0, "set"), failureScreenshotRef, null),
        };

    private static async Task SeedAsync(IAlbaHost host, string tenantId, params RunTimeline[] timelines)
    {
        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenantId);
        session.Store(timelines);
        await session.SaveChangesAsync();
    }

    private static async Task<Guid> DraftAsync(IAlbaHost host, string payload)
    {
        var draft = await host.Scenario(x =>
        {
            x.Post.Json(new JsonObject { ["payload"] = JsonNode.Parse(payload) }).ToUrl("/payloads");
            x.StatusCodeShouldBe(200);
        });
        return (await draft.ReadAsJsonAsync<JsonElement>()).GetProperty("payloadId").GetGuid();
    }

    private static async Task<Guid> RunPinnedAsync(IAlbaHost host, Guid payloadId, string fixtureName)
    {
        var body = new JsonObject
        {
            ["payloadId"] = payloadId,
            ["inputs"] = new JsonObject { ["backend"] = new JsonObject { ["adapter"] = "fake", ["options"] = new JsonObject { ["fixture"] = fixtureName } } },
            ["async"] = true,
        };
        var accepted = await host.Scenario(x =>
        {
            x.Post.Json(body).ToUrl("/runs");
            x.StatusCodeShouldBe(202);
        });
        return (await accepted.ReadAsJsonAsync<JsonElement>()).GetProperty("runId").GetGuid();
    }

    private static async Task<JsonElement> GetJsonAsync(IAlbaHost host, string url)
    {
        var result = await host.Scenario(x =>
        {
            x.Get.Url(url);
            x.StatusCodeShouldBe(200);
        });
        return (await result.ReadAsJsonAsync<JsonElement>()).Clone();
    }
}
