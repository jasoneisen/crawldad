using System.Text.Json;
using System.Text.Json.Nodes;
using Crawldad.Tests.Support;
using Crawldad.Web.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>
/// The real-Chromium screenshot-on-failure gate (§13): drives a failing async run through the executor saga against real
/// headless Chromium (the parity <c>local</c> backend, served entirely from the fixture corpus — no live traffic), so the
/// product <see cref="Crawldad.Web.Infrastructure.Browser.Real.PlaywrightPageHandle"/> captures a <b>genuine</b> PNG via
/// Playwright's screenshot API. The <c>StepFailed</c> event links the content-addressed ref; the deletable blob store holds
/// the real image bytes (§12). This covers the real screenshot path the fake cannot.
/// </summary>
[Collection(RealChromiumParityCollection.Name)]
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
}
