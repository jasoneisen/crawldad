using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace Crawldad.Api.Features.Docs;

/// <summary>The committed <c>llms.txt</c> (repo root), embedded into this assembly under its own logical name so the served
/// bytes and the checked-in file are one source of truth. Read once at first use.</summary>
internal static class LlmsText
{
    private const string _resourceName = "llms.txt";

    /// <summary>The raw <c>llms.txt</c> contents — an LLM-oriented index pointing at API.md, the served schema, and the
    /// curated examples (the llms.txt convention, llmstxt.org).</summary>
    public static string Content { get; } = ReadEmbedded();

    private static string ReadEmbedded()
    {
        // Embedded by Crawldad.Api.csproj under this exact logical name, so the stream is always present; a missing resource
        // is a build misconfiguration that fails loudly here at first use (no coverable branch).
        using var stream = typeof(LlmsText).Assembly.GetManifestResourceStream(_resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary><c>GET /llms.txt</c>: serves the committed <c>llms.txt</c> at the API root (the llms.txt convention,
/// llmstxt.org), pointing at <c>docs/API.md</c>, the served schema, and curated examples. Deliberately anonymous, like
/// <c>/health</c> and the schema route: a root-level discovery pointer carries no tenant data. Served as <c>text/plain</c>.</summary>
public static class LlmsEndpoint
{
    [AllowAnonymous]
    [WolverineGet("/llms.txt")]
    public static IResult Get() => Results.Text(LlmsText.Content, "text/plain", Encoding.UTF8);
}
