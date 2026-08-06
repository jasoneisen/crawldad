# Crawldad — Phased Implementation Plan

> Companion to `CRAWLDAD_DESIGN.md`. Section refs (§) point there.
> Format (goal-driven): each phase states a **Goal**, **Why here** (ordering rationale),
> **Delivers**, **Success criteria** (observable/testable — the gate), and **De-risks**.
> Principle: surface the highest-risk unknowns first — the **expression language** and the
> **callback/streaming** decision — not CRUD. Phase 1 is a *runnable vertical slice*, not a schema.

---

## Ordering rationale (read first)

The two things that can sink this design are named in the brief: (2) the **expression-language
boundary** (too little ⇒ LJCMG inexpressible; too much ⇒ reinvented JavaScript, safety claim lost) and
(1) the **mid-execution callback** reshaping (a one-shot API cannot call back). Everything else —
persistence, versioning, observability UI, real backend adapters — is comparatively well-understood
Critter-Stack CRUD + IO and is deliberately **later**. So:

- **P1** proves the interpreter + expression language + Playwright mapping on a *real* (small) LJCMG
  fragment, end-to-end and runnable, against a fake backend. Riskiest core, smallest slice.
- **P2** drives the expression language to its **hardest** LJCMG cases and makes the **callback/
  streaming decision real** (declarative `knownUrls` stop + download-to-`Target` + the retryable/
  terminal taxonomy). If the language or the reshaping is wrong, we learn here, cheaply.
- **P3** completes both operations and turns the LJCMG payloads into the **acceptance suite** —
  meeting the MVP criterion against captured fixtures.
- **P4** swaps the fake for **real backends** and adds the **security boundary** (credential
  scrubbing), validating against a local fixture site, then a rate-limited live canary.
- **P5** adds the **managed-payload product surface** (versioning/drift, SSE progress, replay,
  cancellation, checkpoint-resume) — the paid features, on the now-proven engine.

The acceptance criterion (identical output to the C# scraper) is **met at P3** against fixtures and
**re-confirmed at P4** against a real browser. P5 is product, not correctness.

---

## Phase 1 — Runnable vertical slice through the riskiest core

**Goal.** A minimal Crawldad host that executes a real LJCMG *fragment* payload end-to-end against a
`FakeBrowserBackend` and returns a caller-shaped object — exercising the interpreter, the expression
language, the selector model, the Playwright-action mapping, and `result` shaping in one slice.

**Why here.** This is the smallest slice that touches the core thesis and the #2 risk. The chosen
fragment — the search **date-fill + first-page row extraction** (`LJCMGClient.cs:95-140`) — already
needs conditional fill (`if`), the row-range loop (`for i=3 .. count-2`), relative selectors, `text`/
`attr`/`trim`/`coalesce`, `waitForRequest` around a click, `push`, and a `result` object. If this is
awkward, the design needs another pass now, not at P3.

**Delivers.**
- Critter-Stack host skeleton (combined `Crawldad.Web`, `HostConfiguration`, config-driven projection
  lifecycle, Wolverine+Marten wiring, JasperFx CLI) — the §14 layout, Payloads/Runs slices stubbed.
- Interpreter v0: node dispatch for `goto, waitForLoadState, waitForRequest, waitFor, click, fill,
  clear, locate, set, push, if, loop(for), forEach, break, continue`; the expression evaluator
  (grammar §7.1 + the string/collection/url/DOM builtins the fragment uses); `Sel` resolution incl.
  relative/`nth`; `result` evaluation.
- `IBrowserBackend` + `FakeBrowserBackend` seeded from a **captured** CapHome results page (record/
  replay fixture).
- `POST /runs` (synchronous) → executes an inline payload → returns `{status, result, stats}` (§10);
  a `Run` stream with coarse events persisted.

**Success criteria (gate).**
- `dotnet run --project src/Crawldad.Web` serves; `POST /runs` with the fragment payload returns a
  JSON `result` whose rows equal the C# `EnforcementSearchResponse.Results` for the same captured page
  (byte-compare against a golden captured from the reference).
- `dotnet build -warnaserror` and `dotnet test` are green; coverage gate satisfied for shipped code.
- An Alba integration test drives the fragment through the real HTTP endpoint against the fake backend
  and asserts the shaped output + the persisted `Run` events (Wolverine tracked session).

**De-risks.** Expression language viability, node interpreter, selector/locator model, result shaping,
the fake-backend test seam — the entire thesis, on a real fragment.

---

## Phase 2 — Expression language to its hardest + the callback/streaming decision

**Goal.** Prove the expression language on LJCMG's genuinely hard fragments, and make the tension-#1
reshaping concrete: declarative early-termination, downloads-to-`Target`, and the retryable/terminal
error taxonomy.

**Why here.** These are the two risks the brief calls out. Both are cheap to falsify now and expensive
later. The hard fragments are the true test of the §7 boundary.

**Delivers.**
- Expression completions: `switch`, `filter/map/any/all/min/max/sortBy/keys/get`, `matches`/regex,
  `substring/substringAfterLast`, computed-key `set` paths (`parents[${indent}]`), `loop(while)` do-
  while, `guard`/`fail`, `log`, template `${}` interpolation in selectors.
- The four hardest fragments, each against captured DOM: **related-records parent resolution**
  (`:625-697`), **nested `k*2+1/k*2+2` violations** (`:359-425`), **processing-status split chains**
  (`:455-529`), **3/4/5 `<br>` address branch** (`:229-268`).
- Tension #1 realized: `knownUrls` + `priorCrawlComplete` inputs and the declarative early-termination
  (Appendix B.1); `download` action → `IDownloadSink`/`Target` with engine-native content hash
  (`= AttachmentHashing`) and idempotency; the retry/timeout config and the retryable-vs-terminal
  classifier (§8.3), including the §3.6 page-crash reopen-and-rebind fix.

**Success criteria (gate).**
- Each hard fragment produces output **byte-identical** to the C# for a captured record (golden
  compare) — in particular the related-records `parentRecordNumber` resolves correctly for a
  multi-level tree.
