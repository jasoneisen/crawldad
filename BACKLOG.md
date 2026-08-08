# Crawldad — Backlog (canonical work list)

> The post-MVP work list, created 2026-08-07 after all five phases of `CRAWLDAD_PLAN.md` shipped
> (`8369556`). **Live tracking happens in GitHub Issues** — each ticket is a GitHub issue on
> `jasoneisen/crawldad` with a tier label; this file is the stable index + full ticket text.
> Tickets are referenced by issue number (`#N`). The original `CD-N` ticket names (retired
> 2026-08-08) map 1:1 to issues #1–#16; the correspondence broke once PRs #17–#19 consumed the
> shared issue/PR number space, so newer tickets (#20+) have no CD alias. `CD-N` mentions in older
> commits, PR bodies, SECURITY.md, and the design docs are historical aliases for issue #N.
> Section refs (§) point to `CRAWLDAD_DESIGN.md`; product/pricing refs (§Pv, §P, §1–§6) point to
> `docs/PRODUCT.md` (the product-architecture report, added 2026-08-08).
>
> Explicitly **not** tickets (out of scope by the brief, re-affirmed at plan close): proxy layer,
> browser fleet, billing, marketing site, self-serve authoring UI, `evaluate`/arbitrary JS (never —
> the safety thesis), non-CDP exotic backends, multi-region interpreter placement, `emulationOs`.

## Tier 1 — GA blockers (before any real customer data)

### [#1](https://github.com/jasoneisen/crawldad/issues/1) Auth/authz + tenant isolation
**Status:** shipped 2026-08-08 (PR #17, reviewer-approved). Per-tenant API keys, Marten conjoined
multi-tenancy, Wolverine envelope tenant propagation, tenant-partitioned storage seams. **Ref:** §12.
Every endpoint today — runs, payloads, SSE, cancel, replay — is unauthenticated (deliberate MVP
deferral; the reference's no-auth must not be copied). Build the tenant boundary: authenticated
tenant, actor/`By` from the principal (never the request body), per-tenant Marten
`DatabaseSchemaName`, per-run backend sessions never shared across tenants, per-tenant blob storage.
**Done when:** no anonymous mutating or reading route; a cross-tenant access test proves isolation
(runs, payloads, timelines, SSE, blobs); actor stamped on payload mutation events from the principal.

### [#2](https://github.com/jasoneisen/crawldad/issues/2) Real blob storage adapters + retention/lifecycle
**Status:** shipped 2026-08-08 (PR #25, reviewer-approved after a two-round loop — round 1 caught a
retention-janitor exception that would stop the host on transient storage errors and a missing
shared tenant/key guard on the Azure adapter, both fixed with tests). Filesystem adapter is the
durable default (100%-covered on real disk); Azure Blob validated against Azurite (hermetic CI);
provider knob `Crawldad:Storage:Provider`; retention TTLs enforced by a scheduled janitor;
crypto-shredding documented designed-not-built. TTL is measured from first write under
content-addressing — revisit before any blob-retrieval endpoint. **Ref:** §9.3, §12, §13.
Production wiring registers only `FakeDownloadSink` (keyed `"fake"`) and `InMemoryScreenshotStore`.
Ship at least one durable adapter for each (S3 / Azure Blob / filesystem) behind the existing keyed
registry + `IScreenshotStore` seams, plus the §12/§13 policies: deletable PII blobs,
retention/lifecycle rules for screenshots and downloads, optional crypto-shredding (per-run key,
discard to erase).
**Done when:** a real adapter passes the existing download/content-hash/idempotency and screenshot
test matrices against real storage (or an emulator) with zero live third-party traffic in CI;
retention policy documented in SECURITY.md.

### [#3](https://github.com/jasoneisen/crawldad/issues/3) Remaining §12 resource limits
**Status:** shipped 2026-08-08 (PR #18, reviewer-approved). All five limits landed; the concurrency
cap's reject-at-cap was superseded by #16's queue the same night. **Ref:** §12 "Resource limits".
Built: run wall-clock deadline (`config.deadlineMs`), per-node `timeoutMs` hierarchy,
`loop.maxIterations`, regex size/time guards. Not built: **max steps**, **max total downloaded
bytes**, **max event count**, **max concurrent runs per tenant**, expression evaluation step budget.
Exceeding a limit is a terminal failure with a clear code.
**Done when:** each limit has a config knob, a terminal failure code, and a test driving it over the
limit; concurrent-runs cap is per-tenant (depends on #1 for tenancy identity — a global cap is an
acceptable first slice). Note: under the slot-based pricing model (`docs/PRODUCT.md` §Pv.3) the
per-tenant concurrent-runs cap is the billing-critical limit — its at-limit semantics are #16.

### [#15](https://github.com/jasoneisen/crawldad/issues/15) Cap synchronous runs at 120 s with auto-upgrade to async
**Status:** shipped 2026-08-08 (PR #23, reviewer-approved after a two-round loop — round 1 caught a
pinned/replay `ObjectDisposedException` across the 202 handoff and a supervisor slot leak, both
fixed and regression-tested). Sub-cap sync goldens unchanged; window is
`Crawldad:Limits:SyncUpgradeThresholdMs` (default 120 s). **Ref:** `docs/PRODUCT.md` §1.1/§2.2, §8.4.
Every viable Azure ingress kills a long sync request first (Front Door 240 s, Container Apps Envoy
240 s, App Service ~230 s fixed) — the 30-minute synchronous `POST /runs` is architecturally dead
behind any of them. Cap sync execution at 120 s wall clock; at the cap the run is auto-upgraded,
not failed: the engine continues as if `"async": true` and the caller gets
`202 {runId, status:"running"}` at the moment of upgrade, then uses the existing async surface.
**Done when:** sub-120 s sync responses are byte-identical to today (goldens unchanged); a run
crossing the cap returns 202 and completes with the same terminal result async would have
produced; the threshold is a config knob; deployment docs state the required ingress timeout.

### [#4](https://github.com/jasoneisen/crawldad/issues/4) Browserbase `connectUrl` live-primary re-check
**Status:** shipped 2026-08-08 (PR #22). Live-primary re-verified both remote backends against real
accounts (one session each, released). **Finding — the Browserbase shape drifted:** the live
`connectUrl` is `wss://connect.<region>.browserbase.com/?signingKey=<JWT>` (a per-session JWT — it does
**not** embed the account apiKey, which travels only in the `X-BB-API-Key` header), so a leak now
compromises one session, not the account. Browserless confirmed:
`wss://production-<region>.browserless.io/chromium/playwright?token=<token>` (token accepted; no-token
→ 401). No scrub-rule change needed (`signingKey`/`token` already known params); `SECURITY.md` updated
docs-based → live-primary for both providers, with the scrub re-confirmation. **Ref:** §3.5/§9,
SECURITY.md.

## Tier 2 — approved engineering follow-ups

### [#5](https://github.com/jasoneisen/crawldad/issues/5) Complete `RunExecutorSaga` at terminal via the `RunDeadline` handler
**Status:** approved 2026-08-07. **Ref:** §14.2, SECURITY.md "Durable state at rest".
A finished run's script + inputs linger indefinitely in `mt_doc_runexecutorsaga` (the saga is never
`MarkCompleted()` — the finisher isn't a saga handler). Fix: `Handle(RunDeadline)` checks the run's
disposition via `RunProgress` and `MarkCompleted()` when terminal — the already-scheduled deadline
message doubles as the janitor, bounding retention to `deadlineMs` with zero new messages and no
race (saga handlers are serialized). Alternative if prompt cleanup is wanted: the executor publishes
`RunFinished(runId)`.
**Done when:** tests cover late-timeout-to-completed-saga, crash between terminal-commit and
cleanup, and the resume invariant; the leak-test retention assertion flips from "lingers" to "gone
after deadline"; SECURITY.md table updated.
Note (added 2026-08-08, from the #16 review): fold in `StartRun`/saga-start idempotency — the
#16 reviewer confirmed a latent saga-starter race under redelivered `StartRun`/`PromoteQueued`
load (masked, not fixed, by #16's conditional enqueue-nudge; promotion itself is idempotent).

### [#6](https://github.com/jasoneisen/crawldad/issues/6) BYO key vault + `secretRef` form-fill credentials
**Status:** approved 2026-08-07; design recorded in SECURITY.md "Designed, not built". **Ref:** §12.
Pluggable `ISecretStore` adapters behind the keyed-registry pattern (`config`, then
`azure-keyvault` / `aws-secretsmanager` / `hashicorp-vault` / customer HTTP endpoint); a `secretRef`
payload input type whose value is the reference string only; a dedicated secret-valued action field
(`fill: { sel, secret: … }`) so secrets never enter the `${…}` expression space; resolution at
action time into `IRunSecretScope` (scrubbed everywhere, never at rest, re-resolved on resume).
Unblocks the first login-gated target.
**Done when:** the SECURITY.md design section's four commitments each have a test, including a leak
sweep proving the resolved secret is absent from events, projections, SSE, durable envelopes, and
the saga while the ref is present.

### [#16](https://github.com/jasoneisen/crawldad/issues/16) Slot admission queue: queue-don't-reject at the concurrent-run cap
**Status:** shipped 2026-08-08 (PR #19, reviewer-approved after a three-round loop — a
double-terminal slot-leak race was found, fixed via advisory-lock claim, and re-verified). Wire
codes as shipped: `queue_depth_exceeded` / `queue_wait_exceeded`; `concurrent_runs_exceeded` was
removed with the queue. **Ref:** `docs/PRODUCT.md` §Pv.3/§Pv.5; depends on #1 (tenant
scope) and #3 (`maxConcurrentRunsPerTenant` — this ticket defines that cap's at-limit semantics).
Slot-based pricing makes the per-tenant concurrency cap revenue enforcement at `StartRun`
admission, so its rejection behavior is product-critical: queue, don't 429. Durable FIFO on the
existing Wolverine machinery — at the cap, run N+1 is accepted (`202`, `status:"queued"`, position
in GET + SSE) and starts when a slot frees; `429` (`queue_depth_exceeded`) only past the per-tier
max queue depth; cancel-while-queued dequeues without consuming a slot; `deadlineMs` starts at
execution with a separate max-queue-wait knob; p95 queue wait per tenant emitted (the upgrade
signal the pricing model depends on).
**Done when:** tests cover FIFO fill→queue→auto-start, depth 429, cancel-while-queued,
deadline-at-execution, and crash/restart with a non-empty queue (queued runs survive, start in
order); queue position visible via GET + SSE; p95 queue wait observable.

### [#20](https://github.com/jasoneisen/crawldad/issues/20) Docs MVP: API.md + published payload schema + curated examples + llms.txt
**Status:** approved 2026-08-08. **Ref:** `schema/crawldad-1.schema.json`, acceptance fixtures
under `tests/Crawldad.Tests/Fixtures/Payloads/`, SECURITY.md.
MVP documentation for users **and LLMs**, as plain self-contained artifacts rather than a docs
site: (1) a single hand-written `docs/API.md` — auth, the run lifecycle in all three shapes (sync
result / async 202 / queued 202 + promotion), SSE, cancel, replay, payload CRUD/revisions/diff,
queue stats, and a complete wire-code table with the config knob behind each limit; (2) the payload
schema served at a stable URL with a `description` on every node/field (the DSL reference —
editor-autocomplete- and LLM-ready); (3) 4–6 commented examples derived from the tested acceptance
fixtures; (4) `llms.txt` served at the API root and committed. Required even at MVP: a
**docs-drift test** — every JSON payload block in API.md and every example file validates against
the schema in the normal `dotnet test` run.
**Done when:** API.md covers every routable endpoint and every shipped wire code; schema served
with full descriptions; ≥4 validated examples; llms.txt served + committed; the drift test fails
the build on any invalid example. Deferred: docs site, cookbook, SDK generation, MCP server.

### [#21](https://github.com/jasoneisen/crawldad/issues/21) OpenAPI spec for the HTTP surface
**Status:** approved 2026-08-08. **Ref:** #20 (this is the split-out envelope-spec ticket).
`GET /openapi.json` generated from the Wolverine.HTTP endpoints: every routable endpoint,
request/response contracts from `src/Crawldad.Contracts`, both auth schemes, and the full status
shapes (sync result, 202 queued/running, 429 `queue_depth_exceeded`). Envelope only — payload
bodies reference the served schema (#20) rather than duplicating the DSL.
**Done when:** `/openapi.json` served (anonymous-or-not decided deliberately, like `/health`); the
spec covers every routable endpoint with accurate auth + response shapes; a sync test asserts no
endpoint is missing from the spec. Deferred: SDK generation, hosted reference UI.

### [#26](https://github.com/jasoneisen/crawldad/issues/26) Drain `SyncRunSupervisor` tasks on host shutdown
**Status:** shipped 2026-08-08 (PR #27, reviewer-approved; flake death independently verified
10/10 stress iterations). Supervisor is an IHostedService draining tracked tails in StopAsync
(bounded 15 s) before provider disposal; shutdown skips the promotion nudge, durably re-triggered
by the recovery scan. Non-blocking residuals for a future hardening pass (candidates for #5):
a request upgrading DURING the drain can adopt after the drain snapshot (degrades to the durable
hard-crash recovery path); drain-timeout path leaves `Task.WhenAll` faults unobserved (benign
under .NET defaults). **Ref:** #15 (PR #23) `SyncRunSupervisor`.
`Adopt` fire-and-forgets `DriveToTerminalAsync` with no tie to the host lifetime; the
post-persistence tail (finalize via singleton store → promote via a fresh scope + `IMessageBus`)
can hit disposed services on shutdown → intermittent `ObjectDisposedException` teardown flakes in
`SyncCapTests`/`ConcurrentRunsCapTests`, and in production a graceful shutdown can cut a promotion
mid-flight (recovered durably on restart, but noisy). Fix direction: register supervised tasks
with the host lifetime and drain on shutdown (or guard the tail against a disposing provider);
fixtures drain in-flight supervised runs before dispose.
**Done when:** a shutdown-with-inflight-upgraded-run test proves clean drain; the teardown flake
cascades disappear from repeated full-suite runs.

## Tier 3 — operational toggles

### [#7](https://github.com/jasoneisen/crawldad/issues/7) Activate the nightly live canary
**Status:** open (workflow shipped in P4; ran once manually 2026-08-07, passed in 23 s).
Configure repo `vars` for `.github/workflows/canary.yml` (canary link uses the live three-part
`capID1/capID2/capID3` format, not the fixtures' single `capID=`).
**Done when:** a scheduled run has passed from CI and drift alerting lands somewhere someone reads.

## Tier 4 — carried engine gaps (deliberate since P3; none block LJCMG)

### [#8](https://github.com/jasoneisen/crawldad/issues/8) Explicit `screenshot` node
**Status:** open; cheap — `IPageHandle.ScreenshotAsync` + `IScreenshotStore` exist (P5
screenshot-on-failure). Add the payload-authored action: schema, semantic walker, interpreter,
trace event with blob ref, fake + real coverage.

### [#9](https://github.com/jasoneisen/crawldad/issues/9) Structured `Sel` role/text/xpath
**Status:** open. **Ref:** §5.2. `Sel` carries css + title today; the acceptance suite needs no
more. Add role/text/xpath variants with parity coverage when a workload needs them (XPath is
carried in the design, not gated on).

### [#10](https://github.com/jasoneisen/crawldad/issues/10) `loop.for.step` as a typed number
**Status:** open (minor). `step` is an Expr string today; accept a JSON number, keep the Expr form
for computed steps.

### [#11](https://github.com/jasoneisen/crawldad/issues/11) Honor `download.idempotencyKey`
**Status:** open. Accepted-but-ignored today (content-hash identity already provides the
`stored:true` short-circuit). Decide: implement key-based dedup or remove the field from the schema
— either way, stop silently ignoring it.

## Tier 5 — designed, awaiting a workload (post-MVP by the brief)

### [#12](https://github.com/jasoneisen/crawldad/issues/12) Webhooks (`SendWebhook` / `ISideEffect`)
**Status:** designed (§14 note), not built. The LJCMG host uses inputs + storage targets. Build
when a workload needs push notification of run completion/drift.

### [#13](https://github.com/jasoneisen/crawldad/issues/13) `@puppeteer/replay` importer
**Status:** designed (§16), not built. Post-MVP authoring on-ramp: importer translating recordings
into payloads.

### [#14](https://github.com/jasoneisen/crawldad/issues/14) General checkpoint resumability beyond the two LJCMG loops
**Status:** partially exists — the `checkpoint` node is generic (any top-level loop can carry one);
what's missing is the documented authoring guidance + validation for arbitrary payloads and the
fully-dynamic streaming stop path (§11 note). Revisit with the first non-LJCMG long-running
workload.
