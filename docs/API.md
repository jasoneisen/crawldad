# Crawldad API reference

Crawldad runs one JSON **payload** — a small, JSON-Schema'd browser-automation DSL — against a headless
browser and returns a caller-shaped result. This is the single consumer reference: read it top-to-bottom
(with the [payload schema](../schema/crawldad-1.schema.json) for exhaustive field detail) and you can author
a payload and drive a run. It reflects the shipped surface as of this revision; it is derived from the
contracts and endpoints, not from older design notes.

- **The payload is the program.** Structure is JSON (composable, diffable); only leaf expressions are strings
  in a small, pure expression language. The full grammar is the JSON Schema — served live at
  `GET /schema/crawldad-1.schema.json`, where every node and field carries a `description`.
- **The HTTP surface is small.** Runs (`/runs`, plus SSE / cancel / replay / drift / timeline / queue-stats)
  and managed payloads (`/payloads`, plus revisions / diff). Everything is JSON; enums serialize camelCase.
- **A failed *run* is not a failed *request*.** A run that starts and faults is `HTTP 200` with a typed
  `failure`. `4xx`/`429` are reserved for requests that never start a run (bad input, an over-depth queue).

## Contents

1. [Authentication](#1-authentication)
2. [The payload, in brief](#2-the-payload-in-brief)
3. [Running a payload — `POST /runs`](#3-running-a-payload--post-runs)
4. [The three run shapes: sync, async, queued](#4-the-three-run-shapes-sync-async-queued)
5. [Polling — `GET /runs/{id}`](#5-polling--get-runsid)
6. [Streaming trace (SSE) — `GET /runs/{id}/events`](#6-streaming-trace-sse--get-runsidevents)
7. [Cancel — `POST /runs/{id}/cancel`](#7-cancel--post-runsidcancel)
8. [Replay — `POST /runs/{id}/replay`](#8-replay--post-runsidreplay)
9. [Drift & timeline — `GET /runs/{id}/drift`, `/timeline`](#9-drift--timeline)
10. [Queue stats — `GET /runs/queue-stats`](#10-queue-stats--get-runsqueue-stats)
11. [Managed payloads — `/payloads`](#11-managed-payloads--payloads)
12. [Wire codes](#12-wire-codes)
13. [Reading validation errors](#13-reading-validation-errors)
14. [Served docs & health](#14-served-docs--health)
15. [Examples](#15-examples)
16. [Endpoint quick reference](#16-endpoint-quick-reference)

---

## 1. Authentication

Every route except the anonymous ones ([§14](#14-served-docs--health)) requires a per-tenant API key,
presented **either** way:

```http
Authorization: Bearer <api-key>
```
```http
X-Api-Key: <api-key>
```

`Authorization: Bearer` is checked first, then `X-Api-Key`. A missing, empty, or unknown key is
`401 Unauthorized` — the key is never echoed back or logged. The authenticated key resolves a **tenant**;
every Marten session, run, and payload is automatically scoped to it, so one tenant can never read or drive
another's runs. The actor stamped on payload-mutation events comes from the key, never the request body.

---

## 2. The payload, in brief

A payload is one JSON document. Only the leaf **expressions** (`Expr`) and **templates** (`Tmpl`) are strings;
everything else is JSON structure.

```json
{
  "crawldad": "1",
  "name": "example.title",
  "inputs": {
    "backend": { "type": "backend", "required": true }
  },
  "config": { "backend": "input.backend" },
  "steps": [
    { "goto": { "url": "https://example.com" } },
    { "waitForLoadState": { "state": "load" } },
    { "set": { "var": "title", "value": "trim(coalesce(text('h1'), ''))" } }
  ],
  "result": "{ title: title }"
}
```

Top-level shape (all required except `inputs`/`vars`):

| Field | Kind | Meaning |
|---|---|---|
| `crawldad` | `"1"` | Dialect version — v1 is frozen. |
| `name` | string | Logical identity of the payload. |
| `inputs` | object | Typed run parameters: `{ "<name>": { "type": …, "required"?, "default"? } }`. |
| `config` | object | Session config; only `config.backend` is required. |
| `vars` | object | Initial variable bindings, evaluated once in order (optional). |
| `steps` | array | The ordered program (the nodes). |
| `result` | `Expr` | Final expression that shapes the response body. |

**Field kinds.** `Expr` — a pure, total expression string (string literals inside are quoted: `"'owner'"`).
`Tmpl` — a string with `${<Expr>}` interpolation (`"…/page/${n}"`); no `${}` means a literal. `Sel` — a
selector ([§2.3](#23-selectors-sel)). `Node` — one step: an object with **exactly one** recognised head key.

### 2.1 Inputs

Input `type` is one of `string`, `number`, `boolean`, `date`, `array`, `object`, `backend`, `storageTarget`,
`secretRef`. A `backend` input takes the wire shape below; a `secretRef` is a **vault reference only** (never
the secret — see [§2.5](#25-secrets-secretref)); a `storageTarget` names a download sink.

Inputs are supplied at run time under `inputs` (see [§3](#3-running-a-payload--post-runs)). A `backend`
input's value:

```jsonc
// the record/replay fake backend (used by the examples/tests)
{ "adapter": "fake", "options": { "fixture": "caphome-search" } }

// a real adapter (local | browserless | browserbase); the credential is a vault reference, never the secret
{ "adapter": "browserless", "options": { /* provider passthrough */ }, "credentialRef": "vault:my-token" }
```

### 2.2 Nodes (the action set)

Each step is `{ "<head>": { … } }`. The schema is the exhaustive reference; the vocabulary:

- **Navigation / waits:** `goto`, `waitForLoadState`, `waitForRequest` (run a `trigger`, await the request it
  provokes), `waitFor` (await a selector state), `frame` (bind a frame handle), `addStyleTag`.
- **Interaction:** `click`, `fill` (`value` **or** `secret`), `clear`, `screenshot` (full-page capture),
  `download` (stream a provoked download to a `storageTarget`).
- **Data / control:** `locate` (bind a lazy locator handle), `set`, `push`, `log`, `guard`/`fail` (typed
  abort), `if`, `switch`, `loop` (`for` **or** `while`), `forEach`, `break`, `continue`, `checkpoint`.

Two rules the engine enforces at save time: **every `loop`/`forEach` carries a mandatory `maxIterations`
cap**, and `comment` nodes are no-ops (documentation only).

### 2.3 Selectors (`Sel`)

A `Sel` is a **string** (CSS by default; `"xpath=…"` for XPath) or a structured object rooted at **exactly
one** of `css` / `xpath` / `text` / `role` / `title` / `base`:

```jsonc
{ "css": "table tr" }
{ "role": "button", "name": "Search" }   // getByRole + accessible name (role is a fixed ARIA vocabulary)
{ "text": "Sign out" }                    // getByText (innermost, normalised substring)
{ "xpath": "//table[@id='results']//tr" }
// refinements: nth, first, filter.hasTextRegex, base (child locator), in (bound frame)
{ "base": "rowVar", "css": "td:nth-child(2)" }
```

`base` may pair with a relative `css` (the sole two-root combination) and `name` accompanies `role` only.
Any other multi-root combination is rejected at save with `ambiguous_selector`.

### 2.4 Expressions

The expression sublanguage is CEL-shaped: pure, total, side-effect-free, non-Turing-complete — **no**
user functions, recursion, assignment, iteration, or IO. Operators `+ - * / %`, comparisons, `&& || !`,
ternary `?:`, member `.`, index `[]`; references `input.*`, declared vars, loop `var`/`index`, and
`pageUrl()`. `+` concatenates when either side is a string. String/DOM builtins **null-propagate** (a null
primary yields null, like C# `?.`); a **required conversion** or an **out-of-range index** is a *terminal*
failure, never null — so `coalesce(x, default)` / `?:` are how you supply a default. The enumerated builtins
(string / collection / URL / DOM read-only) are the whole surface — the schema and design doc list them.

### 2.5 Secrets (`secretRef`)

A `secretRef` input carries a **reference** into a vault, never the secret. It is consumable **only** by
`fill.secret`, which resolves it from the vault at fill time and types it straight into the field — it never
enters an expression, a variable, or the trace (which records only `secret:<name>`). Referencing a
`secretRef` anywhere in an expression is rejected at save with `secret_ref_in_expression`.

```jsonc
"inputs": { "password": { "type": "secretRef", "required": true } },
// …
{ "fill": { "selector": { "role": "textbox", "name": "Password" }, "secret": "input.password" } }
```

---

## 3. Running a payload — `POST /runs`

Execute exactly one payload — supplied **inline** or by **pinned managed payload** — and get the shaped
result or a typed failure. Body (`StartRunRequest`):

```jsonc
{
  "payload": { /* inline crawldad document */ },   // XOR "payloadId"
  "payloadId": "…", "revision": 3,                 // pin a managed payload (revision defaults to head)
  "inputs": { "backend": { "adapter": "fake", "options": { "fixture": "caphome-search" } } },
  "async": false                                   // default; see §4
}
```

Provide **exactly one** of `payload` (an inline object) or `payloadId` (a pinned managed payload); supplying
both or neither is `400 ProblemDetails`. `inputs` must be a JSON object when present. Credentials are always
by-reference (a `credentialRef` / `secretRef`), so `inputs` carries no raw secret.

Successful synchronous run (`200`, `RunResponse`):

```jsonc
{
  "runId": "3f…",
  "status": "succeeded",
  "result": { "title": "Example Domain" },   // the payload's `result`, evaluated (object key order preserved)
  "stats": { "durationMs": 812, "steps": 37, "requests": 2, "cacheHits": 0, "downloads": 0 }
}
```

Failed run — still `200` (the request succeeded; the run faulted):

```jsonc
{
  "runId": "3f…",
  "status": "failed",
  "failure": {
    "class": "terminal",                     // terminal | retryable-exhausted
    "code": "record_not_accessible",         // a stable slug — see §12
    "message": "Record not accessible (redirected to /Login.aspx)",
    "atStep": { "index": 2, "kind": "guard" }
  },
  "stats": { "durationMs": 410, "steps": 3, "requests": 1, "cacheHits": 0, "downloads": 0 }
}
```

`stats`: `durationMs` (wall clock), `steps` (nodes executed — loop bodies re-count per iteration), `requests`
(navigations + matched `waitForRequest`s), `cacheHits` (route cache; 0 until it lands), `downloads`
(completed `download` nodes).

---

## 4. The three run shapes: sync, async, queued

The mode is chosen by the `async` flag in the body **and** the tenant's concurrency state — there is no
`Prefer` header. Pin resolution runs first (so a bad `payloadId` is always a `400`, never queued), then a
single admission decision:

**(a) Synchronous (default, `async:false`).** Under the tenant's concurrent-run cap the interpreter runs
inline and returns the terminal `200 RunResponse` above. This run writes no background progress row, so
`GET /runs/{id}` returns `404` for it — the `POST` response is the whole story.

**(b) Sync → async auto-upgrade (the 120 s window).** A default run that is *still executing* when the
synchronous window elapses is **auto-upgraded, not failed**: it keeps executing on the durable surface, and
the call returns `202` with a `Location: /runs/{runId}`:

```jsonc
{ "runId": "…", "status": "running" }
```

You then follow the async surface ([§5](#5-polling--get-runsid)/[§6](#6-streaming-trace-sse--get-runsidevents)).
The window is `Crawldad:Limits:SyncUpgradeThresholdMs` (default **120 000 ms**), deliberately under every Azure
ingress ceiling so the connection is always answered — result or upgrade — before ingress can kill it. A run
finishing inside the window returns the synchronous body unchanged. One deliberate consequence: because the
run executes on its own cancellation source, a **client disconnect no longer cancels an in-flight run** — it
is bounded by the sync window and then the run wall-clock deadline (`config.deadlineMs`, default 30 min), not
by the connection. This 120 s window is distinct from the per-action `config.defaultTimeoutMs` (also 120 s by
coincidence) and from the run wall-clock deadline.

**(c) Explicit async (`async:true`).** The run starts on the durable executor saga immediately and returns
`202 { runId, status:"running" }` with `Location`. Identical polling/SSE surface as (b).

**(d) Queued (at the concurrent-run cap).** When the tenant is at its concurrent-run cap
(`Crawldad:Limits:MaxConcurrentRunsPerTenant`, default **32**; per-tenant override), the run is **not
rejected** — it is accepted, persisted `queued`, and returns `202` with a 1-based `position`:

```jsonc
{ "runId": "…", "status": "queued", "position": 4 }
```

A queued **sync** run is upgraded to this async surface. It holds no slot; when a slot frees, the tenant's
**oldest** queued run is promoted automatically (FIFO) to `running` and the executor kicks in. Poll
`GET /runs/{id}` to watch `queued → running → terminal`; once promoted, the response carries `queueWaitMs`
(the enqueue→start latency). The **only** `429` from admission is `queue_depth_exceeded` — the queue itself is
full (`Crawldad:Limits:MaxQueueDepthPerTenant`, default **1000**; per-tenant override).

`RunStateResponse` (the `202`/poll/cancel body) carries exactly the field for the current status:
`position` while `queued`, `result` once `succeeded`, `failure` once `failed`, `partial` once `cancelled`;
`stats` accompanies any terminal status, and `queueWaitMs` appears once a queued run has started.

---

## 5. Polling — `GET /runs/{id}`

The state of a background (async / upgraded / queued) run, from the executor-owned progress model:

```jsonc
// queued
{ "runId": "…", "status": "queued", "position": 2 }
// running
{ "runId": "…", "status": "running" }
// terminal (succeeded shown; failed carries `failure`, cancelled carries `partial`)
{ "runId": "…", "status": "succeeded", "result": { /* … */ },
  "stats": { "durationMs": 91234, "steps": 512, "requests": 84, "cacheHits": 0, "downloads": 12 },
  "queueWaitMs": 4120 }
```

`404` when there is no such background run — including a purely synchronous run, which never writes a progress
row (its result was returned inline). Read-your-writes: this reflects the latest committed state, not the
lagging cross-run projection.

---

## 6. Streaming trace (SSE) — `GET /runs/{id}/events`

A run's trace as Server-Sent Events (`Content-Type: text/event-stream`). On connect it **backfills from the
durable event stream**, then follows the live tail until a terminal event, then closes. Frames:

```text
id: 12
event: StepStarted
data: {"index":4,"kind":"click"}

id: 13
event: Navigated
data: {"url":"https://example.gov/portal/search"}
```

- `id` is the event's **stream version**. Reconnect with `Last-Event-ID: <version>` (or `?lastEventId=<version>`)
  to resume **exactly** where you left off — the durable stream is authoritative, so no frame is lost or
  duplicated across a disconnect.
- `event` is the trace event's type name (`StepStarted`, `Navigated`, `Clicked`, `Waited`, `Extracted`,
  `Downloaded`, `Screenshotted`, `Filled`, `LogEmitted`, `StepFailed`, …). The stream closes on `RunSucceeded`,
  `RunFailed`, or `RunCancelled`.
- `data` is the already-**scrubbed** event JSON — no credential ever streams. An unknown (or cross-tenant) run
  is `404` (checked before any SSE headers).

---

## 7. Cancel — `POST /runs/{id}/cancel`

Cancel a background run (no body). A **running** run gets a cooperative cancel: the request appends a durable
`RunCancellationRequested` and raises the stop signal; the executor honours it **between steps**, tears the
browser session down cleanly, and the run reaches `cancelled` with a `partial` result (poll `GET /runs/{id}`).
A **queued** run is dequeued straight to `cancelled` without ever consuming a slot (nothing is promoted).

Returns `202` with the **pre-cancel** state snapshot (and `Location: /runs/{id}`); `404` for an unknown run.
Cancelling an already-finished run is a no-op.

---

## 8. Replay — `POST /runs/{id}/replay`

Re-execute a historical run's **pinned payload revision** — the exact revision + script hash the original
recorded, so the script is guaranteed identical (the basis of the drift story). Body (`ReplayRunRequest`):

```jsonc
{ "inputs": { /* resupplied — see below */ }, "async": false }
```

Two deliberate v1 rules: **inputs are resupplied** by the caller (input *values* are never persisted, so a
replay cannot recover them; the pinned revision + hash guarantee the same *script*), and **only a pinned run
is replayable** — an inline run's script was never stored as a revision, so it is rejected with
`inline_not_replayable` (`400`). Pin resolution, the archived-payload guard, and sync/async dispatch are
shared verbatim with `POST /runs`, so the response is the same shape (`200 RunResponse` / `202 RunStateResponse`);
an unknown run is `404`.

---

## 9. Drift & timeline

### `GET /runs/{id}/drift`
A run's pinned revision vs the payload's current head. Drift = the pinned revision is no longer head.

```jsonc
{ "runId": "…", "payloadId": "…", "pinnedRevision": 3, "pinnedScriptHash": "…",
  "headRevision": 5, "headScriptHash": "…", "drifted": true }
```

Equal hashes under a revision mismatch mean the head moved by a metadata-only change (rename/archive). An
inline run never drifts (`payloadId`/head fields `null`, `drifted:false`). `404` for an unknown run.

### `GET /runs/{id}/timeline`
The observability read model (the lag-tolerant cross-run view): ordered steps with per-step durations, the
**redacted** input key *names*, extracted-value shape refs (never values), download and screenshot blob refs
(never bytes), the terminal failure + its screenshot ref, the pinned revision + script hash, and the backend
region. Everything derives from already-scrubbed trace events, so no raw credential or bulk PII surfaces.
`404` for an unknown run.

---

## 10. Queue stats — `GET /runs/queue-stats`

The tenant's admission-queue snapshot, computed on read (no metrics library):

```jsonc
{ "queued": 3, "sampled": 214, "p95QueueWaitMs": 4120 }
```

`queued` is the current depth; `p95QueueWaitMs` is the 95th-percentile enqueue→start latency across the
tenant's promoted runs (`sampled` = the sample size). Sustained wait is the "add slots" signal.

---

## 11. Managed payloads — `/payloads`

A managed payload is an event-sourced aggregate whose stream **is** its version history. Every save runs the
same scrub-then-validate gate (JSON Schema + semantic pass), so a persisted revision is always executable and
credential-free.

### `POST /payloads` — draft
Body `{ "payload": { /* crawldad document */ } }`. Drafts revision 1. Response (`200`, `PayloadResponse`):

```jsonc
{ "payloadId": "…", "name": "example.title", "revision": 1, "scriptHash": "…", "status": "active" }
```

A schema/semantic failure is `400` with the full structured error list ([§13](#13-reading-validation-errors)):

```jsonc
{ "errors": [ { "path": "/steps/6/loop", "code": "missing_max_iterations", "message": "…" } ] }
```

### `POST /payloads/{id}/revise`
Body `{ "payload": { … }, "note"?: "…" }`. Appends a new script revision (same gate). `200` new-head
`PayloadResponse`; `404` unknown; `400` when archived (`payload_archived`) or invalid.

### `POST /payloads/{id}/rename`
Body `{ "name": "new.name" }`. Metadata-only revision — advances the head revision, script hash unchanged.
`200`; `404` unknown; `400` when archived or the name is empty.

### `POST /payloads/{id}/archive`
No body. Terminal lifecycle change (advances the head revision; script hash unchanged); blocks further
revise/rename/archive and new **pinned** runs. `200` (status `archived`); `404` unknown; `400` if already
archived.

### `GET /payloads` — list
`{ "payloads": [ { "payloadId", "name", "revision", "scriptHash", "status", "draftedAt", "updatedAt" } ] }`
(metadata only, no script body).

### `GET /payloads/{id}` — state
The current `PayloadResponse`; `404` when unknown.

### `GET /payloads/{id}/revisions/{revision}` — one revision (with script)
`{ "payloadId", "revision", "scriptHash", "script": { /* the stored, scrubbed payload */ } }`; `404` when the
payload/revision is unknown. (This is where the executable script body is fetched — there is no separate
`/script` route.)

### `GET /payloads/{id}/diff/{from}/{to}` — revision diff
Both revisions' scripts plus a minimal structural diff (deepest-changed JSON pointers):

```jsonc
{ "payloadId": "…", "fromRevision": 1, "toRevision": 2,
  "fromScript": { /* … */ }, "toScript": { /* … */ },
  "changes": [ { "path": "/steps/3/click/selector", "kind": "changed", "from": "#a", "to": "#b" } ] }
```

`kind` is `added` / `removed` / `changed` (`from` absent when added, `to` absent when removed). `404` when the
payload or either revision is unknown.

---

## 12. Wire codes

Three surfaces carry stable slugs: **request rejections** (no run starts — `4xx`/`429`), **save-time
validation** (`400` on `/payloads`), and **run failures** (`failure.code`, `HTTP 200` sync or terminal after a
`202`). Enum values below are exact.

### 12.1 Request rejections — no run starts

| Code | HTTP | Where | Meaning |
|---|---|---|---|
| `unknown_payload` | 400 | `POST /runs`, `/replay` | pinned payload id does not exist |
| `unknown_revision` | 400 | `POST /runs`, `/replay` | pinned revision does not exist |
| `payload_archived` | 400 | `POST /runs`, `/replay` | the pinned payload is archived (cannot run) |
| `inline_not_replayable` | 400 | `POST /runs/{id}/replay` | the run executed an inline payload (no stored revision to replay) |
| `queue_depth_exceeded` | 429 | `POST /runs`, `/replay` | the tenant's admission queue is at its depth cap |

There is **no** `concurrent_runs_exceeded` — at the concurrent-run cap a run **queues** (`202`), it is not
rejected. `queue_depth_exceeded` is the only `429`. (A malformed request body — both/neither payload source,
non-object `inputs`, a non-object payload — is a `400 ProblemDetails` from the boundary validator, not one of
these slugs.)

### 12.2 Save-time payload validation — `400 PayloadValidationProblem`

Each error is `{ "path": <JSON Pointer>, "code": <slug>, "message": … }`. Two kinds of `code`:

- **JSON-Schema keywords** (structural), e.g. `required`, `type`, `enum`, `additionalProperties`, `const`,
  `minimum`, `minLength`, `pattern`, `oneOf` — the failing schema keyword at that path.
- **Semantic slugs:** `unknown_node`, `missing_max_iterations`, `undefined_reference`, `ambiguous_selector`,
  `checkpoint_misplaced`, `checkpoint_not_unique`, `secret_ref_in_expression`, `fill_secret_not_secret_ref`,
  `malformed_node`, `type_error` (a bare non-integral `loop.for` bound), and the expression parse codes
  `unknown_function`, `wrong_arity`, `syntax_error`, `expression_too_deep`.
- `payload_archived` is also returned here (as a `PayloadValidationProblem`) by revise/rename/archive on an
  archived payload.

### 12.3 Run failures — `failure.code`

`failure.class` is `terminal` (never retried) or `retryable-exhausted` (a retryable condition that exhausted
`config.retry`). Engine and expression failures:

| Code | Class | Notes |
|---|---|---|
| `unknown_node` / `missing_max_iterations` | terminal | structural (also caught save-time) |
| `max_iterations_exceeded` | terminal | a loop hit its own `maxIterations` |
| `max_steps_exceeded` | terminal | server cap (knob below) |
| `max_download_bytes_exceeded` | terminal | server cap (knob below) |
| `max_events_exceeded` | terminal | server cap (knob below) |
| `expression_budget_exceeded` | terminal | server cap (knob below) |
| `undefined_push_target` | terminal | `push` target is undefined / not an array |
| `handle_in_result` | terminal | a locator/frame handle leaked into `result` |
| `unknown_backend_adapter` / `invalid_backend_binding` | terminal | `config.backend` did not resolve |
| `backend_unavailable` | terminal | the backend connect/setup faulted |
| `malformed_node` | terminal | a node was structurally malformed at run time |
| `invalid_download_target` / `unknown_download_sink` | terminal | `download.to` did not resolve to a registered sink |
| `fill_secret_not_secret_ref` | terminal | `fill.secret` did not name a `secretRef` input (also save-time) |
| `secret_ref_missing` | terminal | the `secretRef` input was not supplied |
| `unknown_secret_vault` | terminal | the `secretRef`'s vault kind has no adapter |
| `secret_unresolved` | terminal | the vault held no secret for the (safe) reference |
| `run_deadline_exceeded` | terminal | the run wall-clock deadline elapsed (`config.deadlineMs`) |
| `queue_wait_exceeded` | terminal | a queued run outwaited the max-queue-wait bound (knob below) |
| `type_error` | terminal | operator/builtin applied to a rejected type (incl. a non-integral loop bound at run time) |
| `index_out_of_range` | terminal | array index out of range / on null |
| `division_by_zero` | terminal | integer `/` or `%` by zero |
| `int_conversion_failed` | terminal | a required integer conversion (`toInt`) failed |
| `invalid_url` | terminal | a URL builtin got a non-absolute URL |
| `unknown_identifier` | terminal | a bare identifier was unbound at evaluation |
| `regex_too_large` / `regex_timeout` | terminal | a regex exceeded the size / time guard |
| `timeout` / `pageCrashed` | retryable-exhausted | the two retryable conditions, after `config.retry` exhausted |

`guard`/`fail` also raise **author-defined** codes (any slug, `class` `terminal` or `retryable`) from the
node's `Failure` — e.g. the `record_not_accessible` in [§3](#3-running-a-payload--post-runs). Those are your
own vocabulary, not a fixed enum.

> **Inline vs saved:** an inline run skips the save-time semantic pass, so a mistake a saved payload would have
> caught as `undefined_reference` / `ambiguous_selector` surfaces at *run* time instead — typically as
> `unknown_identifier` or a `malformed_node`/selector failure. Save your payload (`POST /payloads`) to get the
> full static check.

### 12.4 Server limits and their config knobs

Every mid-run cap is a deployment config value under `Crawldad:Limits` (a payload can never raise them). Keys
equal the C# property names; defaults are generous so legitimate runs never trip them.

| Failure code / behavior | Config knob (`Crawldad:Limits:…`) | Default |
|---|---|---|
| `max_steps_exceeded` | `MaxStepsPerRun` | `100000` |
| `max_download_bytes_exceeded` | `MaxDownloadedBytesPerRun` | `1073741824` (1 GiB) |
| `max_events_exceeded` | `MaxEventsPerRun` | `100000` |
| `expression_budget_exceeded` | `ExpressionStepBudget` | `1000000` |
| `queue_depth_exceeded` (429) | `MaxQueueDepthPerTenant` | `1000` (per-tenant override) |
| `queue_wait_exceeded` | `MaxQueueWaitMs` | `0` (disabled — wait forever) |
| queues instead of `429` (no code) | `MaxConcurrentRunsPerTenant` | `32` (per-tenant override) |
| sync → async upgrade (no code) | `SyncUpgradeThresholdMs` | `120000` (120 s) |
| `max_iterations_exceeded` | — (the payload's own `loop.maxIterations`) | — |
| `run_deadline_exceeded` | — (the payload's `config.deadlineMs`) | `1800000` (30 min) when omitted |

---

## 13. Reading validation errors

Save-time errors are reported **per (JSON-Pointer location, keyword)**. The one wart to know: a bad node fails
the schema's `oneOf` over the node vocabulary, and the JSON Schema library reports **every** non-matching
branch — so a single malformed node can produce dozens of errors (~98 for a deeply-nested one). Read them like
this:

- The **`path`** (a JSON Pointer, e.g. `/steps/6/loop/do/3`) tells you exactly which node/field is wrong —
  trust the deepest, most specific path.
- Ignore the breadth of `oneOf`/`not`/`required` noise; look for the **semantic slug** (`missing_max_iterations`,
  `ambiguous_selector`, …) or the most specific keyword (`additionalProperties`, `enum`, `const`) at that path.
- An `additionalProperties` error at a node path almost always means a typo'd or unsupported field on that
  node; an `enum`/`const` error means a bad literal (a role outside the ARIA set, a `log.level` that is not
  `info`/`warning`/`error`, etc.).

The validator is intentionally not "fixed" to collapse this noise in this release — the JSON-Pointer path is
the reliable anchor.

---

## 14. Served docs & health

Four routes are deliberately **anonymous** (no key), because each is a public, tenant-independent artifact:

| Route | Returns |
|---|---|
| `GET /health` | `{ "status": "ok" }` — a liveness probe (touches no storage). |
| `GET /schema/crawldad-1.schema.json` | the payload JSON Schema (`application/schema+json`) — usable as an editor `$schema` target. |
| `GET /llms.txt` | the `llms.txt` discovery index (`text/plain`) pointing at this reference, the schema, and the examples. |
| `GET /openapi.json` | the generated OpenAPI 3.1 description of this HTTP envelope (`application/json`) — every endpoint, its auth, request/response contracts, and status codes. Payload request bodies `$ref` the schema above rather than restating the DSL. |

Every other route requires authentication; an endpoint-enumeration test asserts exactly these four are the
only anonymous ones.

---

## 15. Examples

Six curated, schema-valid payloads live in [`docs/examples/`](examples/) (every one is validated against the
schema in CI, so they never drift). Five are lifted verbatim from the tested acceptance fixtures; one
(`login-and-search`) is authored to show the newer surface.

- **[`first-search.json`](examples/first-search.json)** — the gentle intro. Navigate, conditionally `fill`
  two date fields, fire the search via `waitForRequest` (click + await the postback), then `locate` the result
  rows and walk them with a `for` loop, pushing one object per row. Shows the core shape: inputs, waits,
  extraction, and a `result` that reshapes accumulated vars.

- **[`extract-location.json`](examples/extract-location.json)** — the expression language doing real work. A
  `guard` aborts terminally if the page redirected; a `forEach` walks address rows; a `switch` over the address
  block's `<br>` count (`length(split(innerHtml(block), '<br>'))`) selects how to slice city/state/zip — the
  chained `split`/`trim`/`coalesce` string surgery that motivated the sublanguage.

- **[`download-attachment.json`](examples/download-attachment.json)** — the `download` node. It runs a
  `trigger` (clicking a file link), streams the bytes to a `storageTarget` input, and reads back
  `{ contentId, sha256, sizeBytes, storedAs, stored }`. A second download of the same content short-circuits on
  `stored:true` (content-addressed dedup — no re-upload), which the payload branches on.

- **[`login-and-search.json`](examples/login-and-search.json)** — the newer surface, all at once.
  `fill.secret` types a vault-resolved password that never touches an expression; a `screenshot` node captures
  the authenticated page for the audit trail; the form is addressed by **structured selectors** (`role`+`name`,
  `text`, `xpath`); and a **checkpointed `while` loop** paginates results so a host restart resumes from the
  last completed page against a fresh session (the `checkpoint` is the loop body's first node, and its `resume`
  sub-program re-navigates to the cursor).

- **[`search-pagination.json`](examples/search-pagination.json)** — a full checkpointed crawl. The top-level
  `while` (do-while) loop checkpoints each page cursor; nested `for` loops extract rows and accumulate new
  links; `break` stops on an empty page or a known URL; the `result` returns `{ newLinks, crawledToEnd, pages }`.
  This is the long-running / resumable shape end-to-end.

- **[`scrape-record.json`](examples/scrape-record.json)** — the comprehensive one. A single record scrape that
  combines `guard`, `frame` (an attachments iframe), `switch` ladders, a checkpointed attachment-page walk,
  `download`, and deeply nested loops, building one nested result object — the "everything" reference.

---

## 16. Endpoint quick reference

| Method + route | Auth | Body | Success | Errors |
|---|---|---|---|---|
| `POST /runs` | ✔ | `StartRunRequest` | `200 RunResponse` / `202 RunStateResponse` | `400`, `429 queue_depth_exceeded` |
| `GET /runs/{id}` | ✔ | — | `200 RunStateResponse` | `404` |
| `POST /runs/{id}/cancel` | ✔ | — | `202 RunStateResponse` | `404` |
| `GET /runs/{id}/events` | ✔ | — (SSE) | `200 text/event-stream` | `404` |
| `POST /runs/{id}/replay` | ✔ | `ReplayRunRequest` | `200 RunResponse` / `202 RunStateResponse` | `404`, `400 inline_not_replayable` |
| `GET /runs/{id}/drift` | ✔ | — | `200 RunDriftResponse` | `404` |
| `GET /runs/{id}/timeline` | ✔ | — | `200 RunTimelineResponse` | `404` |
| `GET /runs/queue-stats` | ✔ | — | `200 QueueStatsResponse` | — |
| `POST /payloads` | ✔ | `SavePayloadRequest` | `200 PayloadResponse` | `400 PayloadValidationProblem` |
| `GET /payloads` | ✔ | — | `200 PayloadListResponse` | — |
| `GET /payloads/{id}` | ✔ | — | `200 PayloadResponse` | `404` |
| `POST /payloads/{id}/revise` | ✔ | `RevisePayloadRequest` | `200 PayloadResponse` | `404`, `400` |
| `POST /payloads/{id}/rename` | ✔ | `RenamePayloadRequest` | `200 PayloadResponse` | `404`, `400` |
| `POST /payloads/{id}/archive` | ✔ | — | `200 PayloadResponse` | `404`, `400` |
| `GET /payloads/{id}/revisions/{revision}` | ✔ | — | `200 PayloadRevisionResponse` | `404` |
| `GET /payloads/{id}/diff/{from}/{to}` | ✔ | — | `200 PayloadDiffResponse` | `404` |
| `GET /health` | — | — | `200` | — |
| `GET /schema/crawldad-1.schema.json` | — | — | `200 application/schema+json` | — |
| `GET /llms.txt` | — | — | `200 text/plain` | — |
| `GET /openapi.json` | — | — | `200 application/json` | — |

`✔` = requires authentication ([§1](#1-authentication)). All `2xx` bodies are JSON except SSE (`text/event-stream`),
the schema (`application/schema+json`), and `llms.txt` (`text/plain`).
