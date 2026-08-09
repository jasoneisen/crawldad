using System.Text;
using Crawldad.Web.Features.Payloads;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace Crawldad.Web.Features.Docs;

/// <summary>
/// <c>GET /schema/crawldad-1.schema.json</c> (Deliverable 2, #20): serves the payload v1 JSON Schema — the normative DSL
/// reference — verbatim from the assembly-embedded <c>crawldad-1.schema.json</c>, the same bytes the save-time validator
/// enforces (<see cref="PayloadSchema"/>). Editors resolve it for autocomplete; an LLM fetches it to author a payload.
/// <para>
/// <b>Deliberately anonymous (CD-1).</b> Like <c>/health</c>, this route opts out of the <c>RequireAuthorizeOnAll</c> tenant
/// gate with <see cref="AllowAnonymousAttribute"/>: the schema is a public, tenant-independent product artifact (it carries no
/// tenant data), and a public URL is what makes it usable as an editor <c>$schema</c> target and an LLM reference without
/// distributing a key. The endpoint-enumeration auth test allowlists exactly this route while asserting every other endpoint
/// rejects an unauthenticated request. Served as <c>application/schema+json</c> (the registered JSON Schema media type; any
/// JSON parser reads the <c>+json</c> suffix).
/// </para>
/// </summary>
public static class SchemaEndpoint
{
    /// <summary>The media type for the served schema — the IANA-registered JSON Schema type.</summary>
    public const string SchemaMediaType = "application/schema+json";

    /// <summary>Handles <c>GET /schema/crawldad-1.schema.json</c>.</summary>
    /// <returns><c>200</c> with the embedded schema document as <c>application/schema+json</c>.</returns>
    [AllowAnonymous]
    [WolverineGet("/schema/crawldad-1.schema.json")]
    public static IResult Get() => Results.Text(PayloadSchema.Json, SchemaMediaType, Encoding.UTF8);
}
