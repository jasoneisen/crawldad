using System.Text.Json;
using Alba;
using Crawldad.Api.Infrastructure.Browser.Fake;
using Crawldad.Client;
using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;
using Microsoft.AspNetCore.TestHost;

namespace Crawldad.Tests.Client;

/// <summary>A dedicated real API host (its own Marten schema, the fake backend) for the client integration suite, plus a
/// <see cref="CrawldadClient"/> built over the in-process TestServer — so create → poll → timeline and the SSE stream are
/// exercised end to end through the SDK against the actual endpoints, not a stub.</summary>
public sealed class ClientIntegrationFixture : IAsyncLifetime
{
    public IAlbaHost Host { get; private set; } = null!;
    public CrawldadClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Host = await DurableHost.BuildAsync("crawldad_client_it", new FakeBrowserBackend(Runner.FixturesRoot));
        var http = Host.GetTestServer().CreateClient();
        Client = new CrawldadClient(http, new CrawldadClientOptions { BaseUrl = http.BaseAddress, ApiKey = TestTenants.PrimaryKey });
    }

    public Task DisposeAsync() => Host.DisposeAsync().AsTask();
}

/// <summary>The client integration collection — one shared real host.</summary>
[CollectionDefinition(Name)]
public sealed class ClientIntegrationCollection : ICollectionFixture<ClientIntegrationFixture>
{
    public const string Name = "client-integration";
}

/// <summary>End-to-end tests driving the real API host in-process through <see cref="CrawldadClient"/>.</summary>
[Collection(ClientIntegrationCollection.Name)]
public class ClientIntegrationTests(ClientIntegrationFixture fixture)
{
    private CrawldadClient Client => fixture.Client;

    private const string _demoPayload =
        """
        { "crawldad": "1", "name": "obs.demo", "config": { "backend": "input.backend" }, "vars": {},
          "steps": [
            { "goto": { "url": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx?module=Enforcement&TabName=Enforcement" } },
            { "set": { "var": "landed", "value": "pageUrl()" } }
          ],
          "result": "{ url: landed }" }
        """;

    private static JsonElement JsonElementOf(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonElement DemoInputs() =>
        JsonElementOf("""{ "backend": { "adapter": "fake", "options": { "fixture": "caphome-multipage" } } }""");

    private async Task<RunStateResponse> PollUntilTerminalAsync(Guid runId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var state = await Client.GetRunAsync(runId);
            if (state.Status is RunStatus.Succeeded or RunStatus.Failed or RunStatus.Cancelled)
            {
                return state;
            }

            await Task.Delay(40);
        }

        throw new TimeoutException($"run {runId} did not reach a terminal state within {timeout}");
    }

    [Fact]
    public async Task Create_async_then_poll_then_timeline_through_the_client()
    {
        var start = await Client.CreateInlineRunAsync(JsonElementOf(_demoPayload), DemoInputs(), async: true);

        start.IsCompleted.ShouldBeFalse(); // 202 accepted onto the async surface
        start.Status.ShouldBe(RunStatus.Running);

        var terminal = await PollUntilTerminalAsync(start.RunId, DurableHost.PollTimeout);
        terminal.Status.ShouldBe(RunStatus.Succeeded);
        terminal.Result!.Value.GetProperty("url").GetString().ShouldNotBeNullOrEmpty();

        var timeline = await Client.GetRunTimelineAsync(start.RunId);
        timeline.Status.ShouldBe(RunStatus.Succeeded);
        timeline.Region.ShouldBe("fake");
        timeline.Steps.Select(s => s.Kind).ShouldBe(["goto", "set"]);
        timeline.InputKeys.ShouldContain("backend");
    }

    [Fact]
    public async Task Stream_events_happy_path_through_the_client()
    {
        var start = await Client.CreateInlineRunAsync(JsonElementOf(_demoPayload), DemoInputs(), async: true);
        await PollUntilTerminalAsync(start.RunId, DurableHost.PollTimeout);

        var frames = new List<RunEventFrame>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await foreach (var frame in Client.StreamRunEventsAsync(start.RunId, ct: cts.Token))
        {
            frames.Add(frame);
        }

        frames[0].EventType.ShouldBe("RunStarted");
        frames.Select(f => f.EventType).ShouldContain("Navigated");
        frames[^1].EventType.ShouldBe("RunSucceeded");
        frames[^1].IsTerminal.ShouldBeTrue();
        frames.Where(f => f.Id is not null).Select(f => f.Id!.Value)
            .ShouldBe(frames.Where(f => f.Id is not null).Select(f => f.Id!.Value).Order().ToList());
    }

    [Fact]
    public async Task Stream_events_resume_from_a_midpoint_through_the_client()
    {
        var start = await Client.CreateInlineRunAsync(JsonElementOf(_demoPayload), DemoInputs(), async: true);
        await PollUntilTerminalAsync(start.RunId, DurableHost.PollTimeout);

        var all = new List<RunEventFrame>();
        await foreach (var frame in Client.StreamRunEventsAsync(start.RunId))
        {
            all.Add(frame);
        }

        var midpoint = all[all.Count / 2].Id!.Value;
        var tail = new List<RunEventFrame>();
        await foreach (var frame in Client.StreamRunEventsAsync(start.RunId, lastEventId: midpoint))
        {
            tail.Add(frame);
        }

        tail.ShouldAllBe(f => f.Id > midpoint); // the Last-Event-ID resume dropped everything already seen
        tail[^1].EventType.ShouldBe("RunSucceeded");
    }

    [Fact]
    public async Task Unknown_run_maps_to_not_found_against_the_real_host()
    {
        await Should.ThrowAsync<CrawldadNotFoundException>(() => Client.GetRunAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Save_then_get_a_payload_through_the_client()
    {
        var saved = await Client.SavePayloadAsync(JsonElementOf(_demoPayload));
        saved.Revision.ShouldBe(1);

        var fetched = await Client.GetPayloadAsync(saved.PayloadId);
        fetched.Name.ShouldBe("obs.demo");
        fetched.ScriptHash.ShouldBe(saved.ScriptHash);
    }
}
