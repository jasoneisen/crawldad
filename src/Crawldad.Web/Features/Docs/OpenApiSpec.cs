using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Crawldad.Contracts;
using Crawldad.Contracts.Browsers;
using Crawldad.Contracts.Payloads;
using Crawldad.Contracts.Runs;
using Crawldad.Web.Infrastructure.Security;

namespace Crawldad.Web.Features.Docs;

/// <summary>The generated OpenAPI 3.1 document for the HTTP envelope, served at <c>GET /openapi.json</c>
/// (<see cref="OpenApiEndpoint"/>). <b>Built, not hand-copied:</b> component schemas come from the contract types via
/// <see cref="JsonSchemaExporter"/> so they cannot drift from the DTOs; a drift test checks this table against Wolverine's live routes.</summary>
public static class OpenApiSpec
{
    /// <summary>The served payload DSL JSON Schema that payload-carrying request bodies reference instead of
    /// restating the DSL. Same-origin path so it resolves against whatever host serves this document; a drift test asserts it
    /// equals <see cref="SchemaEndpoint"/>'s route.</summary>
    public const string PayloadSchemaUrl = "/schema/crawldad-1.schema.json";

    private const string _openApiVersion = "3.1.0";

    private const string _runs = "Runs";
    private const string _payloads = "Payloads";
    private const string _browsers = "Browsers";
    private const string _docs = "Docs";

    private const string _infoDescription =
        "OpenAPI description of the Crawldad HTTP envelope (issue #21): every routable endpoint, its authentication, the "
        + "request/response contracts (from Crawldad.Contracts), and the status codes — including the 202 running/queued run "
        + "shapes and the 429 `queue_depth_exceeded` admission limit. This documents the ENVELOPE only; the payload DSL is the "
        + "published JSON Schema served at `" + PayloadSchemaUrl + "`, which payload-carrying request bodies reference rather "
        + "than restate. Authenticate every non-anonymous request with `Authorization: Bearer <api-key>` or `X-Api-Key: <api-key>`.";

    // Options that mirror the live wire conventions (ContractsJson: string enums, camelCase) so exported schemas match the
    // bytes the API actually serializes. Strict number handling (not JsonSerializerDefaults.Web) keeps integers typed as
    // `integer` rather than the read-from-string `["string","integer"]` union the Web defaults would emit.
    private static readonly JsonSerializerOptions _schemaOptions = CreateSchemaOptions();

    private static readonly JsonSchemaExporterOptions _exporterOptions = new()
    {
        TreatNullObliviousAsNonNullable = true,

        // JsonSchemaExporter derives `required` from constructor-parameter defaults and ignores [JsonIgnore(WhenWritingNull)],
        // so a field the API omits at runtime (a running run's result/failure/partial/stats, a diff entry's `from`) would be
        // wrongly marked required. Relax `required` to match what the wire actually emits (nested types included).
        TransformSchemaNode = RelaxRequired,
    };

    // Served as application/json (not embedded in HTML), so relax escaping: em dashes, backticks, `<`, `>`, `+` in the
    // descriptions render as themselves rather than \uXXXX. Still valid JSON; cleaner for a published spec.
    private static readonly JsonSerializerOptions _writeOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // Every DTO the envelope references, generated from its contract type. The three payload-carrying request bodies swap
    // their `payload` property for a $ref to the published DSL schema; StartRunRequest additionally relaxes `required`
    // (payload XOR payloadId — nothing is unconditionally required).
    private static readonly Component[] _components =
    [
        new(nameof(HealthStatus), typeof(HealthStatus)),
        new(nameof(PayloadListResponse), typeof(PayloadListResponse)),
        new(nameof(PayloadResponse), typeof(PayloadResponse)),
        new(nameof(PayloadRevisionResponse), typeof(PayloadRevisionResponse)),
        new(nameof(PayloadDiffResponse), typeof(PayloadDiffResponse)),
        new(nameof(PayloadValidationProblem), typeof(PayloadValidationProblem)),
        new(nameof(RunResponse), typeof(RunResponse)),
        new(nameof(RunStateResponse), typeof(RunStateResponse)),
        new(nameof(RunRejection), typeof(RunRejection)),
        new(nameof(RunDriftResponse), typeof(RunDriftResponse)),
        new(nameof(RunTimelineResponse), typeof(RunTimelineResponse)),
        new(nameof(QueueStatsResponse), typeof(QueueStatsResponse)),
        new(nameof(StartRunRequest), typeof(StartRunRequest), SwapPayload: true, PayloadOptional: true),
        new(nameof(ReplayRunRequest), typeof(ReplayRunRequest)),
        new(nameof(SavePayloadRequest), typeof(SavePayloadRequest), SwapPayload: true),
        new(nameof(RevisePayloadRequest), typeof(RevisePayloadRequest), SwapPayload: true),
        new(nameof(RenamePayloadRequest), typeof(RenamePayloadRequest)),
        new(nameof(RegisterBrowserRequest), typeof(RegisterBrowserRequest)),
        new(nameof(BrowserSummary), typeof(BrowserSummary)),
        new(nameof(BrowserListResponse), typeof(BrowserListResponse)),
    ];

