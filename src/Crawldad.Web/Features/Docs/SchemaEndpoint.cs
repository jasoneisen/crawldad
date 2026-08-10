using System.Text;
using Crawldad.Web.Features.Payloads;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace Crawldad.Web.Features.Docs;

/// <summary><c>GET /schema/crawldad-1.schema.json</c>: serves the payload v1 JSON Schema verbatim from the embedded
/// resource — the same bytes the save-time validator enforces (<see cref="PayloadSchema"/>). Deliberately anonymous: a
/// public, tenant-independent artifact usable as an editor <c>$schema</c> target without a key. <c>application/schema+json</c>.</summary>
public static class SchemaEndpoint
{
    /// <summary>The media type for the served schema — the IANA-registered JSON Schema type.</summary>
    public const string SchemaMediaType = "application/schema+json";

    [AllowAnonymous]
    [WolverineGet("/schema/crawldad-1.schema.json")]
    public static IResult Get() => Results.Text(PayloadSchema.Json, SchemaMediaType, Encoding.UTF8);
}
