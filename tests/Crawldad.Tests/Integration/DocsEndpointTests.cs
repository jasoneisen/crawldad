using System.Text.Json;
using Alba;
using Crawldad.Web.Features.Docs;
using Crawldad.Web.Features.Payloads;

namespace Crawldad.Tests.Integration;

/// <summary>
/// The anonymous #20 docs surface over real HTTP: <c>GET /schema/crawldad-1.schema.json</c> and <c>GET /llms.txt</c> both
/// answer <b>without a key</b> (the deliberate opt-out from the tenant gate, decided like <c>/health</c>), serve the exact
/// embedded bytes, and carry the intended media types. The endpoint-enumeration <see cref="AuthenticationTests"/> already
/// proves these are the only anonymous routes besides <c>/health</c>; this proves they are reachable and correct.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class DocsEndpointTests(AppFixture fixture)
{
    private IAlbaHost Host => fixture.Host;

    [Fact]
    public async Task The_payload_schema_is_served_anonymously_as_the_embedded_document()
    {
        var result = await Host.Scenario(x =>
        {
            x.RemoveRequestHeader("Authorization"); // drop the fixture's default key — the schema must answer anonymously
            x.Get.Url("/schema/crawldad-1.schema.json");
            x.StatusCodeShouldBeOk();
        });

        result.Context.Response.ContentType!.ShouldStartWith(SchemaEndpoint.SchemaMediaType); // application/schema+json

        var body = await result.ReadAsTextAsync();
        body.ShouldBe(PayloadSchema.Json); // the served bytes ARE the validator's embedded schema (one source of truth)

        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("$id").GetString().ShouldBe("https://crawldad.dev/schema/crawldad-1.schema.json");
        document.RootElement.GetProperty("title").GetString().ShouldBe("Crawldad payload v1");
    }

    [Fact]
    public async Task Llms_txt_is_served_anonymously_as_plain_text_pointing_at_the_docs()
    {
        var result = await Host.Scenario(x =>
        {
            x.RemoveRequestHeader("Authorization"); // a root discovery pointer must answer anonymously
            x.Get.Url("/llms.txt");
            x.StatusCodeShouldBeOk();
        });

        result.Context.Response.ContentType!.ShouldStartWith("text/plain");

        var body = await result.ReadAsTextAsync();
        body.ShouldBe(LlmsText.Content);            // the served bytes ARE the committed, embedded llms.txt
        body.ShouldContain("docs/API.md");          // it points at the consumer reference
        body.ShouldContain("schema/crawldad-1.schema.json");
    }
}