    // The single source of truth for the envelope. The drift test asserts this set of (method, path) equals the live route
    // table and that every anonymous flag matches the endpoint's [AllowAnonymous] opt-out.
    private static readonly Endpoint[] _endpoints =
    [
        // Anonymous, tenant-independent product artifacts (the same opt-out as /health, decided in each endpoint).
        new("get", "/health", "getHealth", "Liveness probe.", _docs, Anonymous: true, [], null,
            [new("200", "The service is live.", Component: nameof(HealthStatus))]),
        new("get", "/openapi.json", "getOpenApi", "This OpenAPI document.", _docs, Anonymous: true, [], null,
            [new("200", "The OpenAPI 3.1 description of this API.", MediaType: "application/json", Schema: OpenApiSelfSchema)],
            Description: "The generated OpenAPI 3.1 description of the HTTP envelope. Anonymous, like /health and the payload schema: it is a public, tenant-independent product artifact carrying no tenant data."),
        new("get", PayloadSchemaUrl, "getPayloadSchema", "The payload DSL JSON Schema.", _docs, Anonymous: true, [], null,
            [new("200", "The normative payload v1 JSON Schema (issue #20).", MediaType: SchemaEndpoint.SchemaMediaType, Schema: PayloadDslSchema)]),
        new("get", "/llms.txt", "getLlmsTxt", "The llms.txt discovery index.", _docs, Anonymous: true, [], null,
            [new("200", "The llms.txt discovery index pointing at the reference, schema, and examples.", MediaType: "text/plain", Schema: PlainTextSchema)]),

        // Runs.
        new("post", "/runs", "startRun", "Start a run.", _runs, Anonymous: false, [],
            new(nameof(StartRunRequest), "The inline payload (or a pinned payloadId + optional revision) plus inputs and the async flag."),
            [
                new("200", "A synchronous run reached its terminal state (a failed RUN is still 200 — see failure).", Component: nameof(RunResponse)),
                new("202", "The run is running (async or sync-upgraded) or queued at the concurrent-run cap; poll GET /runs/{id}.", Component: nameof(RunStateResponse)),
                new("400", "The request references an unrunnable pinned payload (unknown_payload / unknown_revision / payload_archived). A malformed request body is instead an RFC 7807 problem+json.", Component: nameof(RunRejection)),
                new("429", "The tenant's admission queue is at its depth (queue_depth_exceeded) — the only admission 429; at the run cap a run queues (202) rather than being rejected.", Component: nameof(RunRejection)),
            ],
            Description: "Executes exactly one payload, inline (default, synchronous terminal RunResponse) or in the background (async:true → 202 + poll). At the tenant's concurrent-run cap the run is queued (202 { status:\"queued\", position }), not rejected. Supply the payload one of two mutually-exclusive ways: an inline `payload` document, or a pinned `payloadId` (+ optional `revision`)."),
        new("get", "/runs/{id}", "getRun", "Poll a run's state.", _runs, Anonymous: false, [Id],
            null,
            [new("200", "The run's current state (queued/running/terminal).", Component: nameof(RunStateResponse)), NotFound("run")]),
        new("post", "/runs/{id}/cancel", "cancelRun", "Cancel a run.", _runs, Anonymous: false, [Id], null,
            [new("202", "Cancellation was accepted; the pre-cancel state is returned.", Component: nameof(RunStateResponse)), NotFound("run")]),
        new("get", "/runs/{id}/events", "streamRunEvents", "Stream a run's trace as Server-Sent Events.", _runs, Anonymous: false, [Id], null,
            [new("200", "An SSE stream of the run's (scrubbed) trace, backfilled from the durable stream then live-tailed until terminal.", MediaType: "text/event-stream", Schema: PlainTextSchema), NotFound("run")]),
        new("post", "/runs/{id}/replay", "replayRun", "Replay a pinned run.", _runs, Anonymous: false, [Id],
            new(nameof(ReplayRunRequest), "The resupplied inputs and async flag (input values are never persisted, so inputs are supplied fresh)."),
            [
                new("200", "A synchronous replay reached its terminal state.", Component: nameof(RunResponse)),
                new("202", "The replay is running or queued; poll GET /runs/{id}.", Component: nameof(RunStateResponse)),
                NotFound("run"),
                new("400", "The run is inline and not replayable (inline_not_replayable), or its pinned payload is unrunnable via the shared admission path (payload_archived / unknown_payload / unknown_revision) — a replay resolves the pin and dispatches exactly as POST /runs.", Component: nameof(RunRejection)),
                new("429", "The tenant's admission queue is at its depth (queue_depth_exceeded); a replay runs the same admission path as POST /runs.", Component: nameof(RunRejection)),
            ]),
        new("get", "/runs/{id}/drift", "getRunDrift", "Report a run's payload drift.", _runs, Anonymous: false, [Id], null,
            [new("200", "The run's pinned revision vs the payload head.", Component: nameof(RunDriftResponse)), NotFound("run")]),
        new("get", "/runs/{id}/timeline", "getRunTimeline", "The run observability timeline.", _runs, Anonymous: false, [Id], null,
            [new("200", "The run's ordered steps, extracts, downloads, screenshots, and failure.", Component: nameof(RunTimelineResponse)), NotFound("run")]),
        new("get", "/runs/{id}/screenshots/{reference}", "getRunScreenshot", "Retrieve a run's captured screenshot.", _runs, Anonymous: false, [Id, Reference], null,
            [
                new("200", "The captured screenshot as PNG bytes (content-addressed, so privately cacheable with a digest ETag).", MediaType: "image/png", Schema: BinarySchema),
                new("404", "No such run, the ref is not recorded on this run, or the screenshot has expired per the storage retention policy."),
            ],
            Description: "Streams a captured screenshot (an authored `screenshot` node, or a screenshot-on-failure) back to the run's tenant. The `{reference}` is the timeline's `screenshotRef` with its `screenshots/` prefix dropped. Authorization is by run association: the ref must appear in this run's tenant-scoped trace, so a foreign or guessed ref is a 404, indistinguishable from an unknown run."),
        new("get", "/runs/queue-stats", "getQueueStats", "The tenant's admission-queue stats.", _runs, Anonymous: false, [], null,
            [new("200", "The current queue depth and p95 queue wait.", Component: nameof(QueueStatsResponse))]),

        // Managed payloads.
        new("post", "/payloads", "draftPayload", "Draft a managed payload.", _payloads, Anonymous: false, [],
            new(nameof(SavePayloadRequest), "The inline payload to scrub, validate, and draft as revision 1."),
            [new("200", "The drafted payload (revision 1).", Component: nameof(PayloadResponse)), ValidationProblem]),
        new("get", "/payloads", "listPayloads", "List managed payloads.", _payloads, Anonymous: false, [], null,
            [new("200", "Every managed payload's summary row.", Component: nameof(PayloadListResponse))]),
        new("get", "/payloads/{id}", "getPayload", "A payload's current state.", _payloads, Anonymous: false, [Id], null,
            [new("200", "The payload's current head state.", Component: nameof(PayloadResponse)), NotFound("payload")]),
        new("post", "/payloads/{id}/revise", "revisePayload", "Append a payload revision.", _payloads, Anonymous: false, [Id],
            new(nameof(RevisePayloadRequest), "The revised payload and an optional note."),
            [new("200", "The new head revision.", Component: nameof(PayloadResponse)), NotFound("payload"), ValidationOrArchived]),
        new("post", "/payloads/{id}/rename", "renamePayload", "Rename a payload.", _payloads, Anonymous: false, [Id],
            new(nameof(RenamePayloadRequest), "The new logical name (metadata only; the head revision advances)."),
            [
                new("200", "The renamed payload's new head.", Component: nameof(PayloadResponse)),
                NotFound("payload"),
                new("400", "The payload is archived (returned as a PayloadValidationProblem). An empty name (RenamePayloadRequestValidator) is instead an RFC 7807 problem+json.", Component: nameof(PayloadValidationProblem)),
            ]),
        new("post", "/payloads/{id}/archive", "archivePayload", "Archive a payload.", _payloads, Anonymous: false, [Id], null,
            [new("200", "The archived payload.", Component: nameof(PayloadResponse)), NotFound("payload"),
             new("400", "The payload is already archived (returned as a PayloadValidationProblem).", Component: nameof(PayloadValidationProblem))]),
        new("get", "/payloads/{id}/revisions/{revision}", "getPayloadRevision", "Get one payload revision.", _payloads, Anonymous: false, [Id, Revision], null,
            [new("200", "The historical revision's script and metadata.", Component: nameof(PayloadRevisionResponse)), NotFound("payload/revision")]),
        new("get", "/payloads/{id}/diff/{from}/{to}", "getPayloadDiff", "Diff two payload revisions.", _payloads, Anonymous: false, [Id, From, To], null,
            [new("200", "Both revisions' scripts and a minimal structural diff.", Component: nameof(PayloadDiffResponse)), NotFound("payload/either revision")]),

        // Browser connect credentials (tenant self-service; the registered name becomes a payload's credentialRef).
        new("put", "/browsers/{name}", "registerBrowser", "Register or replace a browser connect credential.", _browsers, Anonymous: false, [Name],
            new(nameof(RegisterBrowserRequest), "The adapter, mode, the secret (a connect URL or api key), and optional provider options. The secret is encrypted at rest and never returned."),
            [
                new("200", "The registered browser's metadata — never the secret.", Component: nameof(BrowserSummary)),
                new("400", "The name is not a valid slug, or the body failed validation (unknown adapter/mode, empty secret, or a non-wss/https connectUrl secret) — an RFC 7807 problem+json."),
            ],
            Description: "Registers (or replaces) a browser connect credential for the authenticated tenant under {name}, which becomes the credentialRef payloads reference. The secret is encrypted at rest via ASP.NET Data Protection and is never echoed in any response, event, or log; a replace preserves createdAt. Connect resolution is tenant-scoped: a registered browser wins over the tenant-namespaced config fallback, and no tenant can resolve another's."),
        new("get", "/browsers", "listBrowsers", "List registered browsers.", _browsers, Anonymous: false, [], null,
            [new("200", "Every browser the tenant has registered — name, adapter, mode, options, and timestamps; secrets omitted.", Component: nameof(BrowserListResponse))]),
        new("delete", "/browsers/{name}", "unregisterBrowser", "Unregister a browser credential.", _browsers, Anonymous: false, [Name], null,
            [new("204", "The registration was removed."), NotFound("browser")]),
    ];

