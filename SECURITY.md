# Crawldad — Security notes

> Companion to `CRAWLDAD_DESIGN.md` §12 (Security). This file records the credential-handling boundary
> as built in Phase 4 (WP1 credential-by-reference + connect scrubbing; WP3 the scrubbing boundary at
> every sink and its leak tests) and extended in Phase 5 (the background executor opens the run's secret
> scope; the SSE / `RunTimeline` / screenshot sinks are now real and leak-tested; the durable at-rest
> surfaces are documented below). Terse and factual by design.

## Credentials by reference

Payloads and events never carry a raw secret. A backend binding carries a **credential reference**
(`credentialRef`) — an id into an `ISecretStore` (`Infrastructure/Security/ISecretStore.cs`). The
secret is resolved **only at connect time**, by the adapter, and lives solely in the interpreter's
memory for the session. `apiKey` mode stores a vault ref; `connectUrl` mode treats the whole URL as a
one-time secret (still scrubbed — see below). The store's default backing is configuration
(`Secrets:{ref}`), so any provider (user-secrets, env, a mounted vault file) supplies secrets without
a code change.

## The Browserbase `connectUrl` is NOT safe to leak (re-verified)

Verified shape: `wss://connect.browserbase.com?apiKey=bb_live_…&sessionId=ses_…`. The default CDP
`connectUrl` **embeds the account `apiKey` in its query string**. It is ephemeral in *session
lifetime* but **not credential-isolated**: a holder can read the key out and mint more sessions.
`connectUrl` mode therefore reduces credential *storage* (no long-lived key in our vault) but not
*blast radius on leak*. It is scrubbed **exactly like an `apiKey`**; the session-create response's
separate `signingKey` is likewise secret. Do not describe `connectUrl` as "safe to leak" anywhere.
The Browserless `token` is account-scoped (a leaked token drains the account's unit balance) — same
rules.

## The scrubbing primitive

`Infrastructure/Security/CredentialScrubber.cs` is the single scrubber. Two rules, idempotent, and a
no-op on ordinary text:

1. **Exact secret** — any credential resolved for the *current run* (registered by the connecting
   adapter, below) is replaced wherever it appears, catching free-form text no param rule would
   recognise (a `log` message echoing an input, an exception, a scraped page that echoes the value).
2. **Known credential params** — the values of `apiKey`, `token`, `signingKey` (case-insensitive) are
   redacted anywhere they appear as `name=value`, which covers a `ws://`/`wss://` connect URL's query
   and a JSON-embedded connect URL. Surrounding text (scheme, host, path, the non-secret `sessionId`)
   is preserved so the redaction stays diagnostic.

Redaction marker: `[redacted]`. The word "token" without `=value` is never touched (so the acceptance
goldens are byte-identical), and a value below a short length floor is not exact-scrubbed (so a
pathologically short "secret" can't mangle common substrings — the param rule still redacts it).

A third input feeds the exact-match rule: the configured **tenant API keys** (CD-1) are registered as **always-on**
secrets (process-wide, not per-run), so a leaked key is redacted anywhere it might surface. See "Authentication & tenant
isolation" below.

## The per-run secret registry (exact-match lifetime)

`Infrastructure/Security/IRunSecretScope.cs` (`AmbientRunSecretScope`). A scope is opened at the run's
**execution** entry — inline in `POST /runs` for a synchronous run, and by the **background executor**
(`RunExecutor.DriveAsync`, WP2) for an async run, where it spans the whole execution including retries
and a checkpoint resume (a fresh `ConnectAsync` re-registers the resolved credential naturally, so a
resumed run rebuilds its scrub set with no persisted secret). The connecting adapter **registers the
resolved secret** (a token, an apiKey, and the apiKey-embedding `connectUrl`) into it; every sink's
scrubber consults it. The current scope is ambient (`AsyncLocal`), so it flows down the run's async call
chain, concurrent runs never see one another's secrets, and **no secret outlives its run**: disposing the
scope at run end clears the registered secrets, and the scrubber itself holds only a reference to the
seam — never a secret. A short length floor guards the exact-match rule.

## What is scrubbed, and where (one chokepoint per sink)

| Sink | Chokepoint | Note |
|---|---|---|
| **Connect exceptions** | the adapters (`Real/Browserless…`, `Real/Browserbase…`) | any provider fault becomes a hand-written, secret-free `BrowserConnectException` (WP1); the raw `wss://…?token=` URL is never wrapped in. |
| **Marten events** | `Features/Runs/RunEventScrubber`, at both append paths: the `POST /runs` append (`StartRunEndpoint`, sync) and the executor's own-session append (`RunExecutor.ExecutorObserver`, async, WP2) | Every event is scrubbed before append: `RunStarted` (payload name + input key names), `LogEmitted`, `RunFailed`, and the WP3 step trace (`Navigated`/`Clicked`/`Extracted`/`Downloaded`/`StepFailed`/`RunSessionOpened`). Input *values* are never persisted; `Extracted` carries a shape ref, `Downloaded` a blob ref, `StepFailed` a screenshot ref — metadata only (§12). |
| **Payload events** | `Features/Payloads/PayloadScript.Scrub` at save (`DraftPayloadEndpoint`/`RevisePayloadEndpoint`) | a payload's script *is* the stored artifact (§14.1), so it is scrubbed **before** the `PayloadDrafted`/`PayloadRevised` event is built and hashed — the immutable event store can never receive a credential. A well-formed payload (credentials are by reference) scrubs to a no-op, so the stored bytes stay executable and golden-identical. |
| **Projections** | — (by construction) | the `Run` snapshot, the `RunTimeline` read model (§13), and the `PayloadSummary` list derive purely from already-scrubbed events, so they inherit the guarantee; they add no un-scrubbed field (`PayloadSummary` carries no script body at all). |
| **Logs** | `Infrastructure/Security/ScrubbingLoggerFactory` (decorates `ILoggerFactory` in `HostConfiguration`) | every category's logger — application, Wolverine, Marten, ASP.NET — scrubs its rendered message before any sink writes it. Central, not per-call-site. |
| **HTTP response** | `StartRunEndpoint` (sync), `GetRunEndpoint`/`RunProgress` (async poll) | the failure message (same scrubbed value as the event) and the shaped `result`/`partial` (`ScrubJson`) — caller data a scraped page could echo. Credential-free results are byte-identical to their golden. |
| **SSE frames** | `Features/Runs/RunEventsEndpoint` (`RunEventFrames.Format`) | each frame's `data` is the already-scrubbed persisted event re-serialised; the **durable stream** (not an in-memory buffer) is the frame source, so a frame can carry nothing the event did not. |
| **`RunTimeline` + `GET /runs/{id}/timeline`** | — (by construction) | folds the already-scrubbed step trace; surfaces extracted-value refs, blob refs, region, and the screenshot ref — never a raw value or the image. |
| **Screenshot blobs** | `Infrastructure/Storage/IScreenshotStore` (content-addressed ref) | the failing page's image lives in a **deletable** blob store; the immutable `StepFailed` event holds only the `screenshots/{sha256}.png` ref (§12 PII). |

## Durable state at rest (Phase 5) — inputs by reference, not extracted PII

The background executor (§11/§14.2) leaves run state in three durable stores beyond the immutable trace. None is a
credential sink — credentials are **by reference** (`credentialRef`), so only the ref travels — but this is what sits
where, and for how long, stated honestly:

| At-rest store | Holds | Credential-scrubbed? | Retention |
|---|---|---|---|
| **`RunProgress`** (`mt_doc_runprogress`) | the pollable `result`/`partial`/`failure` body and the durable checkpoint (cursor + accumulated-var snapshot) | **yes** — result/partial/checkpoint are scrubbed before store; the body is bulk data deliberately kept out of the immutable trace (§12), and any *extracted PII* it holds lives here (deletable), never in an event | deletable, mutable, executor-owned document |
| **`RunExecutorSaga`** (`mt_doc_runexecutorsaga`) | the run's `script` + `inputs` — the resume source (re-run + re-connect after a restart) | no (inputs are caller config; credentials among them are refs) | **lingers indefinitely** — the saga is never marked complete, so a finished run's inputs+script remain at rest until the schema is dropped |
| **Wolverine durable envelopes** (`wolverine_incoming/outgoing_envelopes`, `wolverine_dead_letters`) | `StartRun` carries `script` + `inputs`; `ExecuteRun` / `RunDeadline` carry only the run id | no | handled `StartRun`/`ExecuteRun` are retained until Wolverine's `keep_until` sweep; the scheduled `RunDeadline` sits until its wall-clock delay fires; a dead-lettered `ExecuteRun` persists until cleared |

The invariant that holds at rest: **inputs travel by reference**, so `credentialRef` — not the resolved secret — is what
these stores contain. `CredentialLeakTests.An_async_by_reference_run_keeps_the_resolved_secret_out_of_the_saga_and_wolverine_envelopes`
drives a by-reference run and asserts the resolved secret appears in the `RunExecutorSaga` document and every Wolverine
envelope body **nowhere**, while the `credentialRef` *is* present — proving the boundary at rest, not just at the sinks.
The honest corollary (see the final "Designed, not built" note): a raw secret passed as a plain **input value** would sit
un-scrubbed in the `inputs` carried by `StartRun` and the saga (and re-read on resume) — which is exactly why form-fill
credentials must become a `secretRef` type, never a plain input.

## Leak test (the phase gate)

`tests/Crawldad.Tests/Integration/CredentialLeakTests.cs` runs a payload through `POST /runs` with a
distinctive sentinel as the resolved credential, driving the real WP1 `browserless` adapter against a
loopback Playwright `run-server` (token in the ws URL) and the `browserbase` adapter against a
loopback CDP endpoint + session-create stub — **zero live third-party traffic**. The payload
adversarially interpolates the sentinel into a `log` message and the shaped `result`. It then asserts
the sentinel appears in **no** event (all events dumped, plus raw `data::text` from the Marten
tables), projection/document row, captured log line (framework categories included), response body,
**SSE frame, `RunTimeline` row, or screenshot key/byte** (the WP3 sinks). Variants cover the failure
path (a connect failed with the sentinel in a bad-port `connectUrl`), the **async/checkpoint** path
(the durable `RunProgress` checkpoint cursor + var snapshot swept), a **failing async run that captures
a real screenshot**, and the **durable at-rest surfaces** above (the `RunExecutorSaga` document +
Wolverine envelope bodies swept for a by-reference credential). Separately, the metadata-only trace
discipline for non-credential **bulk PII** is re-asserted by
`RunObservabilityTests.The_trace_stream_holds_no_raw_extracted_or_input_value_only_metadata_refs`: a
known extracted value reaches the result body but appears in **no** trace event, SSE frame, or timeline
row (the scrubber never touches it — its absence is the discipline itself, not a redaction).

## Authentication & tenant isolation (CD-1)

Built in CD-1 (issue #1). Every route requires an authenticated tenant — **no anonymous mutating or reading route
survives** — the one exception being the `/health` liveness probe (it exposes no tenant data and must answer an
unauthenticated load balancer). This replaces the reference's deliberate no-auth MVP posture (§12: "no auth in the
reference is deliberate and must not be copied").

**Mechanism — per-tenant API keys.** Machine-to-machine auth for a hosted API product: a per-tenant key presented as
`Authorization: Bearer <key>` or `X-Api-Key: <key>`, validated by a custom ASP.NET `AuthenticationHandler` against a
config-bound tenant directory (`Crawldad:Tenants` → `{id, apiKey, actor}`). No ASP.NET Identity / OIDC ceremony — a real
IdP is a later ticket. Keys are **hash-compared**: `TenantRegistry` keeps only a SHA-256 of each configured key and
compares with `CryptographicOperations.FixedTimeEquals` (no timing signal, no plaintext key retained in the long-lived
auth path). A valid key yields a principal carrying the tenant id + actor as claims; `RequireAuthorizeOnAll` turns a
missing/invalid key into a `401`. An **endpoint-enumeration test** discovers the live route table and asserts every route
(bar `/health`) rejects an unauthenticated request, so a route added later without auth fails the test by default rather
than escaping a hand-maintained list.

**Actor from the principal, never the body.** Payload mutation events (`PayloadDrafted/Revised/Renamed/Archived`) carry a
`by` actor stamped from the authenticated tenant's configured identity. The request contracts carry no `by` field at all —
there is nothing to spoof (a test asserts the event's `by` equals the tenant's actor).

**Tenancy model — Marten conjoined multi-tenancy (the pragmatic first slice).** `AllDocumentsAreMultiTenanted()` +
`Events.TenancyStyle = Conjoined`: one shared schema, every event stream and document row qualified by a `tenant_id`, every
session opened for a tenant. A cross-tenant read returns nothing → **`404` (not `403`)**, chosen so a tenant cannot even
confirm another's resource exists. This isolates the event-sourced Payload/Run aggregates, their projections
(`PayloadSummary`, `RunTimeline`), the SSE backfill stream, drift, and replay; the `RunProgress`/`RunExecutorSaga`
documents are tenanted the same way. Listings are tenant-filtered by construction.

The tenant flows into **every** session, including the ones outside the HTTP request scope (the parts the ticket flags as
the risk):
- **HTTP requests** — Wolverine's HTTP tenant detection (`opts.TenantId.IsClaimTypeNamed`) reads the tenant claim and opens
  the injected Marten session for that tenant, and stamps the same tenant onto any message the endpoint publishes.
- **The background run executor** (`RunExecutor`, which owns its own sessions, §14.2) — the tenant travels on the
  `StartRun`/`ExecuteRun` message envelope (inherited from the tenanted HTTP publish and cascaded through the saga); the
  executor reads `Envelope.TenantId` and opens every session under it (trace appends, checkpoints, `RunProgress`). A run
  with no tenant **fails closed** (never touches the default partition).
- **Checkpoint resume & the SSE backfill** — both open tenant-scoped sessions, so a resumed run stays in its partition and a
  cross-tenant SSE stream reads nothing (→ 404, indistinguishable from an unknown run).
- **Startup recovery** (`RunRecoveryService`) — conjoined tenancy scopes each query to its tenant, so recovery fans out over
  every configured tenant, finds each one's interrupted runs, and re-publishes `ExecuteRun` tagged with that tenant.
- **The async projection daemon** — by construction projects each event under its own `tenant_id`.

**Upgrade path.** Conjoined tenancy is one database with no per-tenant provisioning. The design doc's "one
`DatabaseSchemaName` per tenant" is a stronger isolation posture available later via Marten's schema-per-tenant or
database-per-tenant models, at the cost of per-tenant migration/provisioning. The seam is the same tenant id — moving to it
changes only the Marten store wiring, not the endpoints or the executor.

**Per-run backend sessions & per-tenant storage.** Backend sessions are per-run by construction (each run `ConnectAsync`es a
fresh session, disposed at run end) — never shared across runs or tenants; the isolation test asserts distinct session
instances per run. The download/screenshot storage seams (`IDownloadSink`, `IScreenshotStore`) now carry the run's tenant so
a real CD-2 adapter partitions storage per tenant (the tenant in the key/path structure) and the content-addressed
idempotency probe is tenant-scoped — one tenant can neither read, overwrite, nor probe another's blob by content id; the
fakes prove the partitioning. The content-addressed refs stay tenant-independent, so the wire result and immutable trace are
byte-identical.

## Not in scope here

PII crypto-shredding and resource limits are tracked in `CRAWLDAD_DESIGN.md` §12 and are not part of the WP3 scrubbing
boundary. Per-tenant concurrency caps (CD-3), the slot queue (CD-16), real storage adapters (CD-2), and the BYO vault
(CD-6) build on the CD-1 tenant seam but are their own tickets.

## Designed, not built: BYO key vault + `secretRef` form-fill credentials (post-P5)

The first login-gated target needs a credential typed into a **form** (`fill`), not a backend
binding — and today the only by-reference machinery is `credentialRef` → `ISecretStore` at connect.
Passing a password as a plain run input is not acceptable: checkpoint resume (§11) keeps inputs
durably at rest — in the `StartRun` envelope and the `RunExecutorSaga` document ("Durable state at
rest" above) — invisible to the scrubber (which redacts registered run secrets and credential-shaped
params, not arbitrary values). The designed answer, agreed 2026-08-07:

- **BYO vault.** `ISecretStore` becomes a keyed-adapter registry (the same pattern as backends and
  storage targets): `config` (today's `Secrets:{ref}` backing), then `azure-keyvault` /
  `aws-secretsmanager` / `hashicorp-vault` / a customer HTTP endpoint. The customer's vault is the
  sole custodian; a vault binding is declared like a `storageTarget` (`{kind, name, options}`).
- **`secretRef` input type.** The input's value is the *reference string only*. Durable messages,
  events, projections, and payloads carry the ref, never the secret — a Crawldad operator compromise
  leaks references, not credentials.
- **Secrets stay out of the expression value space.** A dedicated secret-valued action field (e.g.
  `fill: { sel, secret: "input.loginPassword" }`), never `${…}` interpolation — otherwise a secret
  could be `push`ed, `log`ged, or shaped into a `result`, and no after-the-fact scrubber should be
  asked to catch that.
- **Resolve at action time; register in the run scope.** Resolution happens in interpreter memory at
  the `fill`, registering the value into `IRunSecretScope` exactly as connecting adapters do — every
  sink scrubs it for free, it never outlives the run, and checkpoint resume re-resolves it naturally
  (the same path backend credentials take at `ConnectAsync`).