- Full `SearchEnforcementRecords` (B.1) over a captured **multi-page** search reproduces the exact
  `newLinks` list and `crawledToEnd` flag for the known-URL early-termination cases (including the
  `!crawledToEnd` nuance), matching the C# `HistoricalCrawler` outputs.
- A `download` over a captured file yields the correct `contentId` (SHA-256 first-16-bytes GUID) and
  `internalFilename` (`{guid}.{ext}`) and streams to the fake sink; an already-present blob short-
  circuits to `stored:true`.
- A `guard`/terminal failure is **not** retried; an injected `timeout` **is** retried per policy; a
  `pageCrashed` reopens the page and continues on the same context (asserted via the fake backend).
- No expression can be authored that loops, recurses, calls `fs`, or `eval`s (negative tests: parser
  rejects unknown builtins/head keys; every `loop` requires `maxIterations`).

**De-risks.** The §7 boundary (is it exactly LJCMG-sized?), the callback reshaping, downloads without
byte round-trips, and the error taxonomy that prevents the 30-min retry burn.

---

## Phase 3 — Full LJCMG acceptance against fixtures (MVP criterion met)

**Goal.** Complete both operations and turn them into the acceptance suite; meet the MVP acceptance
criterion against captured golden records.

**Why here.** With the language and reshaping proven, the remainder is assembling the full programs —
notably the attachments iframe loop with its safety cap — and wiring the golden comparison.

**Delivers.**
- Complete `ScrapeEnforcementRecord` (Appendix B.2): iframe `frame` handles, the capped attachment
  pagination (`maxIterations:50, onMaxIterations:"warn"`), the computed page-number `waitFor`, all
  regions, and the full `RecordScrapedV1` `result`.
- The two LJCMG payloads committed as the **acceptance suite** with a golden-corpus harness: capture a
  set of real records/searches once; store the reference C# output as golden JSON; assert Crawldad
  output equals golden.
- Payload **validation at save** (JSON Schema + semantic pass: defined-before-use, `maxIterations`
  present, expression parse/arity) so malformed payloads never execute (§12).

**Success criteria (gate).**
- For **N ≥ 10** golden enforcement records spanning the branch variety (3/4/5-`<br>` addresses,
  0/1/many owners, violations present/absent, related-record trees, multi-page attachments, and at
  least one CapDetail-guard redirect and one unknown-heading terminal), `ScrapeEnforcementRecord`
  output **equals** `RecordScrapedV1` golden — field-for-field, list-order included.
- For **M ≥ 5** golden searches (single page, multi-page, known-URL early stop, empty results),
  `SearchEnforcementRecords` output matches golden.
- Terminal cases produce the correct `{status:"failed", failure.class:"terminal", code}` and are not
  retried; the attachment cap produces a warning event and a complete record.