    /// <summary>The generated OpenAPI 3.1 document, as indented JSON. Built once — deterministic, like the embedded schema.</summary>
    public static string DocumentJson { get; } = Build();

    // Shared path parameters: a UUID id, string tails (a browser name slug and a screenshot ref), and the integer revision selectors.
    private static Param Id => new("id", Integer: false, "The resource id (UUID).");

    private static Param Name => new("name", Integer: false, "The registered browser name (a lowercase slug).", Uuid: false);

    private static Param Reference => new("reference", Integer: false, "The screenshot ref's {sha256}.png tail (the timeline's screenshotRef without its screenshots/ prefix).", Uuid: false);

    private static Param Revision => new("revision", Integer: true, "The 1-based revision number.");

    private static Param From => new("from", Integer: true, "The base revision number.");

    private static Param To => new("to", Integer: true, "The compared revision number.");

    // Shared responses.
    private static Response ValidationProblem =>
        new("400", "The payload failed the save-time gate (JSON Schema + semantic pass). A grossly-malformed request body is instead an RFC 7807 problem+json.", Component: nameof(PayloadValidationProblem));

    private static Response ValidationOrArchived =>
        new("400", "The payload is archived, or the script failed the save-time gate — both returned as a PayloadValidationProblem. A grossly-malformed request body is instead an RFC 7807 problem+json.", Component: nameof(PayloadValidationProblem));

