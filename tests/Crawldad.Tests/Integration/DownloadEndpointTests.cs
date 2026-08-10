using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Tests.Support;

namespace Crawldad.Tests.Integration;

/// <summary>The <c>download</c> action end-to-end through <c>POST /runs</c> against the fake backend and the
/// DI-registered in-memory sink: a storageTarget input resolves to the sink, the bytes hash to the pinned
/// <c>contentId</c>, and <c>stats.downloads</c> counts the completed downloads.</summary>
[Collection(IntegrationCollection.Name)]
public class DownloadEndpointTests(AppFixture fixture)
{
    private const string _contentId = "18dc2ee2-6e62-f5c6-8ec1-648aa28b2f48";

    private IAlbaHost Host => fixture.Host;

    [Fact]
    public async Task Download_over_http_streams_to_the_sink_and_reports_the_content_id()
    {
        var payload = await File.ReadAllTextAsync(Path.Combine(Runner.FixturesRoot, "Payloads", "download-fragment.json"));
        var body = new JsonObject
        {
            ["payload"] = JsonNode.Parse(payload),
            ["inputs"] = new JsonObject
            {
                ["backend"] = new JsonObject
                {
                    ["adapter"] = "fake",
                    ["options"] = new JsonObject { ["fixture"] = "download-sample" },
                },
                ["attachmentStore"] = new JsonObject { ["kind"] = "fake", ["name"] = "attachmentStore" },
            },
        };

        var scenario = await Host.Scenario(x =>
        {
            x.Post.Json(body).ToUrl("/runs");
            x.StatusCodeShouldBeOk();
        });
        var root = await scenario.ReadAsJsonAsync<JsonElement>();

        root.GetProperty("status").GetString().ShouldBe("succeeded");
        var attachments = root.GetProperty("result").GetProperty("attachments");
        attachments.GetArrayLength().ShouldBe(2);
        attachments[0].GetProperty("attachmentId").GetString().ShouldBe(_contentId);
        attachments[0].GetProperty("internalFilename").GetString().ShouldBe($"{_contentId}.jpg");
        attachments[0].GetProperty("storedAs").GetString().ShouldBe($"{_contentId}.pdf");
        root.GetProperty("stats").GetProperty("downloads").GetInt32().ShouldBe(2);
    }
}
