using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Tests.Support;
using Crawldad.Web.Infrastructure.Browser.Fake;
using Microsoft.AspNetCore.TestHost;

namespace Crawldad.Tests.Integration;

/// <summary>The durable filesystem provider end-to-end through <c>POST /runs</c>: with
/// <c>Crawldad:Storage:Provider=filesystem</c>, a downloaded attachment and a failure screenshot are stored as real
/// files on disk, partitioned under the run's tenant, with zero external dependency.</summary>
[Collection(FileSystemStorageCollection.Name)]
public class FileSystemStorageTests(FileSystemStorageFixture fixture)
{
    private const string _contentId = "18dc2ee2-6e62-f5c6-8ec1-648aa28b2f48";

    [Fact]
    public async Task A_download_is_streamed_to_a_real_file_under_the_tenant_partition()
    {
        var payload = await File.ReadAllTextAsync(Path.Combine(Runner.FixturesRoot, "Payloads", "download-fragment.json"));
        var body = new JsonObject
        {
            ["payload"] = JsonNode.Parse(payload),
            ["inputs"] = new JsonObject
            {
                ["backend"] = new JsonObject { ["adapter"] = "fake", ["options"] = new JsonObject { ["fixture"] = "download-sample" } },
                ["attachmentStore"] = new JsonObject { ["kind"] = "filesystem", ["name"] = "attachmentStore" },
            },
        };

        var scenario = await fixture.Host.Scenario(x =>
        {
            x.Post.Json(body).ToUrl("/runs");
            x.StatusCodeShouldBeOk();
        });
        var root = await scenario.ReadAsJsonAsync<JsonElement>();
        root.GetProperty("status").GetString().ShouldBe("succeeded");
        root.GetProperty("result").GetProperty("attachments")[0].GetProperty("stored").GetBoolean().ShouldBeTrue();

        // The bytes landed as a real file, content-addressed, under {root}/{tenant}/downloads/{contentId} (idempotent: the
        // fragment downloads the same content twice, so exactly one blob exists).
        var blob = Path.Combine(fixture.Root, TestTenants.PrimaryId, "downloads", _contentId);
        File.Exists(blob).ShouldBeTrue();
        (await File.ReadAllBytesAsync(blob)).Length.ShouldBe(30);
    }

    [Fact]
    public async Task A_failure_screenshot_is_written_to_a_real_file_under_the_tenant_partition()
    {
        var body = new JsonObject
        {
            ["payload"] = JsonNode.Parse(
                """
                { "crawldad": "1", "name": "fs.shot", "config": { "backend": "input.backend" }, "vars": {},
                  "steps": [
                    { "goto": { "url": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx" } },
                    { "fail": { "class": "terminal", "code": "fs_boom", "message": "stop" } }
                  ],
                  "result": "'x'" }
                """),
            ["inputs"] = new JsonObject { ["backend"] = new JsonObject { ["adapter"] = "fake", ["options"] = new JsonObject { ["fixture"] = "caphome-search" } } },
            ["async"] = true,
        };

        var accepted = await fixture.Host.Scenario(x =>
        {
            x.Post.Json(body).ToUrl("/runs");
            x.StatusCodeShouldBe(202);
        });
        var runId = (await accepted.ReadAsJsonAsync<JsonElement>()).GetProperty("runId").GetGuid();
        var terminal = await DurableHost.PollUntilTerminalAsync(fixture.Host, runId, TimeSpan.FromSeconds(60));
        terminal.GetProperty("status").GetString().ShouldBe("failed");

        var timeline = await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/runs/{runId}/timeline");
            x.StatusCodeShouldBe(200);
        });
        var screenshotRef = (await timeline.ReadAsJsonAsync<JsonElement>()).GetProperty("failure").GetProperty("screenshotRef").GetString();
        screenshotRef.ShouldStartWith("screenshots/");

        // The screenshot bytes landed as a real PNG under {root}/{tenant}/screenshots/{sha}.png (the ref's tenant-independent tail).
        var tail = screenshotRef!["screenshots/".Length..];
        var blob = Path.Combine(fixture.Root, TestTenants.PrimaryId, "screenshots", tail);
        File.Exists(blob).ShouldBeTrue();
        (await File.ReadAllBytesAsync(blob))[..8].ShouldBe([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]); // PNG signature

        // And it streams back byte-for-byte over GET /runs/{id}/screenshots/{ref} — the durable filesystem read path end-to-end.
        using var client = fixture.Host.GetTestServer().CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", TestTenants.Bearer(TestTenants.PrimaryKey));
        using var response = await client.GetAsync(new Uri($"/runs/{runId}/screenshots/{tail}", UriKind.Relative));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(await File.ReadAllBytesAsync(blob));
    }
}

/// <summary>One filesystem-provider host + a temp blob root, shared by the class and cleaned up on dispose.</summary>
public sealed class FileSystemStorageFixture : IAsyncLifetime
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "crawldad-fsstorage", Guid.NewGuid().ToString("N"));

    public IAlbaHost Host { get; private set; } = null!;

    public async Task InitializeAsync() =>
        Host = await DurableHost.BuildAsync(
            "crawldad_fsstorage",
            new FakeBrowserBackend(Runner.FixturesRoot),
            settings:
            [
                new("Crawldad:Storage:Provider", "filesystem"),
                new("Crawldad:Storage:FileSystem:Root", Root),
            ]);

    public async Task DisposeAsync()
    {
        await Host.DisposeAsync();
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

/// <summary>Isolates the filesystem-provider host on its own schema.</summary>
[CollectionDefinition(Name)]
public sealed class FileSystemStorageCollection : ICollectionFixture<FileSystemStorageFixture>
{
    public const string Name = "filesystem-storage";
}