    private static Response NotFound(string what) => new("404", "No such " + what + ".");

    private static JsonSerializerOptions CreateSchemaOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        ContractsJson.Configure(options); // string enums, camelCase — the shared wire conventions
        return options;
    }

    private static string Build()
    {
        var document = new JsonObject
        {
            ["openapi"] = _openApiVersion,
            ["info"] = new JsonObject
            {
                ["title"] = "Crawldad HTTP API",
                ["version"] = "1",
                ["description"] = _infoDescription,
            },
            ["tags"] = BuildTags(),
            ["paths"] = BuildPaths(),
            ["components"] = new JsonObject
            {
                ["securitySchemes"] = BuildSecuritySchemes(),
                ["schemas"] = BuildSchemas(),
            },
        };

        return document.ToJsonString(_writeOptions);
    }

    private static JsonArray BuildTags() =>
    [
        new JsonObject { ["name"] = _runs, ["description"] = "Start, poll, stream, cancel, replay, and inspect runs." },
        new JsonObject { ["name"] = _payloads, ["description"] = "Draft, revise, rename, archive, list, and diff managed payloads." },
        new JsonObject { ["name"] = _browsers, ["description"] = "Register, list, and unregister tenant browser connect credentials." },
        new JsonObject { ["name"] = _docs, ["description"] = "Anonymous, tenant-independent product artifacts (health, schema, discovery, this document)." },
    ];

    private static JsonObject BuildPaths()
    {
        var paths = new JsonObject();
        foreach (var endpoint in _endpoints)
        {
            if (paths[endpoint.Path] is not JsonObject pathItem)
            {
                pathItem = new JsonObject();
                paths[endpoint.Path] = pathItem;
            }

            pathItem[endpoint.Method] = BuildOperation(endpoint);
        }

        return paths;
    }

    private static JsonObject BuildOperation(Endpoint endpoint)
    {
        var operation = new JsonObject
        {
            ["operationId"] = endpoint.OperationId,
            ["summary"] = endpoint.Summary,
            ["tags"] = new JsonArray { endpoint.Tag },
            ["security"] = SecurityRequirement(endpoint.Anonymous),
            ["responses"] = BuildResponses(endpoint),
        };

        if (endpoint.Description is not null)
        {
            operation["description"] = endpoint.Description;
        }

        if (endpoint.PathParams.Length > 0)
        {
            operation["parameters"] = BuildParameters(endpoint.PathParams);
        }

        if (endpoint.RequestBody is not null)
        {
            operation["requestBody"] = BuildRequestBody(endpoint.RequestBody);
        }

        return operation;
    }

    private static JsonArray BuildParameters(Param[] pathParams)
    {
        var parameters = new JsonArray();
        foreach (var param in pathParams)
        {
            parameters.Add(new JsonObject
            {
                ["name"] = param.Name,
                ["in"] = "path",
                ["required"] = true,
                ["description"] = param.Description,
                ["schema"] = param switch
                {
                    { Integer: true } => new JsonObject { ["type"] = "integer" },
                    { Uuid: true } => new JsonObject { ["type"] = "string", ["format"] = "uuid" },
                    _ => new JsonObject { ["type"] = "string" },
                },
            });
        }

        return parameters;
    }

    private static JsonObject BuildRequestBody(Body body) => new()
    {
        ["required"] = true,
        ["description"] = body.Description,
        ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = Ref(body.Component) } },
    };

    private static JsonObject BuildResponses(Endpoint endpoint)
    {
        var responses = new JsonObject();
        foreach (var response in endpoint.Responses)
        {
            responses[response.Status] = BuildResponse(response);
        }

        if (!endpoint.Anonymous)
        {
            responses["401"] = new JsonObject { ["description"] = "Missing or invalid API key." };
        }

        return responses;
    }

    private static JsonObject BuildResponse(Response response)
    {
        var node = new JsonObject { ["description"] = response.Description };
        if (response.Component is not null)
        {
            node["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = Ref(response.Component) } };
        }
        else if (response.Schema is not null)
        {
            node["content"] = new JsonObject { [response.MediaType!] = new JsonObject { ["schema"] = response.Schema() } };
        }

        return node;
    }

    private static JsonArray SecurityRequirement(bool anonymous) =>
        anonymous
            ? new JsonArray()
            : new JsonArray
            {
                new JsonObject { ["bearerAuth"] = new JsonArray() },
                new JsonObject { ["apiKeyAuth"] = new JsonArray() },
            };

    private static JsonObject BuildSecuritySchemes() => new()
    {
        ["bearerAuth"] = new JsonObject
        {
            ["type"] = "http",
            ["scheme"] = "bearer",
            ["description"] = "The per-tenant API key, presented as `Authorization: Bearer <api-key>`.",
        },
        ["apiKeyAuth"] = new JsonObject
        {
            ["type"] = "apiKey",
            ["in"] = "header",
            ["name"] = CrawldadAuthentication.ApiKeyHeader,
            ["description"] = "The per-tenant API key, presented as the `" + CrawldadAuthentication.ApiKeyHeader + "` header.",
        },
    };

    private static JsonObject BuildSchemas()
    {
        var schemas = new JsonObject();
        foreach (var component in _components)
        {
            schemas[component.Name] = SchemaFor(component);
        }

        return schemas;
    }

    // Generates a component schema from its contract type via JsonSchemaExporter, then applies the two envelope-specific
    // rewrites: swap a `payload` property for the external DSL $ref, and (StartRunRequest only) clear `required` because
    // nothing is unconditionally required (payload XOR payloadId; inputs optional).
    private static JsonObject SchemaFor(Component component)
    {
        var schema = _schemaOptions.GetJsonSchemaAsNode(component.Type, _exporterOptions).AsObject();

        if (component.SwapPayload)
        {
            schema["properties"]!.AsObject()["payload"] = new JsonObject
            {
                ["$ref"] = PayloadSchemaUrl,
                ["description"] = "The Crawldad payload document; see the published DSL JSON Schema at " + PayloadSchemaUrl + " (this envelope does not restate the DSL).",
            };
        }

        if (component.PayloadOptional)
        {
            schema["required"] = new JsonArray();
        }

        return schema;
    }

    // TransformSchemaNode hook (see _exporterOptions): drops every property the wire serializer omits when null
    // ([JsonIgnore(WhenWritingNull)]) from a generated schema's `required` list, so the documented shape matches what the
    // API actually emits (a still-running run is { runId, status }); nested types are covered too (e.g. a diff entry's from/to).
    private static JsonNode RelaxRequired(JsonSchemaExporterContext context, JsonNode schema)
    {
        if (schema is JsonObject node && node["required"] is JsonArray required)
        {
            var omit = OmittedWhenNull(context.TypeInfo.Type);
            if (omit.Count > 0)
            {
                var kept = new JsonArray();
                foreach (var name in required)
                {
                    if (!omit.Contains(name!.GetValue<string>()))
                    {
                        kept.Add(name.GetValue<string>());
                    }
                }

                node["required"] = kept;
            }
        }

        return schema;
    }

    // The wire (camelCase) names of a type's properties omitted when null ([JsonIgnore(WhenWritingNull)]) — the
    // conditionally-absent fields the contracts document as present only for a given run/diff shape.
    private static HashSet<string> OmittedWhenNull(Type type)
    {
        var omit = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition == JsonIgnoreCondition.WhenWritingNull)
            {
                omit.Add(JsonNamingPolicy.CamelCase.ConvertName(property.Name));
            }
        }

        return omit;
    }

    private static JsonObject Ref(string component) => new() { ["$ref"] = "#/components/schemas/" + component };

    private static JsonObject PayloadDslSchema() => new() { ["$ref"] = PayloadSchemaUrl };

    private static JsonObject PlainTextSchema() => new() { ["type"] = "string" };

    private static JsonObject BinarySchema() => new() { ["type"] = "string", ["format"] = "binary" };

    private static JsonObject OpenApiSelfSchema() => new() { ["type"] = "object", ["description"] = "An OpenAPI 3.1 document." };

    private sealed record Endpoint(
        string Method,
        string Path,
        string OperationId,
        string Summary,
        string Tag,
        bool Anonymous,
        Param[] PathParams,
        Body? RequestBody,
        Response[] Responses,
        string? Description = null);

    private sealed record Param(string Name, bool Integer, string Description, bool Uuid = true);

    private sealed record Body(string Component, string Description);

    private sealed record Response(
        string Status,
        string Description,
        string? Component = null,
        string? MediaType = null,
        Func<JsonNode>? Schema = null);

    private sealed record Component(string Name, Type Type, bool SwapPayload = false, bool PayloadOptional = false);
}