- Full green build + coverage gate; the acceptance suite runs in CI with **no live traffic**.

**De-risks.** Whole-program fidelity, the iframe + capped-loop mechanics, and the "identical output"
claim itself — this is the MVP acceptance criterion, satisfied.

---

## Phase 4 — Real backends + the security boundary

**Goal.** Execute the acceptance payloads through **real Chromium** via the backend adapters, and
enforce credential handling/scrubbing from day one.

**Why here.** Correctness is proven against fixtures; now validate that the same payloads behave
identically through a real browser and real remote backends, and that secrets never leak.

**Delivers.**
- `BrowserlessBackend` (native `/chromium/playwright` via `chromium.connect`, preferred) and
  `BrowserbaseBackend` (CDP `connectOverCDP`), both credential modes for Browserbase (`apiKey` and
  `connectUrl`); `backendOptions` passthrough; region tag (§9).
- `ISecretStore` (credential-by-reference) + the **scrubbing filter** at the logging/event/trace sink
  (strips `?apiKey=`/`?token=`/`connectUrl`), applied to logs, events, projections, and SSE (§12).
- The route/cache/throttle/launch/context policy (§8.1) applied on top of the backend context (post-
  connect `RouteAsync` for CDP), reproducing `PlaywrightFactory` against a live browser.
- A **local static fixture site**: the captured Accela pages served over HTTP, driven by real Chromium
  (Browserless pointed at the local origin, or a local Playwright server) — real-browser fidelity with
  **zero live third-party traffic**.

**Success criteria (gate).**
- Both acceptance payloads run against the **local fixture site via real Chromium** and produce output
  equal to the P3 golden (proves the fake and the real engine agree).
- **Scrubbing test:** a run whose backend binding carries a token/`connectUrl` asserts that string
  appears in **no** event, projection row, log line, SSE frame, or trace artifact.
- One **live canary**: a single real enforcement record scraped against the live Accela portal behind
  a feature flag, honoring the global 2 s throttle and host/resource blocking, produces a valid
  `RecordScrapedV1`; the canary is nightly/manual, never in the fast test loop.
- Browserbase `connectUrl`-format re-check completed (§3.5/§9 ship-blocker) and the security copy
  reflects "not safe to leak."

**De-risks.** Real-backend parity, protocol asymmetry (native vs CDP), the credential blast-radius
correction, and "test a live third-party site without hammering it" (fixture-first; canary rare).

---

## Phase 5 — Managed-payload product surface (the paid features)

**Goal.** Add the features recurring revenue attaches to — payload versioning/drift, run
observability/streaming/replay, cancellation, and checkpoint resumability — on the proven engine.

**Why here.** These are additive product surface, not correctness; they should sit on an engine that
already reproduces the reference exactly.

**Delivers.**
- **Payload aggregate** (`PayloadDrafted/Revised/Renamed/Archived`), `PayloadSummary` projection,
  draft/revise/list/diff endpoints; runs **pin** the payload revision + script hash; **drift** =
  pinned-vs-head (§14.1).
- **Run observability**: `RunTimeline` async projection (step list, durations, redacted inputs,
  extracted-value/blob refs, screenshot-on-failure), a **replay** view that re-executes a pinned
  revision.
- **Long-running execution**: the `RunExecutorSaga` (Wolverine, Marten-backed) with wall-clock + per-
  step saga timeouts; **SSE** progress (durable-stream backfill + live tail); `CancelRun`
  (cooperative between-steps teardown → `partial`); **checkpoint resume** for the two LJCMG loops
  (search page cursor, attachment page cursor) surviving a process restart (§11).

**Success criteria (gate).**
- Revising a payload yields a diffable new revision; a historical run still reports its pinned
  revision and re-runs against it; moving the head is reported as drift.
- A long `SearchEnforcementRecords` run streams ordered step events over SSE; killing the host mid-run
  and restarting **resumes from the last checkpoint** (not from step 0, not by event-replay into a
  browser) and completes with the same `result`.
