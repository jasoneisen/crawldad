using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace Crawldad.Web.Features.Docs;

/// <summary>The committed <c>llms.txt</c> (repo root), embedded into this assembly under its own logical name so the served
/// bytes and the checked-in file are one source of truth (Deliverable 4, #20). Read once at first use.</summary>
internal static class LlmsText
{
    private const string _resourceName = "llms.txt";

    /// <summary>The raw <c>llms.txt</c> contents — an LLM-oriented index pointing at API.md, the served schema, and the
    /// curated examples (the llms.txt convention, llmstxt.org).</summary>
    public static string Content { get; } = ReadEmbedded();

    private static string ReadEmbedded()
    {
        // Embedded by Crawldad.Web.csproj under this exact logical name, so the stream is always present; a missing resource
        // is a build misconfiguration that fails loudly here at first use (no coverable branch).
        using var stream = typeof(LlmsText).Assembly.GetManifestResourceStream(_resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>
/// <c>GET /llms.txt</c> (Deliverable 4, #20): serves the committed <c>llms.txt</c> at the API root — the llms.txt convention
/// (llmstxt.org) — a compact, LLM-oriented index pointing at <c>docs/API.md</c>, the served schema
/// (<c>/schema/crawldad-1.schema.json</c>), and the curated examples, so an agent discovering the host can find the consumer
/// docs without scraping.
/// <para>
/// <b>Deliberately anonymous (CD-1),</b> for the same reason as <c>/health</c> and the schema route: a root-level discovery
/// pointer carries no tenant data and is only useful when reachable without a key. Allowlisted in the endpoint-enumeration
/// auth test. Served as <c>text/plain</c>.
/// </para>
/// </summary>
public static class LlmsEndpoint
{
    /// <summary>Handles <c>GET /llms.txt</c>.</summary>
    /// <returns><c>200</c> with the committed <c>llms.txt</c> as <c>text/plain</c>.</returns>
    [AllowAnonymous]
    [WolverineGet("/llms.txt")]
    public static IResult Get() => Results.Text(LlmsText.Content, "text/plain", Encoding.UTF8);
}
