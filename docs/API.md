# Crawldad API reference

Crawldad runs one JSON **payload** — a small, JSON-Schema'd browser-automation DSL — against a headless
browser and returns a caller-shaped result. This is the single consumer reference: read it top-to-bottom
(with the [payload schema](../schema/crawldad-1.schema.json) for exhaustive field detail) and you can author
a payload and drive a run. It reflects the shipped surface as of this revision; it is derived from the
contracts and endpoints, not from older design notes.

- **The payload is the program.** Structure is JSON (composable, diffable); only leaf expressions are strings
  in a small, pure expression language. The full grammar is the JSON Schema — served live at
  `GET /schema/crawldad-1.schema.json`, where every node and field carries a `description`.
- **The HTTP surface is small.** Runs (`/runs`, plus SSE / cancel / replay / drift / timeline / screenshots /
  queue-stats), managed payloads (`/payloads`, plus revisions / diff), registered browsers (`/browsers`), and
  record/replay fixture sets (`/fixtures`, for offline payload-regression CI). Everything is JSON (except a
  screenshot, which streams as `image/png`); enums serialize camelCase.
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
9. [Drift, timeline, screenshots & erasure — `GET /runs/{id}/drift`, `GET /payloads/{id}/drift-status`, `/timeline`, `/screenshots/{ref}`, `DELETE /runs/{id}`](#9-drift-timeline-screenshots--erasure)
10. [Queue stats — `GET /runs/queue-stats`](#10-queue-stats--get-runsqueue-stats)
11. [Managed payloads — `/payloads`](#11-managed-payloads--payloads)
12. [Browsers — `/browsers`](#12-browsers--browsers)
13. [Fixtures — record/replay for payload regression testing — `/fixtures`](#13-fixtures--recordreplay-for-payload-regression-testing--fixtures)
14. [Webhooks — `/webhooks`](#14-webhooks--webhooks)
15. [Wire codes](#15-wire-codes)
16. [Reading validation errors](#16-reading-validation-errors)
17. [Served docs & health](#17-served-docs--health)
18. [Examples](#18-examples)
19. [Endpoint quick reference](#19-endpoint-quick-reference)
20. [Management API — tenants & API keys](#20-management-api--tenants--api-keys----management)
21. [Dashboard read APIs — runs list, webhook deliveries, tenant & usage](#21-dashboard-read-apis--runs-list-webhook-deliveries-tenant--usage)

---

## 1. Authentication

Every route except the anonymous ones ([§17](#17-served-docs--health)) requires a per-tenant API key,
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

// a real adapter (local | browserless | browserbase); credentialRef names a browser you registered
// via PUT /browsers/{name} (§12), resolved tenant-scoped at connect — never the secret itself
{ "adapter": "browserless", "options": { /* provider passthrough */ }, "credentialRef": "my-browser" }
```

A self-hosted CDP tunnel — local Chromium exposed through `ngrok`/`cloudflared` — is a `browserbase` binding in
`connectUrl` mode (the resolved credential is the whole `wss://` URL). The [tunnel-backend
guide](TUNNEL_BACKEND.md) walks that solo-dev on-ramp end to end.

### 2.2 Nodes (the action set)

Each step is `{ "<head>": { … } }`. The schema is the exhaustive reference; the vocabulary:

- **Navigation / waits:** `goto`, `waitForLoadState`, `waitForRequest` (run a `trigger`, await the request it
  provokes), `waitFor` (await a selector state), `frame` (bind a frame handle), `addStyleTag`.
- **Interaction:** `click`, `fill` (`value` **or** `secret`), `clear`, `screenshot` (full-page PNG),
  `download` (stream a provoked download to a `storageTarget`), `capture` (serialize the full document — doctype +
  `<html>`, not `innerHtml` — or an element subtree via `selector`, and stream it to a `storageTarget`; binds a
  ref, never the HTML). `config.captureOnFailure` banks the failing page's HTML to a BYO target on a step failure.
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
(string / collection / URL / DOM read-only) are the whole surface — the schema and [`PAYLOAD_SPEC.md`](PAYLOAD_SPEC.md) list them.

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

### 2.6 Resilience (`config.retry`)

`config.retry` wraps the whole program on the **already-established** session (it never reconnects — the connect
boundary is `config.connectRetry`'s job). It re-runs up to `maxAttempts` times, retrying **only** the `retryOn`
conditions (default `["timeout","pageCrashed"]`); anything else — and any `class:"terminal"` `guard`/`fail` — is
terminal at once, and a retryable condition that exhausts the budget surfaces as `retryable-exhausted`.

Between attempts the engine waits `delayMs` scaled by `backoff`:

| `backoff` | Wait before the retry after the n-th failed attempt |
|---|---|
| `constant` (default) | `delayMs` — the same every time (the behaviour before this knob shipped) |
| `linear` | `delayMs · n` — base, 2·base, 3·base, … |
| `exponential` | `delayMs · 2ⁿ⁻¹` — base, 2·base, 4·base, … (Polly-style doubling) |

An optional `maxDelayMs` caps the computed wait (bounds `linear`/`exponential` growth; absent ⇒ uncapped), and
`jitter: true` applies **full jitter** — the actual wait is a uniform random value in `[0, computed]`,
de-correlating retriers. An unknown `backoff` is rejected at **save/validate** time (a tightened schema `enum`);
omitting `backoff`/`maxDelayMs`/`jitter` reproduces the historical constant-delay behaviour exactly. Every backoff
wait honours the run wall-clock deadline — a wait that would outlive `config.deadlineMs` ends the run terminally
(`run_deadline_exceeded`) rather than sleeping past it.

`onPageCrashed` governs what happens to the page on a `pageCrashed` — **orthogonally** to `retryOn`, which decides
whether a crash is retried at all. `reopenPage` (the default) closes the crashed page and opens a fresh one on the same
session/context before the retry re-runs the program from the top; `fail` opts out of the reopen, so the crash fails the
attempt on the page it crashed on — retried only when `pageCrashed` is in `retryOn` (on that same page), otherwise
terminal (`retryable-exhausted`). Like `backoff`, an unknown `onPageCrashed` is rejected at **save/validate** time (a
tightened schema `enum`) and lands on `invalid_retry_on_page_crashed` on an inline run; absent ⇒ `reopenPage`, unchanged.

```jsonc
"retry": { "maxAttempts": 5, "delayMs": 1000, "backoff": "exponential", "maxDelayMs": 30000, "jitter": true, "onPageCrashed": "reopenPage" }
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
  "stats": { "durationMs": 812, "steps": 37, "requests": 2, "cacheHits": 0, "downloads": 0, "selectorMisses": 0 }
}
```

Failed run — still `200` (the request succeeded; the run faulted):

```jsonc
{
  "runId": "3f…",
  "status": "failed",
  "failure": {
    "class": "terminal",                     // terminal | retryable-exhausted
    "code": "record_not_accessible",         // a stable slug — see §15
    "message": "Record not accessible (redirected to /Login.aspx)",
    "atStep": { "index": 2, "kind": "guard" }
  },
  "stats": { "durationMs": 410, "steps": 3, "requests": 1, "cacheHits": 0, "downloads": 0, "selectorMisses": 0 }
}
```

`stats`: `durationMs` (wall clock), `steps` (nodes executed — loop bodies re-count per iteration), `requests`
(navigations + matched `waitForRequest`s), `cacheHits` (route cache; 0 until it lands), `downloads`
(completed `download` nodes), `selectorMisses` (extraction selectors — `text`/`innerText`/`innerHtml`/`attr` — that
matched **no element**, the soft drift signal; a matched-but-empty element is **not** counted). A run can **succeed
with `selectorMisses > 0`** — that is precisely the drift alarm ("canary succeeded but misses > 0"). Make a miss
terminal with `require(...)` around the extraction, or `config.strictExtraction: true` run-wide — see
[`PAYLOAD_SPEC.md`](PAYLOAD_SPEC.md).

**Scrubbing of your `result`.** Before it is returned (and persisted for the async poll), `result`/`partial`
pass through the credential scrubber's **exact-secret** rule only: any credential *your run* registered — a
backend `credentialRef`, a `fill.secret` — is redacted to `[redacted]` wherever it appears, so a scraped page
that echoes your own token can never hand it back. The scrubber's *credential-param* rule (which elsewhere
rewrites `apiKey=`/`token=`/`signingKey=` values — see [`THREAT_MODEL.md`](THREAT_MODEL.md)) is **deliberately
not applied to `result`**: your extracted content is yours to receive **verbatim**, so a third-party
`token=`-shaped param in a captured `innerHtml(...)` (a WebForms href, a hidden field, an inline script) survives
unchanged — it is **not** rewritten to `[redacted]` (issue #70). That rule still applies in full to logs,
trace/SSE/timeline events, and failure messages. For the raw document with *no* scrubbing at all (not even the
exact-secret rule), use the `capture` node (§2.2), which streams bytes straight to your own storage.

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
  "stats": { "durationMs": 91234, "steps": 512, "requests": 84, "cacheHits": 0, "downloads": 12, "selectorMisses": 0 },
  "queueWaitMs": 4120 }
```

`404` when there is no such background run — including a purely synchronous run, which never writes a progress
row (its result was returned inline). Read-your-writes: this reflects the latest committed state, not the
lagging cross-run projection.

**After result retention expires the body** (see [§9](#9-drift-timeline-screenshots--erasure)), the poll stays
coherent rather than 404-ing: the run keeps its terminal `status` and `stats`, the `result`/`partial` body is gone,
and a `resultExpiredAt` timestamp marks when it was aged out:

```jsonc
{ "runId": "…", "status": "succeeded", "stats": { /* … */ }, "resultExpiredAt": "2026-08-13T12:00:00+00:00" }
```

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

: keepalive
```

- `id` is the event's **stream version**. Reconnect with `Last-Event-ID: <version>` (or `?lastEventId=<version>`)
  to resume **exactly** where you left off — the durable stream is authoritative, so no frame is lost or
  duplicated across a disconnect.
- `event` is the trace event's type name (`StepStarted`, `Navigated`, `Clicked`, `Waited`, `Extracted`,
  `Downloaded`, `Screenshotted`, `Captured`, `SelectorMiss`, `Filled`, `LogEmitted`, `StepFailed`, …). The stream
  closes on `RunSucceeded`, `RunFailed`, or `RunCancelled`. `SelectorMiss` (`{ selector, stepIndex }`) marks an
  extraction selector that matched nothing — emitted once per distinct selector per run, the soft drift signal for
  `stats.selectorMisses`.
- `data` is the already-**scrubbed** event JSON — no credential ever streams. An unknown (or cross-tenant) run
  is `404` (checked before any SSE headers).
- **Keepalive.** During an idle stretch (a long `waitFor`, a slow page, the gap between a queued run's steps) the
  server emits an SSE **comment frame** — a line beginning with `:` (`: keepalive`) — roughly every **15 s**, so the
  connection keeps flowing bytes and no intermediary (Front Door / Envoy / a corporate proxy) drops it on an idle
  timeout. A real frame resets the timer, so keepalives appear only across a genuine gap. Comment frames carry **no
  `id`** and no `data`, so they never affect `Last-Event-ID` resume; a spec-compliant consumer (e.g. the browser
  `EventSource`) ignores them automatically.

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

## 9. Drift, timeline, screenshots & erasure

### `GET /runs/{id}/drift`
A run's pinned revision vs the payload's current head. Drift = the pinned revision is no longer head.

```jsonc
{ "runId": "…", "payloadId": "…", "pinnedRevision": 3, "pinnedScriptHash": "…",
  "headRevision": 5, "headScriptHash": "…", "drifted": true }
```

Equal hashes under a revision mismatch mean the head moved by a metadata-only change (rename/archive). An
inline run never drifts (`payloadId`/head fields `null`, `drifted:false`). `404` for an unknown run.

### `GET /payloads/{id}/drift-status`
**Per-tenant *selector* drift monitoring** (distinct from the run-*revision* drift above): productizes the nightly
canary into a pollable signal. Run a payload's pinned revision on a schedule — against the live site, or a
fixture-replay baseline ([§13](#13-fixtures--recordreplay-for-payload-regression-testing--fixtures)) — and poll
this to learn whether the extraction has drifted from the page it targets.

```jsonc
{ "payloadId": "…", "payloadName": "ljcmg.enforcement", "pinnedRevision": 4,
  "state": "drifted",              // noData | warmingUp | steady | drifted
  "drifted": true,                 // the boolean alarm (state == "drifted")
  "observedRuns": 37, "baselineRuns": 3, "driftedSelectorCount": 1, "threshold": 0,
  "firstObservedAt": "…", "lastObservedAt": "…",
  "selectors": [ { "selector": "#lblRecordNumber", "drifted": true, "baselineFloor": false, "missingInLatest": true } ],
  "evidence": { "runId": "…", "status": "succeeded", "observedAt": "…",
                "failureScreenshotRef": null, "captureRefs": ["captures/…"], "screenshotRefs": ["screenshots/…"] } }
```

The signal is **baseline/delta**, deliberately *not* `selectorMisses > 0` — a payload with a legitimate
multi-selector fallback (`coalesce(text(a), text(b))`) misses on every run at steady state. The **baseline** is the
miss floor of the earliest `baselineRuns` healthy (succeeded) runs; **drift** is a selector that matched at baseline
but is *newly* missing in the latest completed run. A selector missing since the baseline is reported as
`baselineFloor:true`, never `drifted`. Optional `?threshold=N` tolerates `N` new misses before `drifted` is set
(default `0`). `evidence` carries the latest run's `capture`/`screenshot` refs (see the timeline/screenshot sections
below) so an alert arrives with the changed page in hand.

**Scoped to the pinned revision.** The baseline, `observedRuns`, and `firstObservedAt` are scoped to the pinned
revision of the *latest completed run* — reported as `pinnedRevision`. A payload edit that adds or renames selectors
advances that revision, so the baseline re-establishes against the new revision's own earliest healthy runs — the new
revision's selectors are never reported as permanent drift against the old revision's floor. When that revision has
not yet accumulated its baseline window the state is `warmingUp`; a **rollback or re-pin to an already-baselined
revision** (running an older `revision:N` again) instead resumes `steady`/`drifted` immediately against that
revision's own established floor — it does *not* re-warm. Consequently `firstObservedAt` is the first *healthy*
observation **of the current revision** (not the payload's first-ever run), and `observedRuns` counts that revision's
completed runs, not the whole cross-revision history.

Because the current revision is whichever the *latest completed run* pinned, interleaving two revisions in one run
stream — an ad-hoc run at head landing between the canary's own pinned-revision runs — can flip `pinnedRevision` and
the assessed state for a **single poll**, transiently masking a real drift on the pinned revision. The canary's next
run at its pinned revision self-corrects it; this is intentional (drift is a slow, polled signal), so poll the
revision you pinned rather than mixing ad-hoc head runs into a canary's stream.

States: `noData` (no completed run yet), `warmingUp` (baseline not yet established for the current revision — nothing
is alarmed), `steady` (baseline established, no new misses beyond `threshold`), `drifted`. Only **durable** (async)
runs emit the selector-miss trace, so only they are observed. Tenant-scoped: an unknown or foreign payload is a `404`.

### `GET /runs/{id}/timeline`
The observability read model (the lag-tolerant cross-run view): ordered steps with per-step durations, the
**redacted** input key *names*, extracted-value shape refs (never values), download, screenshot, and **capture**
blob refs (never bytes), the `missedSelectors` (the run's distinct extraction selectors that matched nothing — the
per-run drift signal [`GET /payloads/{id}/drift-status`](#get-payloadsiddrift-status) folds), the terminal failure +
its screenshot and capture refs, the pinned revision + script hash, and the backend region. Everything derives from
already-scrubbed trace events, so no raw credential or bulk PII surfaces.
`404` for an unknown run. Each `screenshotRef` here (and `failure.screenshotRef`) is fetched as an actual PNG
via [`GET /runs/{id}/screenshots/{ref}`](#get-runsidscreenshotsref) below.

`captures[]` lists the documents a `capture` node (or `config.captureOnFailure`) streamed to the tenant's **BYO**
storage — `{ blobRef, size, sha256 }`, never the HTML. Unlike screenshots, captured bytes live in the customer's
own storage target under the customer's own retention, so Crawldad serves no read-back endpoint for them (the ref
is resolved against the tenant's storage, not ours). A run that fails a selector wait with `captureOnFailure`
configured leaves the failing page's HTML in `captures[]`, and `failure.captureRef` carries that entry's `blobRef`
(null when nothing was captured) — so you resolve the failing page's document by an explicit ref match rather than by
guessing which `captures[]` entry it is, exactly as `failure.screenshotRef` links the failure screenshot.

### `GET /runs/{id}/screenshots/{ref}`
Streams a captured screenshot — an authored `screenshot` node, or a screenshot-on-failure — back as
`image/png`. The `{ref}` is the timeline's `screenshotRef` with its `screenshots/` prefix dropped, i.e. the
`{sha256}.png` tail:

```text
timeline screenshotRef:  screenshots/9f8c…21.png
GET /runs/7b3…/screenshots/9f8c…21.png   →   200 image/png
```

**Authorization is by run association, not by blob knowledge.** The ref must actually appear in *this* run's
tenant-scoped trace; a ref that belongs to another run — or another tenant, or nothing — is a `404`,
indistinguishable from an unknown run (a tenant can never confirm another's capture exists). Screenshots are
content-addressed, so the bytes for a ref never change: the response is cacheable **privately** (the content is
tenant-scoped) and long-lived, with a digest `ETag` for revalidation (a matching `If-None-Match` gets `304`).

Fetching **mid-run** works — the interpreter stores each capture's blob *before* it records the ref, so any ref
visible in the trace already has its bytes. A `404` after the ref is authorized means the blob has **expired**:
screenshots can show PII, so the retention janitor deletes them once past their (shorter) retention window,
while the immutable trace keeps the ref forever. `404` also covers an unknown run or a malformed ref.

### Result retention

An **async** run's terminal `result`/`partial` is persisted (so the poll can serve it) and can carry PII — scraped
page content. Like screenshots, it is aged out on the host retention policy: a scheduled sweep (the same
`Crawldad:Storage:Retention` janitor, cadence `SweepInterval`) nulls the stored body once past **`ResultTtl` (default
7 days**, the PII-grade window — the sync path never persists a result, so the stored async copy is only a poll
convenience). `ResultTtl: 0` retains stored results indefinitely.

What expires is the **body only**, not the run: after expiry `GET /runs/{id}` still returns `200` with the terminal
`status` and `stats`, no `result`/`partial`, and a `resultExpiredAt` marker (see [§5](#5-polling--get-runsid)) — never
a surprising `404`. The immutable event timeline is untouched by this sweep; erase it on demand with `DELETE` below.

### `DELETE /runs/{id}` — erase result & timeline

On-demand right-to-erasure for a **finished** run: hard-deletes, in one tenant-scoped transaction, the run's stored
result (`RunProgress`), its `Run` snapshot and timeline read models, and its **event stream** — so both the bulk
result body and the incidental PII a scrubbed timeline can still hold (a `LogEmitted` message, a `Navigated` URL) are
gone, not merely archived. The response is `204` with no body (the erased content is never echoed).

- **`404`** when this tenant has no such run — unknown, another tenant's, already-erased, or a purely-synchronous run
  (which never wrote a progress row). No existence oracle, so a **repeated `DELETE` is idempotent** (`204` then `404`),
  the same shape as `DELETE /browsers/{name}`.
- **`409 run_still_active`** (a `RunRejection` body) when the run is still `running` or `queued`: a live run still has
  an executor (or a queue entry) writing to it, so it is not erasable — **cancel it first** ([§7](#7-cancel--post-runsidcancel)),
  then delete the settled run.

After a successful erase, `GET /runs/{id}`, `/timeline`, `/drift`, and `/events` all `404` — the run is gone
coherently.

**Scope — what this does not delete.** The run's **blob artifacts** (its `screenshot`/`download`/`capture` files) are
*not* removed by `DELETE`; they age out on their own retention (screenshots/downloads on `ScreenshotTtl`/`DownloadTtl` via
the blob janitor, or never if that TTL is `0`; captures live in the tenant's own storage under the tenant's own retention).
This erases the run's stored **result body and event/timeline state**; blob-level on-demand erasure is separate
(THREAT_MODEL.md, future work).

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

A schema/semantic failure is `400` with the full structured error list ([§16](#16-reading-validation-errors)):

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

## 12. Browsers — `/browsers`

A tenant registers its browser **connect credentials** through the API rather than an operator editing host config, so onboarding is self-service and every credential is isolated to the tenant that owns it. A registered **name** becomes the `credentialRef` a payload's `config.backend` references ([§2.1](#21-inputs)); at connect time the credential is resolved **tenant-scoped** — a registered browser first, then a tenant-namespaced config fallback (`Secrets:{tenant}:{ref}`) — so no tenant can resolve, list, or delete another's. The **secret is encrypted at rest** (ASP.NET Data Protection) and is **never** returned by any endpoint, nor written to any event or log.

### `PUT /browsers/{name}` — register or replace

`{name}` is a slug (lowercase letters, digits, hyphens; 1–64 chars; no leading/trailing hyphen) and becomes the `credentialRef`.

```jsonc
{
  "adapter": "browserbase",             // browserbase (connectUrl | apiKey) | browserless (apiKey only)
  "mode": "connectUrl",                 // connectUrl (the secret is the whole wss/https URL) | apiKey (a provider key)
  "secret": "wss://…",                  // write-only — never echoed back
  "options": { "region": "us-east-1" }  // optional provider metadata (surfaced in listings, never the secret)
}
```

`200` returns the stored metadata (never the secret); a replace preserves `createdAt`:

```json
{ "name": "prod-bb", "adapter": "browserbase", "mode": "connectUrl",
  "options": { "region": "us-east-1" }, "createdAt": "2026-08-10T12:00:00Z", "updatedAt": "2026-08-10T12:00:00Z" }
```

`400` (RFC 7807 problem+json) when the name is not a valid slug, the adapter or mode is unknown, the adapter has no connect path for the mode (`browserless` is token-only, so `browserless` + `connectUrl` is rejected — see the note below), the secret is empty, or a `connectUrl` secret is not `wss://`/`https://`.

**What the registration drives.** The stored `mode` and `options` are **metadata** today: they shape the listing and are shape-validated here, but at connect the adapter resolves the credential *by reference* and takes its live mode/options from the **payload's** `config.backend` binding ([§2.1](#21-inputs)), not from this registration. `browserbase` reads `options.mode` from that binding to select `connectUrl` vs `apiKey`; `browserless` always connects natively by token (`?token=…`). Because `browserless` has **no** connectUrl connect path, registering it in `connectUrl` mode is an inert combination — it would save cleanly and then fail closed at connect — so it is rejected here with a `400` rather than banked as a silent misconfiguration.

### `GET /browsers` — list

`200 { "browsers": [ { name, adapter, mode, options, createdAt, updatedAt }, … ] }` — every browser this tenant has registered, ordered by name. **Secrets are never included.**

### `DELETE /browsers/{name}` — unregister

`204` on success; `404` when this tenant has no such name — a name owned by another tenant is simply absent here, so a cross-tenant delete is a plain not-found with no existence oracle.

## 13. Fixtures — record/replay for payload regression testing — `/fixtures`

A **payload revision is executable logic**, and a fixture set is how a tenant tests it **offline before it runs against a live site** — the same architecture Crawldad's own acceptance suite uses, generalized to tenants. You **record** a representative live session into a named, tenant-scoped set (each visited page's URL + serialized DOM plus the interactions between them), then **replay** any payload against that set through `POST /runs` — deterministically, with **zero live traffic** — so an external CI job can golden-gate a revision before promoting it. Sets are **tenant-isolated** (a name owned by another tenant is simply absent here) and **managed** resources: they persist until you re-record or delete them (no retention TTL).

Recording captures a **deliberately linear subset**: state-per-navigation/click, page-level **CSS** clicks, and postback emits. That faithfully replays a search/detail extraction session — the phase-2 workload — while staying honest about its limits: a `download`, an in-frame click, or a non-CSS (structured) click selector is **not recorded** and fails the record run classified (`fixture_unrecordable`), rather than banking a set that cannot replay. Each `gotoUrl` is fixed on first sighting of a page, so a set is at its most faithful when each recorded page has a stable URL (the extraction workload's shape).

**Credential safety.** Every URL a manifest persists — each state's `gotoUrl` and `url`, and each transition's postback prefix — is run through the **same credential redaction the run timeline applies to a `Navigated` URL** (exact registered secrets, plus `apiKey`/`token`/`signingKey` query params → `[redacted]`) *at record time*, when the run's secret scope is live. So a set never stores or returns a secret-bearing URL — nothing credential-bearing is persisted. One consequence: because the stored `gotoUrl` is redacted, a **navigation whose URL carries a secret is not replayable** (the strict replay's raw goto no longer exact-matches the redacted stored URL → `fixture_state_miss`) — fixtures target offline extraction, where navigation URLs are credential-free. (Recorded page **HTML** is stored verbatim at rest — like the capture channel, scrubbing it would break replay fidelity — but is only ever referenced by hash, never returned by the API.)

### `POST /fixtures/{name}/record` — record a set

`{name}` is a slug (lowercase letters, digits, hyphens; 1–64 chars; no leading/trailing hyphen). The body is a run request — an inline `payload` plus its `inputs` (the **live** backend binding the session runs against, credentialRefs, parameters):

```jsonc
{
  "payload": { "crawldad": "1", "name": "county.parcel.search-detail", "config": { "backend": "input.backend" }, "steps": [ … ], "result": "…" },
  "inputs":  { "backend": { "adapter": "browserless", "credentialRef": "prod-bl" }, "query": "100 Main St" }
}
```

The run executes **inline** (a record-once setup step, not a queued run) against its configured backend, banking each settled page state and click. On success the set is stored (**replacing** any prior set of that name) and `200` returns the recorded summary + the run's own `result`:

```jsonc
{
  "runId": "9c…",
  "status": "succeeded",
  "fixture": { "name": "accela-search", "pageCount": 3, "transitionCount": 2, "totalBytes": 4821,
               "runId": "9c…", "createdAt": "2026-08-12T12:00:00Z" },
  "result": { … the payload's result, from the recorded pages … },
  "stats":  { … }
}
```

A record run that **fails** — a run failure, or an unrecordable operation — is `HTTP 200` with a typed `failure` (`status: "failed"`) and **persists no set**, exactly like a failed `POST /runs`. `400` when the name is not a valid slug.

### `GET /fixtures` — list

`200 { "fixtures": [ { name, pageCount, transitionCount, totalBytes, runId, createdAt }, … ] }` — every set this tenant has recorded, ordered by name. Page HTML is never included.

### `GET /fixtures/{name}` — inspect

`200 { "summary": { … }, "manifest": { "initialState", "states": { … url + content-hash … }, "transitions": [ … ] } }` — the recorded state machine, so you can see exactly what coverage a replay has. Page HTML is referenced only by hash, never surfaced. `404` when this tenant has no such set.

### `DELETE /fixtures/{name}` — erase

`204` on success (the manifest and all its page HTML, in one tenant-scoped transaction); `404` when this tenant has no such name.

### Replay — a normal run against the set

Replay is not a new endpoint: `POST /runs` with the payload and a backend input naming the set — `{ "adapter": "fixture", "options": { "fixtureSet": "accela-search" } }`. The run pipeline is unchanged (SSE, stats, timeline all apply); only the backend is the recorded set instead of a live browser. Replay is **strict**: a `goto` to a URL the set never recorded fails `fixture_state_miss` (naming the URL), and a click with no recorded transition fails `fixture_transition_miss` — a divergence from recorded coverage fails **classified**, never a hang or a silent mis-replay. Naming a set that does not exist (or another tenant's) is `backend_unavailable`.

### Golden-gating a revision in CI

The full loop an external pipeline runs — record once, then gate every revision on a green, golden-matching replay before promoting it:

```yaml
# .github/workflows/gate-payload.yml
jobs:
  gate:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      # One-time (or when the site's shape changes): record a representative session.
      - name: Record fixture set
        run: |
          curl -fsS -X POST "$CRAWLDAD/fixtures/accela-search/record" \
            -H "X-Api-Key: ${{ secrets.CRAWLDAD_KEY }}" -H 'Content-Type: application/json' \
            --data @record-request.json
      # Per revision: replay the candidate payload against the set and diff its result against the golden.
      - name: Replay + golden-compare
        run: |
          jq -n --slurpfile p payload.json \
            '{ payload: $p[0], inputs: { backend: { adapter: "fixture", options: { fixtureSet: "accela-search" } } } }' \
            > replay.json
          curl -fsS -X POST "$CRAWLDAD/runs" \
            -H "X-Api-Key: ${{ secrets.CRAWLDAD_KEY }}" -H 'Content-Type: application/json' \
            --data @replay.json | jq -e '.status == "succeeded"' > /dev/null
          # …and diff `.result` against golden.json with your differ of choice (jq, etc.).
      # Only a green gate promotes the revision production runs:
      - name: Promote
        run: curl -fsS -X POST "$CRAWLDAD/payloads/$PAYLOAD_ID/revise" -H "X-Api-Key: ${{ secrets.CRAWLDAD_KEY }}" --data @payload.json
    env:
      CRAWLDAD: https://api.crawldad.dev
      PAYLOAD_ID: 3f2504e0-4f89-11d3-9a0c-0305e82c3301
```

Crawldad returns the replay `result` normally; the **golden comparison stays external** (your CI owns the golden and the diff) — there is no server-side diff endpoint for this. The candidate `payload.json` you replay is exactly what you promote with `POST /payloads/{id}/revise`, so a green gate proves the revision that production will run.

## 14. Webhooks — `/webhooks`

A tenant registers **webhook endpoints** through the API (the same self-service shape as `/browsers`) to be **pushed** a run's terminal disposition rather than polling `GET /runs/{id}`. When a durable run reaches a terminal state, Crawldad POSTs a small, **ref-only** JSON envelope — signed with an HMAC the receiver verifies — to each subscribed endpoint. Delivery is **durable and at-least-once** with bounded exponential-backoff retry on an independent channel: **a slow or failing receiver never affects run execution.**

### `PUT /webhooks/{name}` — register or replace

`{name}` is a slug (lowercase letters, digits, hyphens; 1–64 chars; no leading/trailing hyphen).

```jsonc
{
  "url": "https://hooks.example.com/crawldad",  // https only; not a loopback/private/link-local address (SSRF guard)
  "secret": "whsec_…",                          // the HMAC signing secret; write-only — never echoed back
  "events": ["run.failed"]                       // optional; omit or [] to receive ALL terminal events
}
```

`200` returns the stored metadata (never the secret); a replace preserves `createdAt`:

```json
{ "name": "prod", "url": "https://hooks.example.com/crawldad", "events": ["run.failed"],
  "createdAt": "2026-08-12T12:00:00Z", "updatedAt": "2026-08-12T12:00:00Z" }
```

`400` (RFC 7807 problem+json) when the name is not a valid slug, the URL is not `https` or targets a private/loopback/link-local address, the secret is empty or shorter than 16 characters, or an event type is unknown.

The registration check classifies the URL's literal host; the same block-list is **re-applied at delivery**, where the target host is resolved, every resolved address re-checked, and the connection **pinned** to a validated address — so a DNS name that points or later rebinds to an internal address is refused before the request is sent. **Redirects are not followed** (a `3xx` is a failed delivery), so your endpoint must accept the `POST` directly at a publicly-routable `https` address.

The **secret is caller-supplied and encrypted at rest** (ASP.NET Data Protection); it is **never** returned by any endpoint, event, or log. **Rotate** it by re-registering (`PUT`) with a new value.

### `GET /webhooks` — list

`200 { "webhooks": [ { name, url, events, createdAt, updatedAt }, … ] }` — ordered by name. **Secrets are never included.** An empty `events` array means the endpoint receives all terminal events.

### `DELETE /webhooks/{name}` — unregister

`204` on success; `404` when this tenant has no such name (a name owned by another tenant is simply absent — no existence oracle).

### The delivery — `WebhookEventEnvelope`

A terminal run POSTs this body (`application/json`) to each subscribed endpoint:

```json
{ "id": "3f2a…", "type": "run.succeeded", "runId": "…", "payloadId": "…", "revision": 4,
  "status": "succeeded", "stats": { "durationMs": 1234, "steps": 3, "requests": 4, "cacheHits": 0, "downloads": 0, "selectorMisses": 0 },
  "finishedAt": "2026-08-12T12:00:05Z" }
```

- **Refs only, never result content.** The body carries the run id + metadata, never the `result`/`partial` — fetch `GET /runs/{id}` on receipt. This keeps bodies small and PII-free.
- `payloadId`/`revision` are present only for a pinned managed-payload run (absent for an inline run); `failure` (the typed `RunFailureDetail`, see [§15.3](#153-run-failures--failurecode)) is present only for a `run.failed` event.
- **Event catalog** — subscribe to a subset via `events`, or all: `run.succeeded`; `run.failed` (includes a wall-clock deadline or a queue-wait timeout); `run.cancelled` (cooperative, or cancelled while still queued).
- **Scope.** Webhooks fire for runs on the **durable** surface — an `async:true` run, or any run promoted from the admission queue, plus a queued run that terminates in the queue (cancel/timeout). A run that completes on the **synchronous** fast-path returns its disposition in the `POST /runs` response and is not additionally delivered.

### Verifying a delivery — headers & signature

Every POST carries:

| Header | Value |
|---|---|
| `X-Crawldad-Event` | the event type (`run.succeeded` / `run.failed` / `run.cancelled`) |
| `X-Crawldad-Delivery` | the event `id` (stable across retries of one delivery) |
| `X-Crawldad-Timestamp` | the send time, Unix seconds |
| `X-Crawldad-Signature` | `sha256=<hex>`, where `<hex> = HMAC-SHA256(secret, "{timestamp}.{rawBody}")` |

To verify: recompute `HMAC-SHA256(your_secret, X-Crawldad-Timestamp + "." + rawBody)` over the **raw** request bytes (before any JSON re-serialization), hex-encode, and compare in constant time to the hex after `sha256=`. Reject the delivery if `X-Crawldad-Timestamp` is older than your tolerance — a replay bound.

### Retries & idempotency

Delivery is **at-least-once**. A non-`2xx` response or a transport failure (connection error, per-attempt timeout) is retried with **exponential backoff** (`Crawldad:Webhooks:Delivery` — default base 10 s, doubling, capped at 5 min, up to 8 attempts, with a 10 s per-attempt timeout); past the attempt cap the delivery is abandoned. Because delivery can repeat, **make your handler idempotent** — dedupe on (`runId`, `status`), since a run has exactly one terminal disposition — and return `2xx` promptly (do the work asynchronously) so a slow handler is not retried.

## 15. Wire codes

Three surfaces carry stable slugs: **request & control rejections** (a `RunRejection` body — `4xx`/`429`), **save-time
validation** (`400` on `/payloads`), and **run failures** (`failure.code`, `HTTP 200` sync or terminal after a
`202`). Enum values below are exact.

### 15.1 Request & control rejections — `RunRejection`

| Code | HTTP | Where | Meaning |
|---|---|---|---|
| `unknown_payload` | 400 | `POST /runs`, `/replay` | pinned payload id does not exist |
| `unknown_revision` | 400 | `POST /runs`, `/replay` | pinned revision does not exist |
| `payload_archived` | 400 | `POST /runs`, `/replay` | the pinned payload is archived (cannot run) |
| `inline_not_replayable` | 400 | `POST /runs/{id}/replay` | the run executed an inline payload (no stored revision to replay) |
| `queue_depth_exceeded` | 429 | `POST /runs`, `/replay` | the tenant's admission queue is at its depth cap |
| `run_still_active` | 409 | `DELETE /runs/{id}` | the run is still `running`/`queued`, so it cannot be erased — cancel it first |

There is **no** `concurrent_runs_exceeded` — at the concurrent-run cap a run **queues** (`202`), it is not
rejected. `queue_depth_exceeded` is the only `429`. (A malformed request body — both/neither payload source,
non-object `inputs`, a non-object payload — is a `400 ProblemDetails` from the boundary validator, not one of
these slugs.)

### 15.2 Save-time payload validation — `400 PayloadValidationProblem`

Each error is `{ "path": <JSON Pointer>, "code": <slug>, "message": … }`. Two kinds of `code`:

- **JSON-Schema keywords** (structural), e.g. `required`, `type`, `enum`, `additionalProperties`, `const`,
  `minimum`, `minLength`, `pattern`, `oneOf` — the failing schema keyword at that path.
- **Semantic slugs:** `unknown_node`, `missing_max_iterations`, `undefined_reference`, `ambiguous_selector`,
  `capture_in_without_selector` (a `capture` with `in` but no `selector` — the frame has nothing to scope),
  `checkpoint_misplaced`, `checkpoint_not_unique`, `secret_ref_in_expression`, `fill_secret_not_secret_ref`,
  `malformed_node`, `type_error` (a bare non-integral `loop.for` bound), and the expression parse codes
  `unknown_function`, `wrong_arity`, `syntax_error`, `expression_too_deep`.
- `payload_archived` is also returned here (as a `PayloadValidationProblem`) by revise/rename/archive on an
  archived payload.

### 15.3 Run failures — `failure.code`

`failure.class` is `terminal` (never retried) or `retryable-exhausted` (a retryable condition that exhausted
`config.retry`). Engine and expression failures:

| Code | Class | Notes |
|---|---|---|
| `unknown_node` / `missing_max_iterations` | terminal | structural (also caught save-time) |
| `max_iterations_exceeded` | terminal | a loop hit its own `maxIterations` |
| `max_steps_exceeded` | terminal | server cap (knob below) |
| `max_download_bytes_exceeded` | terminal | server cap (knob below) |
| `max_capture_bytes_exceeded` | terminal | server cap (knob below) — the `capture` channel's sibling of the download cap |
| `max_events_exceeded` | terminal | server cap (knob below) |
| `expression_budget_exceeded` | terminal | server cap (knob below) |
| `undefined_push_target` | terminal | `push` target is undefined / not an array |
| `handle_in_result` | terminal | a locator/frame handle leaked into `result` |
| `unknown_backend_adapter` / `invalid_backend_binding` | terminal | `config.backend` did not resolve |
| `invalid_retry_backoff` | terminal | `config.retry.backoff` named a strategy outside `constant`/`linear`/`exponential` (rejected at save/validate time; an inline run lands here) |
| `invalid_retry_on_page_crashed` | terminal | `config.retry.onPageCrashed` named an option outside `reopenPage`/`fail` (rejected at save/validate time; an inline run lands here) |
| `backend_unavailable` | terminal | the backend connect/setup faulted — single-shot unless `config.connectRetry` retries a **transient** fault (a tunnel reconnect, a refused socket, a 5xx, a 429/408 throttle); an auth-shaped fault (rejected key, a 4xx other than 429/408, absent credential) fails fast, and exhausting the bounded attempts stays terminal here |
| `malformed_node` | terminal | a node was structurally malformed at run time |
| `invalid_download_target` / `unknown_download_sink` | terminal | `download.to` did not resolve to a registered sink |
| `invalid_capture_target` / `unknown_capture_sink` | terminal | `capture.to` (or `config.captureOnFailure.to`) did not resolve to a registered sink |
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
| `selector_miss` | terminal | a `require(...)`-wrapped extraction (or any extraction under `config.strictExtraction`) matched no element |
| `fixture_state_miss` | terminal | (replay, [§13](#13-fixtures--recordreplay-for-payload-regression-testing--fixtures)) a `goto` reached a URL the tenant fixture set never recorded — the message names it |
| `fixture_transition_miss` | terminal | (replay, [§13](#13-fixtures--recordreplay-for-payload-regression-testing--fixtures)) a click had no recorded transition from the current fixture state |
| `fixture_unrecordable` | terminal | (record, [§13](#13-fixtures--recordreplay-for-payload-regression-testing--fixtures)) `POST /fixtures/{name}/record` hit an operation it cannot capture (a download, an in-frame or non-CSS click, or a session that never navigated) — no set is persisted |
| `unknown_identifier` | terminal | a bare identifier was unbound at evaluation |
| `regex_too_large` / `regex_timeout` | terminal | a regex exceeded the size / time guard |
| `timeout` / `pageCrashed` | retryable-exhausted | the two retryable conditions, after `config.retry` exhausted. On the real backend a closed-target fault (a Playwright op that starts on an already-dead page, e.g. re-driving the same page after `onPageCrashed: "fail"`) is treated as `pageCrashed` too — provider-side session death classifies here rather than escaping as a raw engine error |

`guard`/`fail` also raise **author-defined** codes (any slug, `class` `terminal` or `retryable`) from the
node's `Failure` — e.g. the `record_not_accessible` in [§3](#3-running-a-payload--post-runs). Those are your
own vocabulary, not a fixed enum.

> **Inline vs saved:** an inline run skips the save-time semantic pass, so a mistake a saved payload would have
> caught as `undefined_reference` / `ambiguous_selector` surfaces at *run* time instead — typically as
> `unknown_identifier` or a `malformed_node`/selector failure. Save your payload (`POST /payloads`) to get the
> full static check.

### 15.4 Server limits and their config knobs

Every mid-run cap is a deployment config value under `Crawldad:Limits` (a payload can never raise them). Keys
equal the C# property names; defaults are generous so legitimate runs never trip them.

| Failure code / behavior | Config knob (`Crawldad:Limits:…`) | Default |
|---|---|---|
| `max_steps_exceeded` | `MaxStepsPerRun` | `100000` |
| `max_download_bytes_exceeded` | `MaxDownloadedBytesPerRun` | `1073741824` (1 GiB) |
| `max_capture_bytes_exceeded` | `MaxCapturedBytesPerRun` | `1073741824` (1 GiB) |
| `max_events_exceeded` | `MaxEventsPerRun` | `100000` |
| `expression_budget_exceeded` | `ExpressionStepBudget` | `1000000` |
| `queue_depth_exceeded` (429) | `MaxQueueDepthPerTenant` | `1000` (per-tenant override) |
| `queue_wait_exceeded` | `MaxQueueWaitMs` | `0` (disabled — wait forever) |
| queues instead of `429` (no code) | `MaxConcurrentRunsPerTenant` | `32` (per-tenant override) |
| sync → async upgrade (no code) | `SyncUpgradeThresholdMs` | `120000` (120 s) |
| `max_iterations_exceeded` | — (the payload's own `loop.maxIterations`) | — |
| `run_deadline_exceeded` | — (the payload's `config.deadlineMs`) | `1800000` (30 min) when omitted |

---

## 16. Reading validation errors

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

## 17. Served docs & health

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

## 18. Examples

Eight curated, schema-valid payloads live in [`docs/examples/`](examples/) (every one is validated against the
schema in CI, so they never drift). Five are lifted verbatim from the tested acceptance fixtures; three
(`login-and-search`, `capture-document`, `strict-extraction`) are authored to show the newer surface.

- **[`first-search.json`](examples/first-search.json)** — the gentle intro. Navigate, conditionally `fill`
  two date fields, fire the search via `waitForRequest` (click + await the postback), then `locate` the result
  rows and walk them with a `for` loop, pushing one object per row. Shows the core shape: inputs, waits,
  extraction, and a `result` that reshapes accumulated vars.

- **[`extract-location.json`](examples/extract-location.json)** — the expression language doing real work. A
  `guard` aborts terminally if the page redirected; a `forEach` walks address rows; a `switch` over the address
  block's `<br>` count (`length(split(innerHtml(block), '<br>'))`) selects how to slice city/state/zip — the
  chained `split`/`trim`/`coalesce` string surgery that motivated the sublanguage. The `innerHtml(...)` markup
  that lands in `result` is returned **verbatim** — a `token=`-shaped param in the scraped page is never
  param-scrubbed to `[redacted]` (§3, issue #70).

- **[`download-attachment.json`](examples/download-attachment.json)** — the `download` node. It runs a
  `trigger` (clicking a file link), streams the bytes to a `storageTarget` input, and reads back
  `{ contentId, sha256, sizeBytes, storedAs, stored }`. A second download of the same content short-circuits on
  `stored:true` (content-addressed dedup — no re-upload), which the payload branches on.

- **[`capture-document.json`](examples/capture-document.json)** — the `capture` node, the document channel
  symmetric with `download`. It captures the full rendered document (doctype + `<html>`) and, separately, a grid
  subtree (`selector` → `outerHTML`), streaming each content-addressed to a `storageTarget` input and pushing only
  `{ url, captureRef, sha256, sizeBytes }` into the result — a compact **manifest of refs**, never the HTML, which
  bypasses the credential scrubber and Crawldad's own retention entirely. `config.captureOnFailure` banks the
  failing page's HTML to the same BYO target for selector-drift diagnosis.

- **[`strict-extraction.json`](examples/strict-extraction.json)** — observable selector-miss (§3, issue #75). A
  **required** anchor `trim(require(text('#…lblPermitNumber')))` fails the run `selector_miss` (banking the failing
  page via `captureOnFailure`) the instant the record-number id drifts — instead of a page of empty records. The
  optional fields stay **soft** (`coalesce(text('#…'), '')`): a miss degrades to `''` but still increments
  `stats.selectorMisses` and emits a `SelectorMiss` event, so a drifting county is visible to drift monitoring while
  the run succeeds. Flip `config.strictExtraction: true` to make every field required without per-field `require(...)`.

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

## 19. Endpoint quick reference

| Method + route | Auth | Body | Success | Errors |
|---|---|---|---|---|
| `POST /runs` | ✔ | `StartRunRequest` | `200 RunResponse` / `202 RunStateResponse` | `400`, `429 queue_depth_exceeded` |
| `GET /runs/{id}` | ✔ | — | `200 RunStateResponse` | `404` |
| `POST /runs/{id}/cancel` | ✔ | — | `202 RunStateResponse` | `404` |
| `DELETE /runs/{id}` | ✔ | — | `204` | `404`, `409 run_still_active` |
| `GET /runs/{id}/events` | ✔ | — (SSE) | `200 text/event-stream` | `404` |
| `POST /runs/{id}/replay` | ✔ | `ReplayRunRequest` | `200 RunResponse` / `202 RunStateResponse` | `404`, `400 inline_not_replayable` |
| `GET /runs/{id}/drift` | ✔ | — | `200 RunDriftResponse` | `404` |
| `GET /runs/{id}/timeline` | ✔ | — | `200 RunTimelineResponse` | `404` |
| `GET /runs/{id}/screenshots/{ref}` | ✔ | — | `200 image/png` | `404` (unknown run / ref, or expired) |
| `GET /runs/queue-stats` | ✔ | — | `200 QueueStatsResponse` | — |
| `POST /payloads` | ✔ | `SavePayloadRequest` | `200 PayloadResponse` | `400 PayloadValidationProblem` |
| `GET /payloads` | ✔ | — | `200 PayloadListResponse` | — |
| `GET /payloads/{id}` | ✔ | — | `200 PayloadResponse` | `404` |
| `POST /payloads/{id}/revise` | ✔ | `RevisePayloadRequest` | `200 PayloadResponse` | `404`, `400` |
| `POST /payloads/{id}/rename` | ✔ | `RenamePayloadRequest` | `200 PayloadResponse` | `404`, `400` |
| `POST /payloads/{id}/archive` | ✔ | — | `200 PayloadResponse` | `404`, `400` |
| `GET /payloads/{id}/revisions/{revision}` | ✔ | — | `200 PayloadRevisionResponse` | `404` |
| `GET /payloads/{id}/diff/{from}/{to}` | ✔ | — | `200 PayloadDiffResponse` | `404` |
| `GET /payloads/{id}/drift-status` | ✔ | — (opt. `?threshold=N`) | `200 PayloadDriftStatus` | `404` |
| `PUT /browsers/{name}` | ✔ | `RegisterBrowserRequest` | `200 BrowserSummary` | `400` |
| `GET /browsers` | ✔ | — | `200 BrowserListResponse` | — |
| `DELETE /browsers/{name}` | ✔ | — | `204` | `404` |
| `POST /fixtures/{name}/record` | ✔ | `RecordFixtureRequest` | `200 RecordFixtureResponse` | `400` |
| `GET /fixtures` | ✔ | — | `200 FixtureListResponse` | — |
| `GET /fixtures/{name}` | ✔ | — | `200 FixtureDetailResponse` | `404` |
| `DELETE /fixtures/{name}` | ✔ | — | `204` | `404` |
| `PUT /webhooks/{name}` | ✔ | `RegisterWebhookRequest` | `200 WebhookSummary` | `400` |
| `GET /webhooks` | ✔ | — | `200 WebhookListResponse` | — |
| `DELETE /webhooks/{name}` | ✔ | — | `204` | `404` |
| `GET /webhooks/{name}/deliveries` | ✔ | — (opt. `?limit=N`) | `200 WebhookDeliveryResponse` | `404` |
| `GET /runs` | ✔ | — (filters + `?page`/`?size`) | `200 RunListResponse` | `400 invalid_status` / `400 invalid_payload_id` |
| `GET /tenant` | ✔ | — | `200 TenantProfileResponse` | — |
| `GET /usage` | ✔ | — | `200 UsageResponse` | — |
| `GET /health` | — | — | `200` | — |
| `GET /schema/crawldad-1.schema.json` | — | — | `200 application/schema+json` | — |
| `GET /llms.txt` | — | — | `200 text/plain` | — |
| `GET /openapi.json` | — | — | `200 application/json` | — |

`✔` = requires authentication ([§1](#1-authentication)). All `2xx` bodies are JSON except SSE (`text/event-stream`),
the schema (`application/schema+json`), and `llms.txt` (`text/plain`).

## 20. Management API — tenants & API keys — `/management`

> **Interim, server-side surface.** These endpoints administer the DB-backed **tenant registry** — creating tenants and
> issuing/revoking the API keys tenants authenticate with. They are consumed **server-side** (by the future portal), not
> by tenant clients, and they authenticate with a **single management key**, not a tenant API key. This is a deliberate
> stop-gap until portal operator auth matures; the surface is intentionally **not** part of the OpenAPI envelope ([§17](#17-served-docs--health))
> or the [§19](#19-endpoint-quick-reference) table, which describe the tenant-facing API only.

### 20.1 Enabling & authentication

The management surface is **disabled by default**. It is enabled only when a management key is configured:

```
Management__ApiKey = <a long, high-entropy secret>
```

- **Disabled ⇒ 404.** With no key configured, the `/management/*` routes are never mapped — every request is a plain
  `404`, indistinguishable from any other unmatched path. There is no half-open state.
- **Enabled.** Present the key as `Authorization: Bearer <management-key>`. It is compared in **constant time** (both
  sides hashed to a fixed-length digest first, so the compare leaks neither length nor how much matched). A missing or
  wrong key is `401` with no body — it never reveals whether a tenant or route exists.

The management key is an **operator credential** with full authority over the registry (it can mint a key for any
tenant). Treat it like a root secret: inject it from your secret store, rotate it out of band, never place it in a
tenant-reachable config. It is compared, never logged.

### 20.2 The tenant registry & key model

- A **tenant** is `{ id, displayName, actor, status (active|suspended), tier, slotAllowance }`. The `id` is a lowercase
  slug (letters, digits, hyphens; no `:`), and becomes the Marten tenant partition and billing subject — the same
  identity a configured `Crawldad:Tenants` tenant has. `actor` defaults to `id`; `slotAllowance` (nullable) is the
  per-tenant concurrent-run override that flows into the admission cap ([§4](#4-the-three-run-shapes-sync-async-queued)),
  exactly as a configured tenant's override does — a registry tenant with `slotAllowance: 5` is capped at 5 concurrent
  runs, and a null defers to the global default. The cap is resolved from the registry on **every** admission (the
  immediate start **and** the background queue-promotion paths), so it holds for long-running and post-restart runs, not
  only while a recent auth is cached. Note the concurrent-run cap is the **only** per-tenant plan knob the registry carries
  today: the queue-**depth** override (the at-cap wait room) has no registry field yet, so a registry tenant uses the
  global default depth; the per-tenant depth override remains a `Crawldad:Tenants` (env) knob for now.
- **API keys are `ck_<env>_<random>`** — 256 bits of CSPRNG entropy. The **raw key is returned exactly once**, at issue
  time, and is **never stored**: only its SHA-256 (plus a short, non-secret display `prefix`) is persisted. A tenant may
  hold many keys (rotation); each carries `createdAt`, best-effort `lastUsedAt`, and `revokedAt`.
- **Resolution & fallback.** A presented key is resolved against the registry first (behind a short-TTL, revocation-safe
  cache) and, when it matches no registry key, against the env-configured `Crawldad:Tenants` — so existing staging/beta
  wiring keeps working unchanged. **Revoking a key** or **suspending a tenant** takes effect immediately on the serving
  instance (the cache is invalidated in-process) and within the cache TTL elsewhere. A **suspended** tenant's keys are
  rejected exactly like an unknown key (`401`, no existence oracle).

### 20.3 Endpoints

| Method + route | Body | Success | Errors |
|---|---|---|---|
| `POST /management/tenants` | `{ id, displayName, actor?, tier?, slotAllowance? }` | `201` tenant | `400` invalid field, `409 tenant_exists` |
| `GET /management/tenants/{id}` | — | `200` tenant | `404 tenant_not_found` |
| `POST /management/tenants/{id}/suspend` | — | `200` tenant (`status: "suspended"`) | `404 tenant_not_found` |
| `POST /management/tenants/{id}/reactivate` | — | `200` tenant (`status: "active"`) | `404 tenant_not_found` |
| `POST /management/tenants/{id}/keys` | — | `201 { keyId, prefix, apiKey, createdAt }` | `404 tenant_not_found` |
| `GET /management/tenants/{id}/keys` | — | `200 { keys: [{ keyId, prefix, createdAt, lastUsedAt, revokedAt, active }] }` | `404 tenant_not_found` |
| `DELETE /management/tenants/{id}/keys/{keyId}` | — | `204` | `404 key_not_found` |

All requests require the management bearer key ([§20.1](#201-enabling--authentication)); absent/wrong ⇒ `401`. The
issue-key `201` body is the **only** time the raw `apiKey` is returned — store it immediately. The list endpoint returns
**prefixes only**, never a raw key or its hash. Revoke is **idempotent**: a second revoke of the same key, a key that
belongs to another tenant, or an unknown key id is `404 key_not_found`.

### 20.4 Issue → use → rotate

```bash
# Create a tenant (management key)
curl -sX POST https://api.example.com/management/tenants \
  -H "Authorization: Bearer $MANAGEMENT_KEY" -H 'Content-Type: application/json' \
  -d '{"id":"acme","displayName":"Acme Corp","tier":"pro","slotAllowance":12}'

# Issue a key — the raw key is in this response and nowhere else
curl -sX POST https://api.example.com/management/tenants/acme/keys \
  -H "Authorization: Bearer $MANAGEMENT_KEY"
# → 201 { "keyId":"…", "prefix":"ck_prod_A1b2C3", "apiKey":"ck_prod_A1b2C3…<secret>", "createdAt":"…" }

# The tenant now authenticates the normal API with that key
curl -s https://api.example.com/payloads -H "Authorization: Bearer ck_prod_A1b2C3…<secret>"

# Rotate: issue a new key, cut over, then revoke the old one (takes effect immediately)
curl -sX DELETE https://api.example.com/management/tenants/acme/keys/<oldKeyId> \
  -H "Authorization: Bearer $MANAGEMENT_KEY"   # → 204
```
---

## 21. Dashboard read APIs — runs list, webhook deliveries, tenant & usage

Four read surfaces that back the portal dashboard (issue #119). All are tenant-scoped and require auth; all are
computed on read from existing state — no new event shapes, no heavy projections.

### `GET /runs` — list runs

A filterable, offset-paginated list of the tenant's runs, **newest first** (by `startedAt`, run id as the stable
tiebreaker). It reads a lightweight `RunSummary` listing projection — list-view fields only; the full result, timeline,
and drift stay on the per-run surfaces ([§5](#5-polling--get-runsid), [§9](#9-drift-timeline-screenshots--erasure)).

Query parameters (all optional, AND-combined):

| Param | Meaning |
|---|---|
| `status` | one of `running` / `queued` / `succeeded` / `failed` / `cancelled`. An unknown value → `400 invalid_status` (a name, never an ordinal — `?status=3` is rejected). |
| `payloadId` | a managed payload UUID. A malformed value → `400 invalid_payload_id`. |
| `from`, `to` | inclusive ISO-8601 bounds on `startedAt`. An unparseable bound is ignored (unbounded), never a `400`. |
| `page` | 1-based page number (default 1; a stray value floors at 1). |
| `size` | page size (default 25, clamped to 1..100; a stray value falls back to the default). |

Response (`RunListResponse`): `runs[]`, plus `page`, `size`, `total` (the count across the whole filtered set, not just
this page), and `hasMore`. Each row (`RunListItem`):

```jsonc
{
  "runId": "5d1e…",
  "status": "succeeded",
  "startedAt": "2026-08-06T12:01:00+00:00",
  "durationMs": 1500,           // terminal-only
  "payloadName": "permits.search",
  "payloadId": "9a3c…",         // omitted for an inline run
  "payloadRevision": 3,         // omitted for an inline run
  "inline": false,              // true → the run was launched from an inline payload document
  "region": "us-east",          // omitted before a backend session opened
  "stats": { "steps": 5, "requests": 12, "selectorMisses": 2 }, // terminal-only
  "failure": { "class": "terminal", "code": "nav_failed" }      // only when status = failed
}
```

A running/queued row omits the terminal-only fields (`durationMs`, `stats`, `failure`) and `region`. (In production the
`RunSummary` projection is async and folds forward for new runs; a projection rebuild backfills history.)

### `GET /webhooks/{name}/deliveries` — delivery history

The recent delivery attempts for one endpoint, **newest first**. Each attempt — including a retry of the same event — is
a distinct row, so a receiver's flakiness reads as its retry ladder. The log is a **rolling window**: the latest **N per
endpoint** (`Crawldad:Webhooks:DeliveryHistory:MaxPerEndpoint`, default **50**) are kept and older rows are pruned as new
ones land — bounded storage, not an audit ledger. An optional `?limit=N` narrows the page (clamped to 1..the cap). An
unknown or foreign endpoint name is a `404`.

Response (`WebhookDeliveryResponse`): `deliveries[]`, each:

```jsonc
{
  "runId": "5d1e…",
  "eventType": "run.succeeded",
  "attempt": 1,                 // 1-based; a retried delivery shows attempt 1, 2, …
  "delivered": true,            // the receiver returned 2xx
  "statusCode": 200,            // omitted on a transport failure (connection error / timeout — no response)
  "latencyMs": 42,
  "at": "2026-08-06T12:00:00+00:00"
}
```

The **`GET /webhooks`** listing ([§14](#14-webhooks--webhooks)) carries the same outcome as an additive `lastDelivery`
on each row — the endpoint's most recent attempt — omitted for an endpoint that has never been delivered to.

### `GET /tenant` — the authenticated tenant's profile

```jsonc
{
  "tenantId": "tenant-alpha",
  "displayName": "alpha@crawldad.test",  // the configured actor identity
  "tier": "pro",                          // optional pricing-tier label; omitted when unset
  "slotAllowance": 5,                     // concurrent-run cap: the per-tenant override, else the global default
  "queueDepthAllowance": 20               // admission-queue depth: the per-tenant override, else the global default
}
```

Read-only, resolved from the bound tenant options; there is deliberately no tenant-management endpoint. If a tenant
registry lands later it can back this same shape without a wire change — this surface does **not** depend on one.

### `GET /usage` — usage against guardrails

Live capacity and consumption, computed on read. Pragmatic and **approximate by design** — a point-in-time occupancy and
a recent-window sample, not a billing ledger.

```jsonc
{
  "slots":   { "inUse": 2, "allowance": 5 },                    // slot occupancy now (admission gate) vs the cap
  "queue":   { "depth": 0, "sampled": 37, "p95WaitMs": 1200 }, // the same reading as GET /runs/queue-stats (§10)
  "runsStartedThisMonth": 412,                                 // this calendar month (UTC)
  "events":  { "guardrail": 5000, "sampled": 100, "avg": 84, "max": 611 } // events-per-run over a recent window
}
```

- `slots.inUse` is a per-process, point-in-time count from the admission gate (§4); `allowance` is the same cap as
  `GET /tenant`'s `slotAllowance`.
- `queue` reuses the queue-stats machinery: `depth` counts waiting runs, `p95WaitMs` is the nearest-rank p95 of the
  recorded per-run queue waits, and `sampled` is how many waits it was computed over.
- `events` compares the mean/peak event count over the most recent runs (a bounded window) against the
  `max-events-per-run` guardrail ([§15.4](#154-server-limits-and-their-config-knobs)) — headroom before a run trips the cap.

### Run-detail shape notes (design clarifications, issue #119)

These document the **existing** truth — no response shapes changed:

- **Failure screenshot/capture refs are timeline-only.** `RunResponse.failure` (`POST /runs`) and `RunStateResponse.failure`
  (`GET /runs/{id}`, [§5](#5-polling--get-runsid)) are a `RunFailureDetail` — `class`, `code`, `message`, `atStep` — and
  carry **no** `screenshotRef`/`captureRef`. The failing page's artifact refs ride `RunTimelineFailure` on
  `GET /runs/{id}/timeline` ([§9](#9-drift-timeline-screenshots--erasure)). So run-detail is one call for the typed
  failure; to show the failing page, make a second call to the timeline for its `screenshotRef`/`captureRef`, then
  `GET /runs/{id}/screenshots/{ref}` (or fetch the capture from your BYO storage).
- **Queue position has no SSE frame.** A queued run's 1-based `position` is surfaced by polling `GET /runs/{id}`
  ([§5](#5-polling--get-runsid)), recomputed on read; there is no `QueuePosition` SSE event. The SSE stream
  ([§6](#6-streaming-trace-sse--get-runsidevents)) is the run's execution trace, which only begins once the run leaves the
  queue. (No new SSE frame is introduced here.)
- **A running run's poll body is `{ runId, status }`.** Partial `stats` are **not** streamed mid-run — `stats` (and
  `result`/`failure`) appear only at a terminal status. The runs list mirrors this: a running/queued `RunListItem` omits
  `stats`. The live SSE trace ([§6](#6-streaming-trace-sse--get-runsidevents)) is the mid-run signal.

## 22. Billing — `/billing` (Stripe, scaffolding)

Billing is sold on the slot-priced tiers in [`BUSINESS_MODEL.md`](BUSINESS_MODEL.md) — **Free** (2 slots), **Team**
($99/mo · 10 slots), **Scale** ($499/mo · 50 slots), **Enterprise** (custom). Purchase runs through **Stripe Checkout**
and plan management through the **Stripe hosted Billing Portal**; the portal/UI never holds a Stripe secret — it only
follows a URL the API mints, and a tenant can never change its own plan through the API (see below).

> **Scaffolding status.** The Stripe SDK is **not wired yet**. In Development/tests an in-process **fake gateway** drives
> these endpoints end to end; in Production a **fail-closed stub** answers — session calls return `503 billing_not_configured`
> (a friendly "not yet available", never a 500) and webhooks are rejected — until `Billing:Stripe:SecretKey` and
> `Billing:Stripe:WebhookSecret` are set and the SDK integration lands.

### `GET /billing/config` — billing state & tier catalog

Tenant-authed. Whether billing is configured, the tenant's current tier, and the tier catalog to render a plan card —
so the portal never duplicates the pricing numbers. `200 BillingConfigResponse`:

```jsonc
{
  "configured": true,                 // is the provider wired (false → portal shows "not yet available")
  "currentTier": "team",              // the tenant's current tier moniker (omitted when none)
  "tiers": [
    { "tier": "free",  "displayName": "Free",  "priceLabel": "$0",     "slots": 2,  "selfServe": false, "isCurrent": false },
    { "tier": "team",  "displayName": "Team",  "priceLabel": "$99/mo", "slots": 10, "selfServe": true,  "isCurrent": true  },
    { "tier": "scale", "displayName": "Scale", "priceLabel": "$499/mo","slots": 50, "selfServe": true,  "isCurrent": false },
    { "tier": "enterprise", "displayName": "Enterprise", "priceLabel": "Custom",    "selfServe": false, "isCurrent": false }
  ]
}
```

`slots` is omitted for a custom/committed tier; `selfServe: false` (Free, Enterprise) renders "contact sales" rather than
a checkout button. The catalog defaults come from `BUSINESS_MODEL.md`; a deployment overrides `Billing:Tiers` to set live
Stripe price ids.

### `POST /billing/checkout-session` — open Checkout for a tier

Tenant-authed. Body `{ "tier": "team" }` (a self-serve tier from the catalog). Returns `200 { "url": "…" }` — the hosted
Checkout URL to redirect the browser to. It **only returns a URL**: it does **not** change the tenant's plan, so a tenant
cannot raise its own slot allowance by calling this. An unknown or non-self-serve tier is `400 unknown_tier`; an
unconfigured provider is `503 billing_not_configured`.

### `POST /billing/portal-session` — open the Billing Portal

Tenant-authed, no body. Returns `200 { "url": "…" }` — the hosted Billing-Portal URL (manage payment method, invoices,
plan). `503 billing_not_configured` when unconfigured.

### `POST /billing/webhook` — inbound subscription events (public, signature-verified)

The **only** path that changes a tenant's plan. **Anonymous** (Stripe is not a tenant), authenticated instead by the
event **signature** in the `Stripe-Signature` header — verified **before** the body is parsed. On a
`customer.subscription.created` / `.updated` / `.deleted`, the subscription's tenant id (from provider metadata — this
tenant is **authoritative**, never a caller claim) and price id map to a tier, and the tenant's `Tier` + `SlotAllowance`
are updated via the registry (§20). Outcomes:

- **bad/absent signature or unparseable body →** `400 invalid_webhook`, nothing changed.
- **replayed event id →** `200`, no-op (processed event ids are de-duplicated; anti-replay).
- **unknown tenant, or a tenant only in the env config (not the registry) →** `200`, logged and dropped (env-fallback
  tenants are read-only for billing).
- **price mapping to no known tier →** `200`, logged and dropped.
- **applied →** `200`; the new slot allowance takes effect immediately (the admission gate's per-tenant override is
  invalidated in-process).

No secret is ever logged, and neither the raw body nor the signature is logged.
