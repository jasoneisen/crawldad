using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Runs;
using Crawldad.Web.Infrastructure.Storage;
using Marten;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>Observability: the <c>RunTimeline</c> projection + <c>GET /runs/{id}/timeline</c>, the SSE stream
/// (<c>GET /runs/{id}/events</c> — durable-backfill, Last-Event-ID reconnect, live tail), and <c>POST /runs/{id}/replay</c>.
/// Drives the real <c>POST /runs</c> path against the fake; SSE uses the raw TestServer client (Alba's scenario API is request/response only).</summary>
[Collection(DurableCollection.Name)]
public class RunObservabilityTests(DurableFixture fixture)
{
    // A tiny async run: navigate + bind a var, so its trace is a small, known set of step events.
    private const string _demoPayload =
        """
        { "crawldad": "1", "name": "obs.demo", "config": { "backend": "input.backend" }, "vars": {},
          "steps": [
            { "goto": { "url": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx?module=Enforcement&TabName=Enforcement" } },
            { "set": { "var": "landed", "value": "pageUrl()" } }
          ],
          "result": "{ url: landed }" }
        """;

    // Binds a known subject value into the run's data model three ways — a set, a push, and a checkpoint cursor — and
    // shapes it into the result. The PII-discipline probe: the value reaches the result body (the deletable
    // RunProgress) but must never land in an immutable trace event, which carry refs/shape/metadata only.
    private const string _piiPayload =
        """
        { "crawldad": "1", "name": "pii.trace", "config": { "backend": "input.backend" }, "vars": { "collected": [] },
          "steps": [
            { "set": { "var": "subject", "value": "input.pii" } },
            { "push": { "into": "collected", "value": "subject" } },
            { "loop": { "maxIterations": 1, "while": "false", "do": [
                { "checkpoint": { "name": "cp", "cursor": "subject", "resume": [] } }
            ] } }
          ],
          "result": "{ subject: subject, collected: collected }" }
        """;

    private static JsonObject DemoBody(bool async) => new()
    {
        ["payload"] = JsonNode.Parse(_demoPayload),
        ["inputs"] = new JsonObject { ["backend"] = new JsonObject { ["adapter"] = "fake", ["options"] = new JsonObject { ["fixture"] = "caphome-multipage" } } },
        ["async"] = async,
    };

    private static JsonObject SearchBody(string fixtureName) => new()
    {
        ["payload"] = JsonNode.Parse(File.ReadAllText(Path.Combine(Runner.FixturesRoot, "Payloads", "search-full.json"))),
        ["inputs"] = new JsonObject
        {
            ["backend"] = new JsonObject { ["adapter"] = "fake", ["options"] = new JsonObject { ["fixture"] = fixtureName } },
            ["startDate"] = "01/01/2024",
            ["endDate"] = "01/31/2024",
            ["knownUrls"] = new JsonArray(),
            ["priorCrawlComplete"] = false,
        },
        ["async"] = true,
    };

    private static async Task<Guid> StartAsyncAsync(IAlbaHost host, JsonObject body)
    {
        var accepted = await host.Scenario(x =>
        {
            x.Post.Json(body).ToUrl("/runs");
            x.StatusCodeShouldBe(202);
        });
        return (await accepted.ReadAsJsonAsync<JsonElement>()).GetProperty("runId").GetGuid();
    }

    private static async Task<Guid> RunDemoToCompletionAsync(IAlbaHost host)
    {
        var runId = await StartAsyncAsync(host, DemoBody(async: true));
        var terminal = await DurableHost.PollUntilTerminalAsync(host, runId, TimeSpan.FromSeconds(20));
        terminal.GetProperty("status").GetString().ShouldBe("succeeded");
        return runId;
    }

    // ----- RunTimeline projection + endpoint ---------------------------

