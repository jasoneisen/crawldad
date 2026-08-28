using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts;
using Crawldad.Contracts.Billing;
using Crawldad.Contracts.Browsers;
using Crawldad.Contracts.Drift;
using Crawldad.Contracts.Fixtures;
using Crawldad.Contracts.Payloads;
using Crawldad.Contracts.Runs;
using Crawldad.Contracts.Tenancy;
using Crawldad.Contracts.Webhooks;

namespace Crawldad.Api.Features.Docs;

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
    private const string _fixtures = "Fixtures";
    private const string _webhooks = "Webhooks";
    private const string _tenancy = "Tenancy";
    private const string _billing = "Billing";
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
        new(nameof(PayloadDriftStatus), typeof(PayloadDriftStatus)),
        new(nameof(RunResponse), typeof(RunResponse)),
        new(nameof(RunStateResponse), typeof(RunStateResponse)),
        new(nameof(RunRejection), typeof(RunRejection)),
        new(nameof(RunDriftResponse), typeof(RunDriftResponse)),
        new(nameof(RunTimelineResponse), typeof(RunTimelineResponse)),
        new(nameof(RunListResponse), typeof(RunListResponse)),
        new(nameof(QueueStatsResponse), typeof(QueueStatsResponse)),
        new(nameof(StartRunRequest), typeof(StartRunRequest), SwapPayload: true, PayloadOptional: true),
        new(nameof(ReplayRunRequest), typeof(ReplayRunRequest)),
        new(nameof(SavePayloadRequest), typeof(SavePayloadRequest), SwapPayload: true),
        new(nameof(RevisePayloadRequest), typeof(RevisePayloadRequest), SwapPayload: true),
        new(nameof(RenamePayloadRequest), typeof(RenamePayloadRequest)),
        new(nameof(RegisterBrowserRequest), typeof(RegisterBrowserRequest)),
        new(nameof(BrowserSummary), typeof(BrowserSummary)),
        new(nameof(BrowserListResponse), typeof(BrowserListResponse)),
        new(nameof(RecordFixtureRequest), typeof(RecordFixtureRequest), SwapPayload: true),
        new(nameof(RecordFixtureResponse), typeof(RecordFixtureResponse)),
        new(nameof(FixtureListResponse), typeof(FixtureListResponse)),
        new(nameof(FixtureDetailResponse), typeof(FixtureDetailResponse)),
        new(nameof(RegisterWebhookRequest), typeof(RegisterWebhookRequest)),
        new(nameof(WebhookSummary), typeof(WebhookSummary)),
        new(nameof(WebhookListResponse), typeof(WebhookListResponse)),
        new(nameof(WebhookDeliveryResponse), typeof(WebhookDeliveryResponse)),
        new(nameof(WebhookEventEnvelope), typeof(WebhookEventEnvelope)),
        new(nameof(TenantProfileResponse), typeof(TenantProfileResponse)),
        new(nameof(UsageResponse), typeof(UsageResponse)),
        new(nameof(CreateTenantKeyRequest), typeof(CreateTenantKeyRequest)),
        new(nameof(TenantApiKeyInfo), typeof(TenantApiKeyInfo)),
        new(nameof(TenantApiKeyList), typeof(TenantApiKeyList)),
        new(nameof(TenantApiKeyCreated), typeof(TenantApiKeyCreated)),
        new(nameof(BillingConfigResponse), typeof(BillingConfigResponse)),
        new(nameof(CheckoutSessionRequest), typeof(CheckoutSessionRequest)),
        new(nameof(BillingSessionResponse), typeof(BillingSessionResponse)),
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
        new("get", "/runs", "listRuns", "List the tenant's runs.", _runs, Anonymous: false,
            [
                new("status", Integer: false, "Filter by run disposition (running/queued/succeeded/failed/cancelled). An unknown value is a 400 invalid_status.", Uuid: false, Query: true),
                new("payloadId", Integer: false, "Filter by the pinned managed payload (UUID). A malformed value is a 400 invalid_payload_id.", Uuid: true, Query: true),
                new("from", Integer: false, "Inclusive lower bound on startedAt (ISO-8601). An unparseable value is ignored (unbounded), never a 400.", Uuid: false, Query: true),
                new("to", Integer: false, "Inclusive upper bound on startedAt (ISO-8601). An unparseable value is ignored (unbounded), never a 400.", Uuid: false, Query: true),
                new("page", Integer: true, "The 1-based page number (default 1). A stray value floors at 1.", Uuid: false, Query: true),
                new("size", Integer: true, "The page size (default 25, clamped to 1..100). A stray value falls back to the default.", Uuid: false, Query: true),
            ],
            null,
            [
                new("200", "A filtered, offset-paginated page of the tenant's run summaries, newest first (with the filtered total and a hasMore flag).", Component: nameof(RunListResponse)),
                new("400", "A filter value was malformed (invalid_status / invalid_payload_id).", Component: nameof(RunRejection)),
            ],
            Description: "Lists the authenticated tenant's runs as lightweight summary rows (never the result body or the full timeline), newest first by startedAt. Each row carries runId, status, startedAt, duration, region, the pinned payload name+revision (or an `inline: true` marker for an inline run), headline stats (steps/requests/selectorMisses, terminal-only), and the failure class/code when failed. Filters (status, payloadId, from/to time range) AND-combine; paging is page/size (offset). Reads the lag-tolerant RunSummary listing projection."),
        new("get", "/runs/{id}", "getRun", "Poll a run's state.", _runs, Anonymous: false, [Id],
            null,
            [new("200", "The run's current state (queued/running/terminal).", Component: nameof(RunStateResponse)), NotFound("run")]),
        new("post", "/runs/{id}/cancel", "cancelRun", "Cancel a run.", _runs, Anonymous: false, [Id], null,
            [new("202", "Cancellation was accepted; the pre-cancel state is returned.", Component: nameof(RunStateResponse)), NotFound("run")]),
        new("delete", "/runs/{id}", "eraseRun", "Erase a finished run's result and timeline.", _runs, Anonymous: false, [Id], null,
            [
                new("204", "The finished run's result, read models, and event timeline were erased."),
                NotFound("run"),
                new("409", "The run is still running or queued and cannot be erased; cancel it first (run_still_active).", Component: nameof(RunRejection)),
            ],
            Description: "On-demand right-to-erasure for a FINISHED run (issue #71): hard-deletes the run's stored result (RunProgress), its Run snapshot and timeline read models, and its event stream — the bulk result body plus the incidental PII a scrubbed timeline can still hold (a log message, a navigated URL). Tenant-scoped: an unknown, foreign, already-erased, or purely-synchronous run (which stores no progress row) is a 404 with no existence oracle, so a repeated DELETE is idempotent (204 then 404). A still-active run (running/queued) is a 409 run_still_active — cancel it first."),
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
            [new("200", "The run's ordered steps, extracts, downloads, screenshots, captures, and failure.", Component: nameof(RunTimelineResponse)), NotFound("run")]),
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
        new("get", "/payloads/{id}/drift-status", "getPayloadDriftStatus", "A payload canary's selector-drift status.", _payloads, Anonymous: false, [Id, Threshold], null,
            [new("200", "The payload's baseline/delta selector-drift assessment (state, drifted selectors, and latest-run evidence).", Component: nameof(PayloadDriftStatus)), NotFound("payload")],
            Description: "Reports whether a payload's canary has drifted (issue #47): computed on read from the payload's runs under a baseline/delta model, where the baseline is the miss floor of the earliest healthy runs and drift is a selector that matched at baseline but is newly missing in the latest completed run — never a naive selectorMisses > 0, which a legitimate multi-selector fallback trips every run. The baseline and observation count are scoped to the latest completed run's pinned revision (issue #89), so a payload edit that adds or renames selectors re-establishes the baseline for the new revision (returning to warmingUp) instead of reporting the new selectors as permanent drift. Optional `?threshold=N` tolerates N new misses before `drifted` is set (default 0). Evidence carries the latest run's capture/screenshot refs so an alert arrives with the changed page. Reads the same async RunTimeline observations the run timeline exposes; only durable (async) runs emit the selector-miss trace, so only they are observed. Distinct from GET /runs/{id}/drift, which reports one run's payload-revision drift."),

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

        // Tenant fixture record/replay (offline payload-regression testing; a recorded set replays via a config.backend "fixture" binding).
        new("post", "/fixtures/{name}/record", "recordFixture", "Record a session into a tenant fixture set.", _fixtures, Anonymous: false, [FixtureName],
            new(nameof(RecordFixtureRequest), "A run request — an inline payload plus its inputs (the live backend the session runs against, credentialRefs, parameters) — executed while banking each page state into the named set."),
            [
                new("200", "The record run's disposition: on success the recorded set summary + the run's own result; on failure (a divergence, an unrecordable operation, or any run failure) a typed failure and no set persisted — a failed record run is still HTTP 200.", Component: nameof(RecordFixtureResponse)),
                new("400", "The set name is not a valid slug — an RFC 7807 problem+json."),
            ],
            Description: "Executes the payload inline against its configured backend, banking each settled page (URL + serialized DOM) and interaction into a named, tenant-scoped fixture set that POST /runs can then replay deterministically with zero live traffic. On success the set is stored (replacing any prior set of that name). The recorded subset is linear (state-per-navigation/click, page-level CSS clicks, postback emits); a download, an in-frame click, or a non-CSS click selector fails the record run classified (fixture_unrecordable) and persists nothing."),
        new("get", "/fixtures", "listFixtures", "List recorded fixture sets.", _fixtures, Anonymous: false, [], null,
            [new("200", "Every fixture set the tenant has recorded — name, page/transition counts, byte size, source run, and timestamp; page HTML omitted.", Component: nameof(FixtureListResponse))]),
        new("get", "/fixtures/{name}", "getFixture", "Inspect a fixture set's manifest.", _fixtures, Anonymous: false, [FixtureName], null,
            [
                new("200", "The set summary plus the recorded manifest (initial state, each state's URL + content-hash, the transition graph); page HTML referenced only by hash.", Component: nameof(FixtureDetailResponse)),
                NotFound("fixture set"),
            ]),
        new("delete", "/fixtures/{name}", "deleteFixture", "Erase a fixture set.", _fixtures, Anonymous: false, [FixtureName], null,
            [new("204", "The set (manifest + page HTML) was erased."), NotFound("fixture set")]),

        // Webhook endpoints (tenant self-service; a run's terminal disposition is signed and POSTed to the registered URL).
        new("put", "/webhooks/{name}", "registerWebhook", "Register or replace a webhook endpoint.", _webhooks, Anonymous: false, [WebhookName],
            new(nameof(RegisterWebhookRequest), "The delivery URL, the HMAC signing secret, and the subscribed event types (empty = all). The secret is encrypted at rest and never returned."),
            [
                new("200", "The registered webhook's metadata — never the secret.", Component: nameof(WebhookSummary)),
                new("400", "The name is not a valid slug, or the body failed validation (a non-https/private/loopback URL, an empty or too-short secret, or an unknown event type) — an RFC 7807 problem+json."),
            ],
            Description: "Registers (or replaces) a webhook endpoint for the authenticated tenant under {name}. When a run reaches a terminal disposition (succeeded/failed/cancelled), a small ref-only JSON envelope (WebhookEventEnvelope — never result content) is POSTed to the URL, signed with an HMAC-SHA256 X-Crawldad-Signature over the raw body under the endpoint's secret plus an X-Crawldad-Timestamp (replay bound). The secret is caller-supplied, encrypted at rest (ASP.NET Data Protection), and never echoed in any response, event, or log; rotate it by re-registering. The URL must be https and must not target a loopback, link-local, or private (RFC 1918 / unique-local) address (SSRF guard). Delivery is durable and at-least-once with bounded exponential-backoff retry; a down receiver never affects run execution. Empty events subscribes to all terminal-run types."),
        new("get", "/webhooks", "listWebhooks", "List registered webhook endpoints.", _webhooks, Anonymous: false, [], null,
            [new("200", "Every webhook endpoint the tenant has registered — name, url, events, timestamps, and each endpoint's most recent delivery outcome (lastDelivery, additive; omitted when never delivered); secrets omitted.", Component: nameof(WebhookListResponse))]),
        new("get", "/webhooks/{name}/deliveries", "listWebhookDeliveries", "List an endpoint's recent deliveries.", _webhooks, Anonymous: false,
            [WebhookName, new("limit", Integer: true, "Cap the rows returned (default and maximum: the retention cap). A stray value falls back to the cap.", Uuid: false, Query: true)],
            null,
            [
                new("200", "The endpoint's recent delivery attempts, newest first (each attempt — including a retry of the same event — a distinct row; capped by the retention policy).", Component: nameof(WebhookDeliveryResponse)),
                NotFound("webhook"),
            ],
            Description: "The delivery history for one of the tenant's webhook endpoints: per-attempt rows (runId, event type, 1-based attempt number, delivered flag, HTTP status or transport-failure, measured latency, timestamp), newest first. Retained as a rolling window — the latest N attempts per endpoint (N = Crawldad:Webhooks:DeliveryHistory:MaxPerEndpoint, default 50) — so the log is bounded, not an audit ledger. Tenant-scoped: an unknown or foreign endpoint name is a 404."),
        new("delete", "/webhooks/{name}", "unregisterWebhook", "Unregister a webhook endpoint.", _webhooks, Anonymous: false, [WebhookName], null,
            [new("204", "The registration was removed."), NotFound("webhook")]),

        // Tenant self-service reads (the authenticated tenant's own profile + usage; no tenant-management surface).
        new("get", "/tenant", "getTenant", "The authenticated tenant's profile.", _tenancy, Anonymous: false, [], null,
            [new("200", "The tenant's id, display name, optional tier label, and slot/queue-depth allowances.", Component: nameof(TenantProfileResponse))],
            Description: "Returns the authenticated tenant's own profile: its stable id, its display identity, an optional pricing-tier label, and its slot (concurrent-run) and queue-depth allowances — each the per-tenant override when configured, else the global default. Read-only; resolved registry-first (a signup/management-created RegistryTenant is authoritative for its display name, tier, and slot allowance; queue depth defers to the global default, which the registry does not carry), falling back to the bound tenant options for an env-configured tenant. There is no tenant-management surface here — self-service key management lives at /tenant/keys."),
        new("get", "/usage", "getUsage", "The tenant's usage against its guardrails.", _tenancy, Anonymous: false, [], null,
            [new("200", "Slot occupancy now, queue depth + p95 wait, runs started this month, and events-per-run over a recent window vs the guardrail.", Component: nameof(UsageResponse))],
            Description: "The tenant's live capacity and consumption against its guardrails, computed on read from existing state: slot occupancy now (from the admission gate) against the slot allowance; admission-queue depth + p95 queue wait (the same reading as GET /runs/queue-stats); runs started this calendar month (UTC); and the avg/max events-per-run over a bounded recent window against the configured max-events-per-run guardrail. Pragmatic and approximate by design — a point-in-time occupancy count and a recent-window sample, not a billing ledger."),

        // Tenant self-service API keys (issue #119): the authenticated tenant managing its OWN keys with its own key —
        // no management credential, registry tenants only. The raw key is returned exactly once, on mint/rotate.
        new("get", "/tenant/keys", "listTenantKeys", "List the tenant's API keys.", _tenancy, Anonymous: false, [], null,
            [
                new("200", "The tenant's keys (prefixes + metadata only, never a raw key or its hash), newest first; the key authenticating this request is flagged current.", Component: nameof(TenantApiKeyList)),
                new("400", "This is an env-configured tenant, not a registry tenant — its keys are operator-managed, so self-service is unavailable (self_service_unavailable)."),
            ],
            Description: "Lists the authenticated tenant's own API keys as prefix-and-metadata rows (keyId, display prefix, optional label, createdAt, best-effort lastUsedAt, revokedAt, active, and current). The raw key and its hash are never returned. Exactly one active key is `current` — the key that authenticated this request — which a rotate replaces and a plain revoke refuses. Registry tenants only: an env-configured tenant is a 400 self_service_unavailable (its keys are operator config)."),
        new("post", "/tenant/keys", "mintTenantKey", "Mint a new API key.", _tenancy, Anonymous: false, [],
            new(nameof(CreateTenantKeyRequest), "An optional display label for the new key (`{}` to mint an unlabelled key)."),
            [
                new("201", "The minted key. The raw apiKey is in THIS body and nowhere else — store it now; only its hash is persisted.", Component: nameof(TenantApiKeyCreated)),
                new("400", "The label failed validation (RFC 7807 problem+json), or this is an env-configured tenant (self_service_unavailable)."),
            ],
            Description: "Mints a new `ck_<env>_<random>` API key for the authenticated tenant, with an optional label to tell keys apart. The full raw key is returned exactly once in the 201 body and is never stored (only its SHA-256 hash and a short display prefix are) or retrievable again. Registry tenants only (env tenant → 400 self_service_unavailable)."),
        new("post", "/tenant/keys/{id}/rotate", "rotateTenantKey", "Rotate an API key.", _tenancy, Anonymous: false, [Id], null,
            [
                new("201", "The replacement key (raw apiKey once); {id} is revoked in the same transaction. The replacement inherits the rotated key's label.", Component: nameof(TenantApiKeyCreated)),
                NotFound("active key for this tenant"),
                new("400", "This is an env-configured tenant, not a registry tenant (self_service_unavailable)."),
            ],
            Description: "Atomically mints a replacement key and revokes {id} in one transaction — the anti-lockout way to replace a key (including the last active key, or the key authenticating this request) with no gap. The replacement's raw key is returned once and inherits the rotated key's label. {id} not being one of the caller's active keys is a 404 (no existence oracle). Registry tenants only."),
        new("delete", "/tenant/keys/{id}", "revokeTenantKey", "Revoke an API key.", _tenancy, Anonymous: false, [Id], null,
            [
                new("204", "The key was revoked; it stops authenticating immediately (auth cache invalidated in-process, within the TTL fleet-wide)."),
                NotFound("active key for this tenant"),
                new("409", "The key is the tenant's last active key (last_active_key) or the one authenticating this request (current_key) — rotate it instead."),
                new("400", "This is an env-configured tenant, not a registry tenant (self_service_unavailable)."),
            ],
            Description: "Revokes one of the authenticated tenant's keys immediately. Refuses to revoke the tenant's LAST active key (last_active_key) or the key authenticating THIS request (current_key) — rotate those instead, since self-service auth needs a live key; both are a 409. An unknown, foreign, or already-revoked id is a 404 with no existence oracle (a repeated DELETE is 204 then 404). Registry tenants only."),

        // Billing (Stripe scaffolding, issue #119): tenant-authed config + checkout/portal session URLs, and the PUBLIC
        // signature-verified subscription webhook — the only path that changes a tenant's plan.
        new("get", "/billing/config", "getBillingConfig", "Billing state and the tier catalog.", _billing, Anonymous: false, [], null,
            [new("200", "Whether billing is configured, the tenant's current tier, and the tier catalog (moniker, display name, price label, included slots, self-serve flag, and whether it is the current tier).", Component: nameof(BillingConfigResponse))],
            Description: "Read-only billing state for the authenticated tenant: whether the payment provider is configured (false → the portal shows a friendly \"not yet available\" state and does not call the session endpoints), the tenant's current tier moniker, and the tier catalog so the portal renders the plan card without duplicating the pricing numbers. It never changes the tenant's plan. Catalog defaults come from BUSINESS_MODEL.md (Free 2 · Team $99/10 · Scale $499/50 · Enterprise custom)."),
        new("post", "/billing/checkout-session", "createBillingCheckoutSession", "Open Stripe Checkout for a tier upgrade.", _billing, Anonymous: false, [],
            new(nameof(CheckoutSessionRequest), "The target self-serve tier moniker (from the catalog)."),
            [
                new("200", "A hosted-Checkout redirect URL. The tenant's plan is NOT changed here — only a URL is minted; the plan changes only via a later verified subscription webhook.", Component: nameof(BillingSessionResponse)),
                new("400", "The tier is unknown or not a purchasable (self-serve) plan (unknown_tier)."),
                new("503", "Billing is not yet available for this deployment (billing_not_configured) — a friendly, never-500 state."),
            ],
            Description: "Mints a Stripe Checkout redirect URL for the tenant to subscribe to a self-serve tier. Returns ONLY a URL — it never changes the tenant's plan, so a tenant cannot raise its own slot allowance by calling this; the tier change lands only when Stripe later posts a verified subscription webhook to POST /billing/webhook. Free/Enterprise (non-self-serve) and unknown tiers are a 400; an unconfigured provider is a friendly 503."),
        new("post", "/billing/portal-session", "createBillingPortalSession", "Open the Stripe billing portal.", _billing, Anonymous: false, [], null,
            [
                new("200", "A hosted Billing-Portal redirect URL (manage payment method, invoices, plan).", Component: nameof(BillingSessionResponse)),
                new("503", "Billing is not yet available for this deployment (billing_not_configured)."),
            ],
            Description: "Mints a Stripe hosted Billing-Portal redirect URL for the tenant to manage its payment method, invoices, and plan. Returns only a URL; the portal never holds a provider secret. An unconfigured provider is a friendly 503."),
        new("post", "/billing/webhook", "billingWebhook", "Inbound Stripe subscription webhook (signature-verified).", _billing, Anonymous: true, [], null,
            [
                new("200", "The event was accepted — applied, or benignly dropped (a replayed event id, an unknown/env-only tenant, or a price mapping to no tier). No provider retry is wanted for a drop."),
                new("400", "The signature was invalid or the event body could not be parsed (invalid_webhook); nothing changed."),
            ],
            Description: "The PUBLIC inbound Stripe endpoint (Stripe is not a tenant, so this opts out of the tenant gate) — authenticated instead by the event signature in the Stripe-Signature header, verified BEFORE the body is parsed. On a customer.subscription.created/updated/deleted, the subscription's tenant id (from provider metadata — authoritative, never a caller claim) and price id map to a tier, and the registry tenant's Tier + SlotAllowance are updated (the new allowance takes effect immediately). This is the ONLY path that changes a tenant's plan. Anti-replay: processed event ids are de-duplicated. Registry tenants only: an env-fallback/unknown tenant, a replay, or an unmapped price are logged and dropped (200). No secret, raw body, or signature is ever logged."),
    ];

    /// <summary>The generated OpenAPI 3.1 document, as indented JSON. Built once — deterministic, like the embedded schema.</summary>
    public static string DocumentJson { get; } = Build();

    // Shared path parameters: a UUID id, string tails (a browser name slug and a screenshot ref), and the integer revision selectors.
    private static Param Id => new("id", Integer: false, "The resource id (UUID).");

    private static Param Name => new("name", Integer: false, "The registered browser name (a lowercase slug).", Uuid: false);

    private static Param FixtureName => new("name", Integer: false, "The fixture-set name (a lowercase slug).", Uuid: false);

    private static Param WebhookName => new("name", Integer: false, "The registered webhook name (a lowercase slug).", Uuid: false);

    private static Param Reference => new("reference", Integer: false, "The screenshot ref's {sha256}.png tail (the timeline's screenshotRef without its screenshots/ prefix).", Uuid: false);

    private static Param Revision => new("revision", Integer: true, "The 1-based revision number.");

    private static Param From => new("from", Integer: true, "The base revision number.");

    private static Param To => new("to", Integer: true, "The compared revision number.");

    // The one declared query parameter: the optional per-payload drift-status alert threshold.
    private static Param Threshold => new("threshold", Integer: true, "Optional per-payload drift alert threshold: the number of newly-missing selectors tolerated before `drifted` is set (default 0). A stray, non-numeric, or negative value reads as 0 — a monitor's poll never 400s on the query.", Uuid: false, Query: true);

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
        new JsonObject { ["name"] = _fixtures, ["description"] = "Record, list, inspect, and erase tenant fixture sets for offline payload-regression testing." },
        new JsonObject { ["name"] = _webhooks, ["description"] = "Register, list, and unregister tenant webhook endpoints for signed run-lifecycle delivery, and read their delivery history." },
        new JsonObject { ["name"] = _tenancy, ["description"] = "The authenticated tenant's own profile and usage against its guardrails (read-only)." },
        new JsonObject { ["name"] = _billing, ["description"] = "Billing state, the tier catalog, Stripe Checkout / Billing-Portal session URLs, and the public signature-verified subscription webhook." },
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

        if (endpoint.Parameters.Length > 0)
        {
            operation["parameters"] = BuildParameters(endpoint.Parameters);
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
                // Path params are always required; the only declared query param (drift-status ?threshold) is optional.
                ["in"] = param.Query ? "query" : "path",
                ["required"] = !param.Query,
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
        Param[] Parameters,
        Body? RequestBody,
        Response[] Responses,
        string? Description = null);

    private sealed record Param(string Name, bool Integer, string Description, bool Uuid = true, bool Query = false);

    private sealed record Body(string Component, string Description);

    private sealed record Response(
        string Status,
        string Description,
        string? Component = null,
        string? MediaType = null,
        Func<JsonNode>? Schema = null);

    private sealed record Component(string Name, Type Type, bool SwapPayload = false, bool PayloadOptional = false);
}
