using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Api.Features.Webhooks;
using Crawldad.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>The two queued-run trigger paths deliver too: cancelling a run while it is still queued fires a
/// <c>run.cancelled</c> webhook, and a run that outlives its max queue wait fires a <c>run.failed</c>
/// (<c>queue_wait_exceeded</c>). Each builds its own cap-1 gated host so a blocked run holds the one slot while a second
/// run waits in the queue.</summary>
public class WebhookQueueTriggerTests
{
    private static readonly CancellationToken _ct = CancellationToken.None;

    // A minimal async payload with one gated postback: it blocks at the CapHome page (holding its slot) until released.
    private static JsonObject Body() => new()
    {
        ["payload"] = JsonNode.Parse("""
            { "crawldad": "1", "name": "webhook.slot", "config": { "backend": "input.backend" }, "vars": {},
              "steps": [
                { "goto": { "url": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx?module=Enforcement&TabName=Enforcement" } },
                { "waitForRequest": { "urlPrefix": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx", "method": "POST",
                    "trigger": [ { "click": { "selector": "#ctl00_PlaceHolderMain_btnNewSearch" } } ] } },
                { "log": { "level": "info", "message": "done" } }
              ],
              "result": "'done'" }
            """),
        ["inputs"] = new JsonObject { ["backend"] = new JsonObject { ["adapter"] = "fake", ["options"] = new JsonObject { ["fixture"] = "caphome-resume" } } },
        ["async"] = true,
    };

    private static async Task<Guid> StartAsync(IAlbaHost host, string expectedStatus)
    {
        var accepted = await host.Scenario(x =>
        {
            x.Post.Json(Body()).ToUrl("/runs");
            x.StatusCodeShouldBe(202);
        });
        var root = (await accepted.ReadAsJsonAsync<JsonElement>()).Clone();
        root.GetProperty("status").GetString().ShouldBe(expectedStatus);
        return root.GetProperty("runId").GetGuid();
    }

    private static async Task SubscribeAsync(IAlbaHost host, string name) =>
        await host.Services.GetRequiredService<IWebhookEndpointStore>()
            .RegisterAsync(TestTenants.PrimaryId, name, $"https://hooks.example.com/{name}", "whsec_queue_0123456789", [], _ct);

    private static JsonElement DeliveredBody(RecordingWebhookSender sender, string name) =>
        JsonDocument.Parse(sender.Calls.First(c => c.Url.EndsWith("/" + name, StringComparison.Ordinal)).Body).RootElement;

    [Fact]
    public async Task Cancelling_a_queued_run_delivers_a_cancelled_event()
    {
        var holder = new GateHolder();
        var gate = new RunGate("CapHome");
        var sender = new RecordingWebhookSender();
        sender.AlwaysDeliver();
        await using var host = await WebhookTesting.BuildGatedHostAsync("crawldad_webhook_qcancel", holder, sender);
        await SubscribeAsync(host, "cancel-hook");

        holder.Arm(gate);
        var blocked = await StartAsync(host, "running"); // holds the one slot, blocked at the gate
        await gate.Reached.WaitAsync(DurableHost.PollTimeout);
        var queued = await StartAsync(host, "queued");   // waits behind the blocker

        await host.Scenario(x =>
        {
            x.Post.Json(new JsonObject()).ToUrl($"/runs/{queued}/cancel");
            x.StatusCodeShouldBe(202);
        });

        await WebhookTesting.PollAsync(() => sender.Calls.Any(c => c.Url.EndsWith("/cancel-hook", StringComparison.Ordinal)), "no cancel webhook delivered");
        var body = DeliveredBody(sender, "cancel-hook");
        body.GetProperty("type").GetString().ShouldBe("run.cancelled");
        body.GetProperty("runId").GetGuid().ShouldBe(queued);

        gate.Release();
        await DurableHost.PollUntilTerminalAsync(host, blocked, DurableHost.PollTimeout);
    }

    [Fact]
    public async Task A_queued_run_timing_out_delivers_a_failed_event()
    {
        var holder = new GateHolder();
        var gate = new RunGate("CapHome");
        var sender = new RecordingWebhookSender();
        sender.AlwaysDeliver();
        await using var host = await WebhookTesting.BuildGatedHostAsync("crawldad_webhook_qtimeout", holder, sender, ("Crawldad:Limits:MaxQueueWaitMs", "200"));
        await SubscribeAsync(host, "timeout-hook");

        holder.Arm(gate);
        var blocked = await StartAsync(host, "running"); // holds the one slot
        await gate.Reached.WaitAsync(DurableHost.PollTimeout);
        var queued = await StartAsync(host, "queued");   // will exceed the 200ms max queue wait

        await WebhookTesting.PollAsync(() => sender.Calls.Any(c => c.Url.EndsWith("/timeout-hook", StringComparison.Ordinal)), "no timeout webhook delivered");
        var body = DeliveredBody(sender, "timeout-hook");
        body.GetProperty("type").GetString().ShouldBe("run.failed");
        body.GetProperty("runId").GetGuid().ShouldBe(queued);
        body.GetProperty("failure").GetProperty("code").GetString().ShouldBe("queue_wait_exceeded");

        gate.Release();
        await DurableHost.PollUntilTerminalAsync(host, blocked, DurableHost.PollTimeout);
    }
}