    [Fact]
    public async Task Timeline_renders_the_step_list_region_extracts_and_redacted_input_keys()
    {
        var host = await fixture.EnsureAsync();
        fixture.Gate.Arm(gate: null);
        var runId = await RunDemoToCompletionAsync(host);

        var result = await host.Scenario(x =>
        {
            x.Get.Url($"/runs/{runId}/timeline");
            x.StatusCodeShouldBe(200);
        });
        var timeline = await result.ReadAsJsonAsync<JsonElement>();

        timeline.GetProperty("status").GetString().ShouldBe("succeeded");
        timeline.GetProperty("region").GetString().ShouldBe("fake"); // surfaced here, not on RunResponse
        timeline.GetProperty("scriptHash").GetString().ShouldNotBeNullOrWhiteSpace();
        timeline.GetProperty("payloadId").ValueKind.ShouldBe(JsonValueKind.Null); // an inline run
        timeline.GetProperty("durationMs").GetInt64().ShouldBeGreaterThanOrEqualTo(0);

        // The redacted input key NAMES only (never values).
        timeline.GetProperty("inputKeys").EnumerateArray().Select(k => k.GetString()).ShouldContain("backend");

        // The ordered top-level steps (goto then set), each with a duration.
        var steps = timeline.GetProperty("steps").EnumerateArray().ToList();
        steps.Select(s => s.GetProperty("kind").GetString()).ShouldBe(["goto", "set"]);
        steps.ShouldAllBe(s => s.GetProperty("durationMs").GetInt64() >= 0);

        // The extracted-value ref (key + shape, never the value).
        var extracted = timeline.GetProperty("extracted").EnumerateArray().ToList();
        extracted.ShouldContain(e => e.GetProperty("key").GetString() == "landed");
    }

    [Fact]
    public async Task Timeline_surfaces_an_explicit_screenshot_capture() // the durable saga → RunTimeline projection → endpoint, with the fake store
    {
        var host = await fixture.EnsureAsync();
        fixture.Gate.Arm(gate: null);

        var body = new JsonObject
        {
            ["payload"] = JsonNode.Parse(
                """
                { "crawldad": "1", "name": "obs.shot", "config": { "backend": "input.backend" }, "vars": {},
                  "steps": [
                    { "goto": { "url": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx?module=Enforcement&TabName=Enforcement" } },
                    { "screenshot": { "name": "after-load" } }
                  ],
                  "result": "'ok'" }
                """),
            ["inputs"] = new JsonObject { ["backend"] = new JsonObject { ["adapter"] = "fake", ["options"] = new JsonObject { ["fixture"] = "caphome-multipage" } } },
            ["async"] = true,
        };
        var runId = await StartAsyncAsync(host, body);
        var terminal = await DurableHost.PollUntilTerminalAsync(host, runId, TimeSpan.FromSeconds(20));
        terminal.GetProperty("status").GetString().ShouldBe("succeeded");

        var result = await host.Scenario(x =>
        {
            x.Get.Url($"/runs/{runId}/timeline");
            x.StatusCodeShouldBe(200);
        });
        var timeline = await result.ReadAsJsonAsync<JsonElement>();

        // The timeline surfaces the capture as an artifact (ref + label + byte size), never the image.
        var shot = timeline.GetProperty("screenshots").EnumerateArray().ToList().ShouldHaveSingleItem();
        var storedRef = shot.GetProperty("screenshotRef").GetString();
        storedRef.ShouldStartWith("screenshots/");
        shot.GetProperty("name").GetString().ShouldBe("after-load");
        shot.GetProperty("size").GetInt64().ShouldBeGreaterThan(8);

        // The bytes live only in the deletable, tenant-partitioned blob store (the same seam as screenshot-on-failure) —
        // the timeline carries only the ref + metadata, never the image.
        var store = (InMemoryScreenshotStore)host.Services.GetRequiredService<IScreenshotStore>();
        store.Blobs.Keys.ShouldContain(storedRef!);
    }

    [Fact]
    public async Task Timeline_for_an_unknown_run_is_404() =>
        await (await fixture.EnsureAsync()).Scenario(x =>
        {
            x.Get.Url($"/runs/{Guid.NewGuid()}/timeline");
            x.StatusCodeShouldBe(404);
        });

    [Fact]
    public async Task Timeline_for_a_failed_run_carries_the_failure_and_a_screenshot_ref()
    {
        var host = await fixture.EnsureAsync();
        fixture.Gate.Arm(gate: null);

        // A run that navigates then fails — the executor captures a (fake) screenshot on the failing step.
        var body = new JsonObject
        {
            ["payload"] = JsonNode.Parse(
                """
                { "crawldad": "1", "name": "obs.fail", "config": { "backend": "input.backend" }, "vars": {},
                  "steps": [
                    { "goto": { "url": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx?module=Enforcement&TabName=Enforcement" } },
                    { "fail": { "class": "terminal", "code": "obs_boom", "message": "stop" } }
                  ],
                  "result": "'x'" }
                """),
            ["inputs"] = new JsonObject { ["backend"] = new JsonObject { ["adapter"] = "fake", ["options"] = new JsonObject { ["fixture"] = "caphome-multipage" } } },
            ["async"] = true,
        };
        var runId = await StartAsyncAsync(host, body);
        (await DurableHost.PollUntilTerminalAsync(host, runId, TimeSpan.FromSeconds(20))).GetProperty("status").GetString().ShouldBe("failed");

        var result = await host.Scenario(x =>
        {
            x.Get.Url($"/runs/{runId}/timeline");
            x.StatusCodeShouldBe(200);
        });
        var failure = (await result.ReadAsJsonAsync<JsonElement>()).GetProperty("failure");
        failure.GetProperty("code").GetString().ShouldBe("obs_boom");
        failure.GetProperty("screenshotRef").GetString().ShouldStartWith("screenshots/");
    }

