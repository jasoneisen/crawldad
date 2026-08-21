# Crawldad — Engine & runtime specification

This is the normative behavioral contract of the run engine: the execution model, session configuration, error taxonomy, timeout hierarchy, resource limits, the trace/event model, checkpoint/resume semantics, and the backend adapter interface. It is the "how a run behaves" companion to three other references and does not duplicate them: the **HTTP surface, wire codes, and exact limit defaults** are in [`API.md`](API.md); the **payload language** (nodes, selectors, expressions) is in [`PAYLOAD_SPEC.md`](PAYLOAD_SPEC.md) and the [JSON Schema](../schema/crawldad-1.schema.json); the **component topology and infrastructure** are in [`ARCHITECTURE.md`](ARCHITECTURE.md); the **security boundary** in [`THREAT_MODEL.md`](THREAT_MODEL.md).

---

## Execution model

The interpreter executes a payload's `steps` in order against one browser session. State lives in **one flat run scope**:

- `input.*` is read-only (the run's supplied inputs); `vars` seed it once, in order; `set`/`push` create and mutate entries; values are string, number, boolean, null, array, object (map), and opaque **locator/frame handles**.
- Handles are usable in reads but never serialized into output — a handle reaching `result` is a terminal `handle_in_result` failure.
- **Loop variables** (`for.var`, `forEach.as`/`index`) are visible inside the loop body and shadow outer names for that scope; they leave scope on loop exit. All other `set`/`push` persist across steps (the engine accumulates result-so-far across a whole operation).
- There are no expression-local bindings and no closures — intermediate values become `set` vars, which keeps the expression language first-order.
- **Determinism.** An expression is a pure function of (inputs, vars, current DOM reads): given the same page it always yields the same value. This is what makes runs replayable and drift detectable.

## Session configuration

`config` (authored in the payload; see [`PAYLOAD_SPEC.md`](PAYLOAD_SPEC.md)) is applied by the interpreter **on top of** whatever context the backend hands back:

- `backend` (an expression selecting the adapter + credential binding), `defaultTimeoutMs` (default 120 000), `launch`/`context` passthrough (e.g. `--disable-web-security`, `bypassCsp`) where the backend allows.
- **Route policy** — request interception applied per page: `blockHosts` and `blockResourceTypes` abort matching requests; `cacheResourceTypes`/`cacheUrlSuffixes` fulfil from a cross-run, per-region asset cache keyed by URL (public web assets only — no tenant data is cached, so cache sharing does not cross the tenant boundary); `throttle.minIntervalMs` serializes one non-cached request per tick globally.
- **Retry** — an operation-level policy wrapping the whole program (the **post-connect** steps on an already-established session): `maxAttempts`, `delayMs` (the base delay), `backoff` (`constant` default | `linear` = `delayMs · n` | `exponential` = `delayMs · 2ⁿ⁻¹`), an optional `maxDelayMs` ceiling and `jitter` (full jitter, spreading each wait across `[0, delay]`), `retryOn` (only the retryable conditions), and `onPageCrashed` (`reopenPage` default — close+reopen the crashed page on the same context and re-run | `fail` — no reopen, the crash fails the attempt and is retried only when `pageCrashed` is in `retryOn`). Backoff waits respect the run deadline (a wait that would outlive it ends the run terminally). It never re-establishes the connection.
- **Connect retry** — `connectRetry { maxAttempts, delayMs }`, the separate knob for the **connect boundary** (which `retry` never reaches). Off by default (single-shot connect). Present ⇒ a **transient** connect fault (a refused/reset socket, DNS, a WebSocket handshake failure, a 5xx or a 429/408 throttle from a hosted session API) is retried, re-resolving the `credentialRef` each attempt so a connector's mid-window re-registration is picked up; an **auth-shaped** fault (a rejected key, a client-error 4xx other than 429/408, an absent credential) fails fast. Bounded (`maxAttempts` ≤ 10, `delayMs` ≤ 60 s) and deadline-respecting; exhaustion stays terminal `backend_unavailable`.

## Error taxonomy

The single most important operational distinction is **retryable vs terminal**.

- **Retryable:** `timeout` and `pageCrashed` — and *only* these. They are retried per `config.retry`. On `pageCrashed` the engine closes and reopens a page on the same context and **rebinds it into the interpreter session**, then re-runs the operation from the top. A retryable condition that exhausts `config.retry` becomes a terminal failure of class `retryable-exhausted`.
- **Terminal (never retried):** anything a `guard`/`fail` raises with `class:"terminal"`, and by default any non-retryable engine or expression error (type errors, out-of-range index, division by zero, unresolved secret, a server-limit breach, an unknown backend adapter, and so on). A connect fault (`backend_unavailable`) is terminal too — `config.connectRetry` gives a **transient** one bounded attempts before it lands here, but it is never a `retryable-exhausted` page condition.
- **Warnings are not failures.** `log level:"warning"` emits a `LogEmitted` event and continues.
- **A failed run is not a failed request.** A run that starts and faults is `HTTP 200` (sync) or reaches a terminal state pollable after a `202` — it carries a typed `failure { class, code, message, atStep }`. Requests that never start a run (bad input, an over-depth queue) are `4xx`/`429`.

The complete, code-derived list of engine/expression failure codes is the table in [`API.md` §12.3](API.md); author-defined `guard`/`fail` codes are the author's own vocabulary, not a fixed enum.

## Timeout hierarchy

Three distinct timers, most-specific-wins where they overlap:

1. **Per-action timeout** — `config.defaultTimeoutMs` (120 s) < a per-node `timeoutMs` override < an action-intrinsic long timeout (e.g. a `download` node's own long default). Enforced by Playwright.
2. **Run wall-clock deadline** — `config.deadlineMs` (default **30 min** when omitted), enforced by the orchestrator, *not* Playwright: the saga schedules a durable `RunDeadline` message that asks the still-running run's in-process control to stop with a terminal `run_deadline_exceeded`. It is deliberately generous — not in the 40–60 s range competitors cap at.
3. **Synchronous-response window** — `Crawldad:Limits:SyncUpgradeThresholdMs` (default **120 s** — a different 120 s from `defaultTimeoutMs`). It bounds only how long a default `POST /runs` holds the caller's HTTP connection before the run is auto-upgraded to async; it does not bound execution. Its rationale is the Azure ingress ceiling — see [`ARCHITECTURE.md`](ARCHITECTURE.md#part-b--infrastructure-reference-architecture).

## Resource limits

Five server-side caps a payload can **never** raise (they are deployment config under `Crawldad:Limits`, not payload fields). The interpreter enforces the first four mid-run; the admission gate enforces the fifth:

| Cap | Default | On breach |
|---|---|---|
| `MaxStepsPerRun` | 100 000 | terminal `max_steps_exceeded` |
| `MaxDownloadedBytesPerRun` | 1 GiB (1 073 741 824) | terminal `max_download_bytes_exceeded` |
| `MaxEventsPerRun` | 100 000 | terminal `max_events_exceeded` |
| `ExpressionStepBudget` | 1 000 000 | terminal `expression_budget_exceeded` |
| `MaxConcurrentRunsPerTenant` | 32 | run **queues** (not rejected) |

Two admission-queue knobs accompany the concurrency cap: `MaxQueueDepthPerTenant` (default 1 000; past it, `429 queue_depth_exceeded`) and `MaxQueueWaitMs` (default 0 = wait indefinitely; when set, a queued run that outwaits it terminates `queue_wait_exceeded`). The concurrency and queue-depth caps take **per-tenant overrides** — this is the mechanism pricing tiers use to set per-tenant slot allowances (the tier quantities themselves live in [`BUSINESS_MODEL.md`](BUSINESS_MODEL.md); the un-overridden platform defaults are the values above). Defaults are deliberately generous so legitimate runs never trip them. The full knob table is [`API.md` §12.4](API.md).

## Run states & admission

A run is `queued` → `running` → a terminal state (`succeeded` / `failed` / `cancelled`). `POST /runs` resolves any pinned payload first (a bad pin is a `400`, never queued), then makes one admission decision against the concurrent-run cap; under the cap it runs immediately (inline-sync or on the durable saga), at the cap it enqueues durably and is promoted FIFO when a slot frees. The three run shapes (sync `200`, async/upgraded `202`, queued `202`) and the poll/cancel bodies are specified in [`API.md` §4–§7](API.md); the flow is diagrammed in [`ARCHITECTURE.md` §A.5](ARCHITECTURE.md#a5-run-lifecycle).

Cancellation is cooperative and honored **between steps**: the browser session is torn down cleanly (you cannot yank mid-step without leaking a backend session), and the run reaches `cancelled` with a `partial`. A queued run cancels straight to `cancelled` without ever consuming a slot.

## The trace & observability model

Observability is not bolted on — it falls out of modelling the run as an event-sourced aggregate: **the run's event stream is the trace.** Granularity is **semantic** (one event per meaningful action), not per micro-op, which keeps stream volume bounded.

- **Trace events** — appended to the run's stream as it executes (inline for a synchronous run; by the background executor's observer for an async or upgraded one): `RunStarted`, `StepStarted`, `Navigated`, `Clicked`, `Filled`, `Waited`, `Extracted`, `Downloaded`, `Screenshotted`, `StepFailed`, `LogEmitted`, `RunAttemptFailed`, `RunSessionOpened`, the checkpoint/resume/cancel markers (`RunCheckpointReached`, `RunResumed`, `RunCancellationRequested`, `RunCancelled`), the queue markers (`RunQueued`, `RunDequeued`), and the terminal `RunSucceeded`/`RunFailed`.
- **Metadata-only discipline.** Events store references, never bulk data: `Extracted` carries a shape ref (never the value), `Downloaded` a blob ref, `Screenshotted`/`StepFailed` a screenshot ref, `Filled` a `secret:<refName>` (never the typed secret). Bulk extracted data lives in the executor-owned `RunProgress` document (deletable); bytes live in blob storage. This is a security invariant, detailed in [`THREAT_MODEL.md`](THREAT_MODEL.md).
- **Read models.** `RunStarted` **pins the exact payload revision + script hash**, so editing a payload never mutates historical runs and drift is detectable. The `RunTimeline` async projection is the lag-tolerant cross-run view (ordered steps, durations, refs, region).
- **Streaming (SSE).** `GET /runs/{id}/events` backfills from the durable stream then tails live to the terminal frame; `id` is the stream version, so `Last-Event-ID` resumes exactly with no loss or duplication. Authoritative live state is read from the run's own stream (read-your-writes), not the lagging projection.
- **Replay & drift.** Replay re-executes a historical run's **pinned revision** (the script is guaranteed identical; inputs are resupplied because input *values* are never persisted). Drift compares a run's pinned revision/hash against the payload head. Productizing drift into scheduled monitoring/alerting is tracked in **issue #47**; the nightly canary that supplies the cross-run drift signal is tracked in **issue #7**.

## Checkpoints & resumability

Because a live browser session is external stateful IO (cookies, auth, JS heap) that **cannot** be rebuilt by replaying events, resume is **checkpoint-based, explicit, and not event-replay**. The event stream reconstructs the *record*, never the *execution*.

Reaching a `checkpoint` node durably records (overwriting the run's single stored checkpoint): a stable `name` + a monotonic `sequence`; the **enclosing top-level step index** (the only position recorded — resume re-enters exactly there); the **cursor** (the value of the checkpoint's `cursor` expression — the author's declaration of where the browser must be to continue); and a **snapshot of every declared variable** *except* `input` (re-supplied at resume) and opaque handles (they point into a dead page and are re-derived).

A resumed run is a brand-new interpreter over a **fresh** session that: **restores** the variable snapshot and binds the cursor; **skips** every top-level step before the checkpoint's; runs the checkpoint's `resume` sub-program **once** to re-navigate the fresh session to the cursor; then re-enters the loop, **re-deriving** transient handles against the fresh page and **re-resolving** any `fill.secret` from the vault (so no secret is ever persisted in a checkpoint). Because messaging is durable, a process death mid-run redelivers on restart and resumes from the last checkpoint.

The **authoring rules** for where a checkpoint may legally appear and what breaks resumability (idempotent iteration work, cursor sufficiency, the head-of-loop placement) are validated at save time and specified in [`PAYLOAD_SPEC.md`](PAYLOAD_SPEC.md#checkpoints). General (non-checkpoint) resumability, and a fully-dynamic per-item stop/continue path richer than a declarative predicate, are out of scope for v1.

## Backend adapter interface

The service never owns browsers. `IBrowserBackend.ConnectAsync(binding, policy, ct)` establishes a live connection to a customer-supplied backend and returns an `IBrowserSession`, from which the interpreter opens `IPageHandle`s. The seam is deliberately Playwright-for-.NET-shaped so real adapters map 1:1; adapters are **asymmetric** by design (Browserbase is CDP-only via `connectOverCDP`; Browserless prefers the native `chromium.connect`), and the interface abstracts *connect*, not *protocol*.

- **Per-run, per-tenant.** Each run connects a fresh session, disposed at run end (disposal tears the remote session down cleanly — the cancellation/cleanup path relies on this to avoid leaking a backend session). Sessions are never shared across runs or tenants.
- **Credentials by reference.** The binding carries a `credentialRef`, never the secret; the adapter resolves it **only at connect time** and the value lives solely in interpreter memory for the session. See [`THREAT_MODEL.md`](THREAT_MODEL.md).
- **Shipped adapters:** `fake` (record/replay over captured DOM; deterministic, no Chromium), `local` (credential-free local Chromium), `browserless` (native connect), `browserbase` (session-create → CDP). An unknown adapter is a terminal `unknown_backend_adapter`.

## Downloads & content identity

A `download` node runs a `trigger` and streams the provoked download from the backend to a sink. The bytes are buffered in memory only to compute their content identity; they **never enter an event, an aggregate, or the response** — only the resulting refs do. The engine natively computes the content identity: `sha256` of the stream, `contentId` = first 16 bytes of the SHA-256 as a GUID, and an idempotent upload (an already-present `contentId` short-circuits to `stored:true`, skipping the re-upload). Deduplication is by **content hash**, not an author-supplied key. The sink is resolved by the target's kind through the download-sink registry; shipped sinks are the config-selected blob provider, with customer-credentialed storage targets on the roadmap.

## Result shaping

The payload **declares** the output shape: `result` is an object-literal expression built from accumulated vars (mirroring a hand-written `return new Record {…}`), so the caller receives identical nested output rather than flat step-results to reassemble. The success/failure/cancelled/running response bodies are specified in [`API.md` §3–§5](API.md).

## Roadmap boundaries

Designed but not built: **webhooks** (an `ISideEffect` sender for run lifecycle notifications) — the design sketch lives on **issue #12**; a **`@puppeteer/replay` importer** (a one-way lift of a linear Chrome DevTools recording into a payload) — **issue #13**. Neither is required by the current workload, which uses inputs, storage targets, and SSE instead.
