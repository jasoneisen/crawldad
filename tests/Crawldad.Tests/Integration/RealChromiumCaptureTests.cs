using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Crawldad.Tests.Support;
using Crawldad.Web.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>Drives a <c>capture</c> run through the executor saga against real headless Chromium (the parity
/// <c>local</c> backend, fixture corpus only — no live traffic) so the product's Playwright page/locator handles
/// serialise a genuine document and element subtree. Covers the real <c>page.content()</c> + element <c>outerHTML</c>
/// paths the fake cannot, including a selector that matches nothing (the empty-capture short-circuit).</summary>
[Collection(RealChromiumCollection.Name)]
public class RealChromiumCaptureTests(ParityAppFixture fixture)
{
    [Fact]
    public async Task Capture_streams_the_real_document_and_element_subtree_to_byo_storage()
    {
        var host = fixture.Host;

        // Navigate a real rendered page, then capture: the whole document, a matching element subtree (body), and a
        // selector that matches nothing (an empty capture). Each streams content-addressed to the fake BYO sink.
        var body = new JsonObject
        {
            ["payload"] = JsonNode.Parse(
                """
                { "crawldad": "1", "name": "capture.parity", "config": { "backend": "input.backend" }, "vars": { "refs": [] },
                  "steps": [
                    { "goto": { "url": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx?module=Enforcement&TabName=Enforcement" } },
                    { "waitForLoadState": { "state": "load" } },
                    { "capture": { "to": "{ kind: 'fake', name: 'parity' }", "var": "doc" } },
                    { "capture": { "to": "{ kind: 'fake', name: 'parity' }", "selector": "body", "var": "sub" } },
                    { "capture": { "to": "{ kind: 'fake', name: 'parity' }", "selector": "#definitely-not-present-xyz", "var": "empty" } },
                    { "push": { "into": "refs", "value": "{ doc: doc.contentId, sub: sub.contentId, empty: empty.contentId, docSize: doc.sizeBytes, subSize: sub.sizeBytes, emptySize: empty.sizeBytes }" } }
                  ],
                  "result": "{ refs: refs }" }
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
        terminal.GetProperty("status").GetString().ShouldBe("succeeded", terminal.ToString());

        // The result is a compact manifest of refs; the whole document is larger than the body subtree, and the
        // no-match selector captured an empty (0-byte) document.
        var refs = terminal.GetProperty("result").GetProperty("refs")[0];
        refs.GetProperty("docSize").GetInt64().ShouldBeGreaterThan(refs.GetProperty("subSize").GetInt64());
        refs.GetProperty("emptySize").GetInt64().ShouldBe(0);

        // The timeline surfaces all three captures as artifacts (refs + metadata, never HTML).
        var timelineResult = await host.Scenario(x =>
        {
            x.Get.Url($"/runs/{runId}/timeline");
            x.StatusCodeShouldBe(200);
        });
        var captures = (await timelineResult.ReadAsJsonAsync<JsonElement>()).GetProperty("captures").EnumerateArray().ToList();
        captures.Count.ShouldBe(3);

        // The stored bytes are the genuine serialised DOM Playwright produced: the full document carries the doctype +
        // the <html> element itself, and the body subtree is the element's outerHTML — a real <body> tag, not the doctype.
        var sink = (FakeDownloadSink)host.Services.GetRequiredKeyedService<IDownloadSink>("fake");
        var docHtml = Encoding.UTF8.GetString(sink.BytesOf(Guid.Parse(refs.GetProperty("doc").GetString()!)));
        docHtml.TrimStart().ShouldStartWith("<!"); // page.content() prepends the doctype
        docHtml.ShouldContain("<html"); // Chromium serialises tag names lowercase

        var subHtml = Encoding.UTF8.GetString(sink.BytesOf(Guid.Parse(refs.GetProperty("sub").GetString()!)));
        subHtml.TrimStart().ShouldStartWith("<body"); // the element's own outerHTML, no doctype/<html> wrapper
        sink.BytesOf(Guid.Parse(refs.GetProperty("empty").GetString()!)).ShouldBeEmpty();
    }
}