    // ----- metadata-only trace discipline (the PII re-assertion) ----------

    [Fact]
    public async Task The_trace_stream_holds_no_raw_extracted_or_input_value_only_metadata_refs()
    {
        // A distinctive, NON-credential subject value: the scrubber never touches it (no apiKey=/token= param, not a
        // registered run secret), so its ABSENCE from the trace proves the metadata-only discipline itself — not an
        // after-the-fact redaction. It stands in for the bulk PII a scraped record carries.
        const string Pii = "PII_SUBJECT_Jane_Q_Doe_dob_1970-01-01_ref_ABCDEF";
        var host = await fixture.EnsureAsync();
        fixture.Gate.Arm(gate: null);

        var body = new JsonObject
        {
            ["payload"] = JsonNode.Parse(_piiPayload),
            ["inputs"] = new JsonObject
            {
                ["backend"] = new JsonObject { ["adapter"] = "fake", ["options"] = new JsonObject { ["fixture"] = "caphome-multipage" } },
                ["pii"] = Pii,
            },
            ["async"] = true,
        };
        var runId = await StartAsyncAsync(host, body);
        var terminal = await DurableHost.PollUntilTerminalAsync(host, runId, TimeSpan.FromSeconds(20));

        // The value really was bound into the data model and shaped into the result body (RunProgress, the deletable
        // store) — so the sweep below is not vacuous: something with this value exists, just never in the trace.
        terminal.GetProperty("status").GetString().ShouldBe("succeeded");
        terminal.GetProperty("result").GetProperty("subject").GetString().ShouldBe(Pii);

        // (a) Every event in the immutable trace: NONE carries the raw value. set/push emit Extracted shape refs only,
        // the checkpoint marker is metadata-only (name + sequence), and no terminal event carries the result body.
        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(TestTenants.PrimaryId);
        var events = await session.Events.FetchStreamAsync(runId);
        string.Join('\n', events.Select(e => JsonSerializer.Serialize(e.Data, e.Data.GetType()))).ShouldNotContain(Pii);

        // …and the metadata-only markers are genuinely present, so the clean sweep is because they carry refs, not values.
        var types = events.Select(e => e.EventType).ToList();
        types.ShouldContain(typeof(Extracted));
        types.ShouldContain(typeof(RunCheckpointReached));

        // (b) The SSE frames render from that already-metadata-only stream — no value reaches a live tail either.
        var frames = await SseReader.ReadToCloseAsync(host, runId, lastEventId: null, TimeSpan.FromSeconds(20));
        string.Join('\n', frames.Select(f => f.Data)).ShouldNotContain(Pii);

        // (c) The RunTimeline projection carries the extracted KEY + shape ref, never the value.
        var timelineResult = await host.Scenario(x =>
        {
            x.Get.Url($"/runs/{runId}/timeline");
            x.StatusCodeShouldBe(200);
        });
        var timeline = await timelineResult.ReadAsJsonAsync<JsonElement>();
        timeline.GetRawText().ShouldNotContain(Pii);
        timeline.GetProperty("extracted").EnumerateArray().Select(e => e.GetProperty("key").GetString()).ShouldContain("subject");
    }

    // ----- SSE: backfill + reconnect + live tail -----------------------

