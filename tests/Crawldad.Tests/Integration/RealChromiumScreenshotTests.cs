using System.Text.Json;
using System.Text.Json.Nodes;
using Crawldad.Tests.Support;
using Crawldad.Web.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>Drives a failing async run through the executor saga against real headless Chromium (the parity
/// <c>local</c> backend, fixture corpus only — no live traffic) so the product's Playwright page handle captures a
/// genuine PNG; the <c>StepFailed</c> event links the content-addressed blob ref. Covers the real path the fake cannot.</summary>
[Collection(RealChromiumCollection.Name)]
public class RealChromiumScreenshotTests(ParityAppFixture fixture)
{
    [Fact]
    public async Task A_failing_async_run_captures_a_real_page_screenshot_on_the_failing_step()
    {
        var host = fixture.Host;

        // Navigate a real rendered page, then fail — the executor screenshots the live page on the failing step.
        var body = new JsonObject
        {
            ["payload"] = JsonNode.Parse(
                """
                { "crawldad": "1", "name": "shot.parity", "config": { "backend": "input.backend" }, "vars": {},
                  "steps": [
                    { "goto": { "url": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx?module=Enforcement&TabName=Enforcement" } },
                    { "waitForLoadState": { "state": "load" } },
                    { "fail": { "class": "terminal", "code": "shot_boom", "message": "stop after render" } }
                  ],
                  "result": "'x'" }
                """),
            ["inputs"] = new JsonObject { ["backend"] = new JsonObject { ["adapter"] = "local", ["options"] = new JsonObject { ["fixture"] = "caphome-search" } } },
            ["async"] = true,
        };

        var accepted = await host.Scenario(x =>
        {
            x.Post.Json(body).ToUrl("/runs");
            x.StatusCodeShouldBe(202);
        });
        var runId = (await accepted.ReadAsJsonAsync<JsonElement>()).GetProperty("runId").GetGuid();
        var terminal = await DurableHost.PollUntilTerminalAsync(host, runId, TimeSpan.FromSeconds(60));
        terminal.GetProperty("status").GetString().ShouldBe("failed");

        // The timeline links the failure's screenshot ref (a content-addressed blob key).
        var timelineResult = await host.Scenario(x =>
        {
            x.Get.Url($"/runs/{runId}/timeline");
            x.StatusCodeShouldBe(200);
        });
        var failure = (await timelineResult.ReadAsJsonAsync<JsonElement>()).GetProperty("failure");
        failure.GetProperty("code").GetString().ShouldBe("shot_boom");
        var screenshotRef = failure.GetProperty("screenshotRef").GetString();
        screenshotRef.ShouldStartWith("screenshots/");

        // The stored blob is a genuine PNG captured by Playwright (its 8-byte signature), not the fake's stand-in.
        var screenshots = (InMemoryScreenshotStore)host.Services.GetRequiredService<IScreenshotStore>();
        var png = screenshots.Blobs[screenshotRef!];
        png[..8].ShouldBe([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        png.Length.ShouldBeGreaterThan(100); // a real rendered screenshot, not an 8-byte stub
    }

    [Fact]
    public async Task An_explicit_screenshot_node_captures_a_real_page_png_through_the_saga()
    {
        var host = fixture.Host;

        // A succeeding async run that navigates a real page and then screenshots it — the product PlaywrightPageHandle
        // captures a genuine PNG, stored through the same IScreenshotStore seam as screenshot-on-failure.
        var body = new JsonObject
        {
            ["payload"] = JsonNode.Parse(
                """
                { "crawldad": "1", "name": "shot.node.parity", "config": { "backend": "input.backend" }, "vars": {},
                  "steps": [
                    { "goto": { "url": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx?module=Enforcement&TabName=Enforcement" } },
                    { "waitForLoadState": { "state": "load" } },
                    { "screenshot": { "name": "caphome" } }
                  ],
                  "result": "'x'" }
                """),
            ["inputs"] = new JsonObject { ["backend"] = new JsonObject { ["adapter"] = "local", ["options"] = new JsonObject { ["fixture"] = "caphome-search" } } },
            ["async"] = true,
        };

        var accepted = await host.Scenario(x =>
        {
            x.Post.Json(body).ToUrl("/runs");
            x.StatusCodeShouldBe(202);
        });
        var runId = (await accepted.ReadAsJsonAsync<JsonElement>()).GetProperty("runId").GetGuid();
        var terminal = await DurableHost.PollUntilTerminalAsync(host, runId, TimeSpan.FromSeconds(60));
        terminal.GetProperty("status").GetString().ShouldBe("succeeded");

        // The timeline surfaces the explicit capture as an artifact: its content-addressed ref, author label, and real byte size.
        var timelineResult = await host.Scenario(x =>
        {
            x.Get.Url($"/runs/{runId}/timeline");
            x.StatusCodeShouldBe(200);
        });
        var timeline = await timelineResult.ReadAsJsonAsync<JsonElement>();
        var shot = timeline.GetProperty("screenshots").EnumerateArray().ToList().ShouldHaveSingleItem();
        shot.GetProperty("name").GetString().ShouldBe("caphome");
        var screenshotRef = shot.GetProperty("screenshotRef").GetString();
        screenshotRef.ShouldStartWith("screenshots/");

        // The stored blob is a genuine PNG captured by Playwright (its 8-byte signature), not the fake's 8-byte stand-in.
        var screenshots = (InMemoryScreenshotStore)host.Services.GetRequiredService<IScreenshotStore>();
        var png = screenshots.Blobs[screenshotRef!];
        png[..8].ShouldBe([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        png.Length.ShouldBeGreaterThan(100);
        shot.GetProperty("size").GetInt64().ShouldBe(png.Length); // the event's Size is the real capture's byte count
    }
}