- `CancelRun` mid-run tears down the backend session and returns `{status:"cancelled", partial}`; no
  orphaned browser session remains (asserted via the fake backend's close-tracking).
- Observability contains **no** raw credentials or bulk PII (events store refs/metadata only;
  screenshots/extracted PII in deletable blob storage) — re-assert the §12 scrubbing invariants.

**De-risks.** The saga/streaming/versioning infrastructure the foundation repo lacks, and the
resumability deviation (record ≠ execution; checkpoints, not replay).

---

## Testing strategy

**The LJCMG payloads are the acceptance suite** (P3 gate, re-run every phase from P3 on).

- **Golden corpus (primary).** Capture a representative set of real Accela records/searches **once**
  into fixtures (raw DOM/network for `FakeBrowserBackend`) and store the **reference C# output** as
  golden JSON. Every acceptance run compares Crawldad output to golden, field-for-field. Cover the
  branch variety explicitly (P3 success criteria).
- **`FakeBrowserBackend` (record/replay).** The engine depends only on `IBrowserBackend`; the fake
  returns scripted DOM/step outcomes (and can inject `timeout`/`pageCrashed`/failures + screenshots).
  This exercises the whole engine — interpreter, events, projections, SSE, cancellation, retry — with
  **no Chromium**, deterministically, in CI. Follows the foundation's `IEmailGateway`/`FakeEmailGateway`
  seam, Alba host reuse, DI overrides, Wolverine tracked sessions, and the **100 % line+branch
  coverage** gate.
- **Real-browser fidelity without hammering (P4).** Serve the captured pages from a **local static
  fixture site** and drive them with **real Chromium** via the adapters — real-browser behavior, zero
  third-party traffic. Assert equality to the P3 golden (fake ≡ real).
- **Live third-party site — rare, gentle, gated.** A **nightly/manual canary** scrapes a single live
  record behind a flag, honoring the global **2 s throttle**, the host/resource blocklist, and the
  cross-session asset cache (all of which reduce load); it alerts on drift and never runs in the fast
  loop. This is how we watch for real-site drift without load — the moat signal, safely. Respect
  robots/ToS; keep concurrency at 1 for the target.
- **Property/negative tests for the language boundary.** The parser rejects unknown builtins/head
  keys, unbounded loops (missing `maxIterations`), and use-before-define; fuzz expressions for
  termination and regex-time guards. Scrubbing has dedicated leak tests (P4/P5).
- **Unit layers** (no host): expression evaluator, selector resolver, content-hash identity,
  validators — fast, exhaustive on the tricky builtins (Appendix C).

---

## Explicitly NOT in the MVP

- **Proxy layer, browser fleet, billing, marketing site** — out of scope by the brief.
- **`evaluate` / arbitrary JS** — tension #3: never shipped in v1 (forfeits the safety thesis and the
  moat; the reference needs zero `evaluate`).
- **`@puppeteer/replay` importer** — designed (§16) as a post-MVP authoring on-ramp; not built for MVP.
- **Webhooks** — the `SendWebhook`/`ISideEffect` shape is designed but not built; the LJCMG host uses
  inputs + storage targets, not webhooks. Add when a workload needs it.
- **General (non-checkpoint) resumability and a streaming control-channel for arbitrary dynamic host
  logic** — v1 ships declarative inputs + checkpoints for the two LJCMG loops; the fully-dynamic
  streaming stop path is post-MVP.
- **Self-serve payload-authoring UI / multi-tenant onboarding beyond what observability needs** — the
  API + the observability read models are the v1 surface.
- **Auth/authz build-out** — the boundary is designed (tenant-authenticated, actor from principal,
  §12) but a full identity system is not MVP; it must exist before any real customer data.
- **Non-CDP exotic backends, multi-region interpreter placement, and the `emulationOs` passthrough**
  (unverified) — the design accounts for region-matching and the three named backends; the rest waits.
- **XPath exercised** — supported in `Sel`, but the acceptance suite is CSS + title only; XPath is
  carried, not gated on.

---

## Risks & mitigations (cross-cutting)

| Risk | Mitigation |
|---|---|
| Expression language can't express a hidden LJCMG case | Front-loaded to P1–P2 against the hardest fragments; Appendix C enumerates the exact surface; boundary is falsifiable early |
| "Identical output" is stricter than the fake can prove | P4 re-runs the same payloads through **real Chromium** against a local fixture; fake≡real is a gate |
| Credentials leak into paid run traces | Scrubbing built at the sink in P4, dedicated leak tests, credential-by-reference from the start |
| Resumability misunderstood as event-replay | Explicit deviation (§11): record ≠ execution; checkpoint-based resume, tested by kill-and-restart in P5 |
| Target-site drift breaks the scraper silently | Drift is a product feature: pinned-revision compare + the live canary alert (P5) |
| Browserbase `connectUrl` credential blast radius mis-stated | §3.5 correction; ship-blocker primary re-check in P4 before security copy |