    [Fact]
    public async Task Events_for_an_unknown_run_is_404()
    {
        using var client = (await fixture.EnsureAsync()).GetTestServer().CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", TestTenants.Bearer(TestTenants.PrimaryKey));
        using var response = await client.GetAsync(new Uri($"/runs/{Guid.NewGuid()}/events", UriKind.Relative));
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Events_backfills_the_whole_terminal_stream_then_closes()
    {
        var host = await fixture.EnsureAsync();
        fixture.Gate.Arm(gate: null);
        var runId = await RunDemoToCompletionAsync(host);

        var frames = await SseReader.ReadToCloseAsync(host, runId, lastEventId: null, TimeSpan.FromSeconds(20));

        frames[0].Event.ShouldBe("RunStarted");
        frames.Select(f => f.Event).ShouldContain("StepStarted");
        frames.Select(f => f.Event).ShouldContain("Navigated");
        frames[^1].Event.ShouldBe("RunSucceeded"); // the terminal frame closes the stream
        frames.Select(f => f.Id).ShouldBe(frames.Select(f => f.Id).Order().ToList()); // strictly ordered by stream version
    }

    [Fact]
    public async Task Events_reconnect_with_last_event_id_continues_exactly_without_loss_or_duplication()
    {
        var host = await fixture.EnsureAsync();
        fixture.Gate.Arm(gate: null);
        var runId = await RunDemoToCompletionAsync(host);

        // Consume the whole stream once, then reconnect from a mid-point sequence — the durable backfill is the reconnect.
        var all = await SseReader.ReadToCloseAsync(host, runId, lastEventId: null, TimeSpan.FromSeconds(20));
        var midpoint = all[all.Count / 2].Id;

        var tail = await SseReader.ReadToCloseAsync(host, runId, lastEventId: midpoint, TimeSpan.FromSeconds(20));

        tail[0].Id.ShouldBe(midpoint + 1);                         // continues at exactly the next frame — no gap
        tail.ShouldAllBe(f => f.Id > midpoint);                     // nothing already-seen is re-sent — no duplication
        tail.Select(f => f.Id).ShouldBe(all.Where(f => f.Id > midpoint).Select(f => f.Id).ToList()); // exact continuation
    }

    [Fact]
    public async Task Events_connected_mid_run_tails_live_frames_through_the_terminal()
    {
        var host = await fixture.EnsureAsync();
        var gate = new RunGate("pg=2");
        fixture.Gate.Arm(gate);
        var runId = await StartAsyncAsync(host, SearchBody("caphome-resume"));
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(20)); // blocked mid-crawl, backfillable events already persisted

        // Connect while the run is blocked; read in the background (poll backstop keeps the tail alive), release, and the
        // stream delivers the remaining frames through the terminal event and closes.
        var reading = SseReader.ReadToCloseAsync(host, runId, lastEventId: null, TimeSpan.FromSeconds(30));
        await Task.Delay(250); // let the tail loop hit its poll backstop at least once while no new events arrive
        gate.Release();

        var frames = await reading;
        frames[0].Event.ShouldBe("RunStarted");
        frames[^1].Event.ShouldBe("RunSucceeded"); // the live tail followed to the terminal frame
    }

    [Fact]
    public async Task Events_stops_tailing_cleanly_when_the_client_disconnects_mid_run()
    {
        var host = await fixture.EnsureAsync();
        var gate = new RunGate("pg=2");
        fixture.Gate.Arm(gate);
        var runId = await StartAsyncAsync(host, SearchBody("caphome-resume"));
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(20));

