using Alba;
using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Runs;
using Crawldad.Web.Infrastructure.Browser.Fake;
using Marten;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>The SSE keepalive tail on <c>GET /runs/{id}/events</c>: while a run is quiet the endpoint emits a comment
/// frame once the idle window elapses, so an intermediary's idle timeout (Front Door / Envoy / a proxy) never drops the
/// stream mid-run (ARCHITECTURE.md §B.1). The comment carries no <c>id</c> (Last-Event-ID resume is untouched), and the
/// tail tears down cleanly when the terminal event closes it. Its own <see cref="AdvanceableClock"/> host drives the
/// idle window deterministically — the SSE tail reads the same injected clock — so there is no wall-clock wait.</summary>
public class RunEventsHeartbeatTests
{
    [Fact]
    public async Task Idle_tail_emits_a_keepalive_comment_then_closes_cleanly_on_the_terminal_event()
    {
        var clock = new AdvanceableClock(FakeClock.Fixed);
        await using var host = await DurableHost.BuildAsync(
            "crawldad_sse_heartbeat", new FakeBrowserBackend(Runner.FixturesRoot), clock: clock);
        var store = host.Services.GetRequiredService<IDocumentStore>();

        // Seed a live, non-terminal run stream: one RunStarted event, so the endpoint's existence check passes and the
        // tail keeps following (nothing terminal yet) — the quiet stretch a real run has during a long step.
        var runId = Guid.NewGuid();
        await using (var session = store.LightweightSession(TestTenants.PrimaryId))
        {
            session.Events.StartStream<Run>(runId, new RunStarted("hb.demo", "hash", clock.GetUtcNow(), [], null, null));
            await session.SaveChangesAsync();
        }

        using var client = host.GetTestServer().CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", TestTenants.Bearer(TestTenants.PrimaryKey));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await client.GetAsync(
            new Uri($"/runs/{runId}/events", UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        // Drain the backfilled RunStarted frame up to (and including) its blank terminator. Nothing else is written yet:
        // the clock is frozen at the connect instant and the RunStarted write reset the idle window.
        var backfill = new List<string>();
        string? line;
        while ((line = await reader.ReadLineAsync(cts.Token)) is { Length: > 0 })
        {
            backfill.Add(line);
        }

        backfill.ShouldContain("event: RunStarted");

        // A full idle interval passes with no real frame → the next tail poll writes a keepalive comment, and only that.
        clock.Advance(RunEventStream.HeartbeatInterval + TimeSpan.FromSeconds(1));
        (await reader.ReadLineAsync(cts.Token)).ShouldBe(": keepalive"); // a comment — no id line, so Last-Event-ID stays at the RunStarted version
        (await reader.ReadLineAsync(cts.Token)).ShouldBe("");            // the comment frame's blank terminator

        // A terminal event closes the tail: the frame is delivered and the server ends the stream — a clean teardown
        // with the heartbeat active (there is no background timer, so nothing leaks or races on close).
        await using (var session = store.LightweightSession(TestTenants.PrimaryId))
        {
            session.Events.Append(runId, new RunSucceeded(new RunStats(0, 0, 0, 0, 0), clock.GetUtcNow()));
            await session.SaveChangesAsync();
        }

        host.Services.GetRequiredService<RunEventSignals>().Notify(runId); // wake the tail now rather than at the poll backstop

        var rest = await reader.ReadToEndAsync(cts.Token); // returns at EOF — the tail returned and the response completed
        rest.ShouldContain("event: RunSucceeded");
    }
}