        // Open the stream and drain frames continuously, then abort the request while the run is still gated — the server
        // observes RequestAborted and stops tailing cleanly.
        using var client = host.GetTestServer().CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", TestTenants.Bearer(TestTenants.PrimaryKey));
        using var cts = new CancellationTokenSource();
        var draining = Task.Run(async () =>
        {
            using var response = await client.GetAsync(new Uri($"/runs/{runId}/events", UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            var buffer = new byte[512];
            while (await stream.ReadAsync(buffer, cts.Token) >= 0)
            {
                // drain the backfilled frames, then park awaiting more (none arrive — the run is gated)
            }
        });
        await Task.Delay(400);   // drain the backfill and let the server park in its poll wait
        await cts.CancelAsync(); // client disconnects mid-tail
        await Should.ThrowAsync<OperationCanceledException>(async () => await draining);

        // The run is unaffected by the disconnect: release and it still completes.
        gate.Release();
        (await DurableHost.PollUntilTerminalAsync(host, runId, TimeSpan.FromSeconds(30))).GetProperty("status").GetString().ShouldBe("succeeded");
    }

    // ----- replay ------------------------------------------------------

    [Fact]
    public async Task Replay_re_executes_a_pinned_runs_revision_with_resupplied_inputs()
    {
        var host = await fixture.EnsureAsync();
        fixture.Gate.Arm(gate: null);

        // Draft a managed payload, run it pinned, then replay that run — the replay pins the SAME revision.
        var draft = await host.Scenario(x =>
        {
            x.Post.Json(new JsonObject { ["payload"] = JsonNode.Parse(_demoPayload) }).ToUrl("/payloads");
            x.StatusCodeShouldBe(200);
        });
        var payloadId = (await draft.ReadAsJsonAsync<JsonElement>()).GetProperty("payloadId").GetGuid();

        var inputs = new JsonObject { ["backend"] = new JsonObject { ["adapter"] = "fake", ["options"] = new JsonObject { ["fixture"] = "caphome-multipage" } } };
        var pinnedRun = await host.Scenario(x =>
        {
            x.Post.Json(new JsonObject { ["payloadId"] = payloadId, ["inputs"] = inputs.DeepClone() }).ToUrl("/runs");
            x.StatusCodeShouldBe(200);
        });
        var pinnedRunId = (await pinnedRun.ReadAsJsonAsync<JsonElement>()).GetProperty("runId").GetGuid();

        // Replay with resupplied inputs — a fresh run, same shape as POST /runs.
        var replay = await host.Scenario(x =>
        {
            x.Post.Json(new JsonObject { ["inputs"] = inputs.DeepClone() }).ToUrl($"/runs/{pinnedRunId}/replay");
            x.StatusCodeShouldBe(200);
        });
        var replayRoot = await replay.ReadAsJsonAsync<JsonElement>();
        replayRoot.GetProperty("status").GetString().ShouldBe("succeeded");
        replayRoot.GetProperty("runId").GetGuid().ShouldNotBe(pinnedRunId); // a NEW run

        // The replay pinned the original run's revision (drift-comparable).
        var drift = await host.Scenario(x =>
        {
            x.Get.Url($"/runs/{replayRoot.GetProperty("runId").GetGuid()}/drift");
            x.StatusCodeShouldBe(200);
        });
        (await drift.ReadAsJsonAsync<JsonElement>()).GetProperty("pinnedRevision").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Replay_async_returns_202_and_completes_in_the_background()
    {
        var host = await fixture.EnsureAsync();
        fixture.Gate.Arm(gate: null);

        var draft = await host.Scenario(x =>
        {
            x.Post.Json(new JsonObject { ["payload"] = JsonNode.Parse(_demoPayload) }).ToUrl("/payloads");
            x.StatusCodeShouldBe(200);
        });
        var payloadId = (await draft.ReadAsJsonAsync<JsonElement>()).GetProperty("payloadId").GetGuid();
        var inputs = new JsonObject { ["backend"] = new JsonObject { ["adapter"] = "fake", ["options"] = new JsonObject { ["fixture"] = "caphome-multipage" } } };
        var pinnedRunId = await StartAsyncAsync(host, new JsonObject { ["payloadId"] = payloadId, ["inputs"] = inputs.DeepClone(), ["async"] = true });
        await DurableHost.PollUntilTerminalAsync(host, pinnedRunId, TimeSpan.FromSeconds(20));

        var accepted = await host.Scenario(x =>
        {
            x.Post.Json(new JsonObject { ["inputs"] = inputs.DeepClone(), ["async"] = true }).ToUrl($"/runs/{pinnedRunId}/replay");
            x.StatusCodeShouldBe(202);
        });
        var replayRunId = (await accepted.ReadAsJsonAsync<JsonElement>()).GetProperty("runId").GetGuid();
        (await DurableHost.PollUntilTerminalAsync(host, replayRunId, TimeSpan.FromSeconds(20))).GetProperty("status").GetString().ShouldBe("succeeded");
    }

    [Fact]
    public async Task Replay_of_an_inline_run_is_rejected_as_not_replayable()
    {
        var host = await fixture.EnsureAsync();
        fixture.Gate.Arm(gate: null);

        // An inline run's script was never stored as a managed revision — it cannot be replayed.
        var inlineRun = await host.Scenario(x =>
        {
            x.Post.Json(DemoBody(async: false)).ToUrl("/runs");
            x.StatusCodeShouldBe(200);
        });
        var inlineRunId = (await inlineRun.ReadAsJsonAsync<JsonElement>()).GetProperty("runId").GetGuid();

        var rejected = await host.Scenario(x =>
        {
            x.Post.Json(new JsonObject { ["inputs"] = new JsonObject() }).ToUrl($"/runs/{inlineRunId}/replay");
            x.StatusCodeShouldBe(400);
        });
        (await rejected.ReadAsJsonAsync<JsonElement>()).GetProperty("code").GetString().ShouldBe("inline_not_replayable");
    }

    [Fact]
    public async Task Replay_of_an_unknown_run_is_404() =>
        await (await fixture.EnsureAsync()).Scenario(x =>
        {
            x.Post.Json(new JsonObject { ["inputs"] = new JsonObject() }).ToUrl($"/runs/{Guid.NewGuid()}/replay");
            x.StatusCodeShouldBe(404);
        });
}
