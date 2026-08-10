# Crawldad acceptance suite (Phase 3 MVP gate)

**The two LJCMG payloads _are_ the acceptance suite.** The MVP
acceptance criterion — *Crawldad output identical to the C# reference
(`mrr.gg/src/US.KY.Jefferson.LJCMG.Worker`)* — is met here against captured-shape golden fixtures,
with **no Chromium and no live traffic**, and re-confirmed against real Chromium in Phase 4.

- **Payloads** (`tests/Crawldad.Tests/Fixtures/Payloads/`): `scrape-full.json` (Appendix B.2,
  `ScrapeEnforcementRecord` → `RecordScrapedV1`) and `search-full.json` (Appendix B.1,
  `SearchEnforcementRecords` → `{ newLinks, crawledToEnd, pages }`). Both are save-validated.
- **Suites**: `Integration/ScrapeRecordAcceptanceTests.cs` (record side) and
  `Integration/SearchAcceptanceTests.cs` (search side). Each drives `POST /runs` and asserts the
  shaped `result` is **byte-identical** to a golden (`JsonAssert.DeepEquals` + canonical byte compare),
  plus event-stream / warning / terminal assertions.
- **Backend**: the record/replay `FakeBrowserBackend`, driven by fixture directories under
  `src/Crawldad.Web/Fixtures/` (each has `manifest.json`, HTML, `golden.json`, `FIXTURE_NOTES.md`).

## How goldens were derived (no live traffic, no engine-copy)
Every `golden.json` is **hand-derived by executing the C# reference algorithm** over the fixture DOM —
never copied from Crawldad's own output — and each fixture's `FIXTURE_NOTES.md` shows the derivation
citing the reference line numbers (`LJCMGClient.cs`, `HistoricalCrawler.cs`). The fixtures are
**synthesized** (faithful to the shapes the reference iterates), not captured; real-Chromium parity
against a local fixture site is the Phase 4 gate (fake ≡ real).

## Record corpus — `ScrapeRecordAcceptanceTests` (12 fixtures)
`Scrape_record_output_equals_golden` is a `[Theory]` over the 10 succeeding records; the 2 terminal
records are asserted by dedicated facts. Together they span the branch variety the plan names.

| Fixture | Covers |
|---|---|
| `record-01-full-suburban` | rich whole-program: 3-`<br>` address, 1 owner, violation, parcel, processing, 1 attachment, related tree |
| `record-02-four-line-owner` | 4-`<br>` address, `1)`-prefixed 4-line owner, `projectName == ""` |
| `record-03-five-line-harbor` | 5-`<br>` address, no status, `projectName == recordType`, 1 attachment |
| `record-04-no-owners` | 0 owners; `"No records found."` attachments row |
| `record-05-many-owners` | many owners (⇒ `MULTIPLE OWNERS` warning), two locations, violation |
| `record-06-multipage-attach` | two attachment pages, a download on each |
| `record-07-related-tree` | non-trivial related-record tree (multi-level parent resolution), `projectName` from Highlight, malformed-row error logs |
| `record-08-attach-cap` | attachment 50-page cap ⇒ warning event **and** a complete record |
| `record-09-guard-redirect` | CapDetail-guard redirect ⇒ terminal `record_not_accessible`, **not retried** |
| `record-10-unknown-heading` | unknown owner heading ⇒ terminal `unknown_heading`, **not retried** |
| `record-11-empty-regions` | all lists empty; no `publishDate` |
| `record-12-owner-empty-block` | owner empty-block skip; header-only attachments grid |

## Search corpus — `SearchAcceptanceTests` (M = 6 searches)
`Search_output_equals_golden` is a `[Theory]` over all six; dedicated facts spotlight the reshaping
nuances. Goldens reproduce the `HistoricalCrawler` callback (:85-104) + the `LJCMGClient` do/while
(:121-167).

| Fixture → golden | Inputs | Covers |
|---|---|---|
| `caphome-search` → `golden-full` | `knownUrls=[]`, prior=false | **single-page** (no pagination link); rich per-cell edges (whitespace/entity/missing-anchor/empty/multi-node) through the full result shape |
| `caphome-multipage` → `golden-a-full` | `knownUrls=[]`, prior=false | **multi-page full crawl**; `crawledToEnd` flips true on the last page (:87) |
| `caphome-multipage` → `golden-b-early-stop` | `knownUrls=[p2-3]`, prior=**true** | **known-URL early stop**: `return !crawledToEnd = false` ⇒ break; page 3 unvisited |
| `caphome-multipage` → `golden-c-continue` | `knownUrls=[p2-3]`, prior=**false** | the **`!crawledToEnd` nuance**: same known url, but continue through to the end |
| `caphome-empty` → `golden` | `knownUrls=[]`, prior=false | **empty results**: `crawledToEnd` true (last page), `newLinks`/`pages` empty |
| `caphome-dedup` → `golden` | `knownUrls=[]`, prior=false | cross-page duplicate url ⇒ `distinct(newLinks)` (= `HashSet<string>`) keeps it once while `pages` keeps both raw rows |

Cases (c) and (d) share the identical known url and differ **only** in `priorCrawlComplete`, isolating
the callback's `return !crawledToEnd` branch — the whole point of the tension-#1 gate.

## Phase 4 WP2 — real-Chromium parity (fake ≡ real)
The Phase 4 core gate re-runs **both payloads through real headless Chromium** and asserts the shaped
result is **byte-identical to the same P3 goldens** — proving the fake and the real engine agree.

- **Suites**: `Integration/RealChromiumScrapeParityTests.cs` and `RealChromiumSearchParityTests.cs`
  mirror the fake acceptance theories **line-for-line**, changing only `backend.adapter` (`fake` →
  `local`). They run through the same `POST /runs` path (`ParityAppFixture`, an ordinary product host
  with the `"local"` adapter overridden). All 10 golden records + 2 terminals + the M = 6 search corpus
  are covered; both terminals (record-09 guard redirect, record-10 unknown heading) run for parity.
- **The local fixture site** (`Support/FixtureSite.cs` + `FixtureChromiumBackend.cs`, test-only, off
  the coverage gate): an in-process origin that serves each fixture's corpus to real Chromium under the
  **canonical Accela origin** via Playwright `route.FulfillAsync`, driven by the **same `manifest.json`**
  the fake uses (`FakeManifest` — the manifests are **not** forked). Documents, frames, and postbacks
  are fulfilled in-process (a canonical `page.Url`, a real POST postback `waitForRequest` observes, the
  record-09 Error.aspx redirect); downloads alone are served from a genuine loopback listener (a
  fulfilled download's bytes read back "canceled"). Manifest transitions fire via a small injected
  script that turns a captured click into the real browser action (form POST to `emit.url`, a
  `Content-Disposition` download, or an in-frame navigation). **Zero live third-party traffic**: the
  route handler default-denies anything but the canonical origin and the loopback listener.

### Fake-vs-real divergences the parity run surfaced (resolved at serve time — the fixture files are untouched, so the fake suite is provably unchanged)
| # | Divergence | Resolution |
|---|---|---|
| 1 | **Missing click targets.** The synthetic minimal CapDetail pages omit `#imgMoreDetail`/`#imgParcel`/the two title tabs the payload clicks unconditionally; the fake no-ops a click on a missing element, real Chromium **auto-waits to a timeout** (→ 15 retries ≈ 30 min). | The fixture site injects a standard no-op tab **scaffold** into record pages that lack it (a real Accela CapDetail page always carries these). |
| 2 | **`getByTitle("Attachments")` strict-mode violation.** Real `getByTitle` is a case-insensitive substring match, so it resolves the tab anchor **and** the `<iframe title="attachments">` → strict-mode throw on click; the fake models it as an exact `[title=…]`. | The iframe `title` is dropped at serve time (the iframe is referenced by id). |
| 3 | **`waitFor` visibility.** The attachment page-number `waitFor` (default state `visible`) is a **no-op in the fake**; real Chromium truly waits. The cyclic 50-page **cap** fixture's frame statically reads page "1", so `waitFor "^${nextPage}$"` would hang forever. | The fixture site renders `SelectedPageButton` to the real pagination position (initial = 1, +1 per in-frame nav) — idempotent for the multi-page fixture whose frames already carry the right numbers. |
| 4 | **iframe content.** The fake serves frame content from the manifest, not from the iframe's `src="about:blank"`. | The iframe `src` is repointed at the frame endpoint at serve time. |
| — | **`FrameLocator` on a missing iframe** returns count 0 (no timeout), and `<a href="#">` clicks are harmless — verified, so records without an attachments iframe need no adaptation. | (no change needed) |

The route policy's **2 s global throttle** is deliberately skipped by the parity route handler (the
legitimate test-time throttle override the plan calls for — the throttle itself is exercised by
`Integration/LocalBackendTests`), so the parity suite stays fast (≈ 30 s for all 26 tests).

## Phase 4 — success criteria → proof

Every Phase 4 gate mapped to the named test(s)/artifact(s) that prove it. Unproven items are flagged as
**GAP**, not papered over.

| # | Acceptance criterion | Proven by | Status |
|---|---|---|---|
| **(a)** | Both acceptance payloads run against the **local fixture site via real Chromium** and equal the P3 golden (fake ≡ real). | `Integration/RealChromiumScrapeParityTests` (10 golden records + 2 terminals, byte-identical to the P3 `RecordScrapedV1` goldens) and `RealChromiumSearchParityTests` (M = 6 searches) — mirror the fake theories line-for-line, changing only `backend.adapter` `fake`→`local`. The route policy (block/cache/throttle) is exercised honestly over real HTTP by `Integration/LocalBackendTests`. Harness: `ParityAppFixture`, `Support/{FixtureChromiumBackend,FixtureSite,RealChromiumFixture}`. | **PROVEN** |
| **(b)** | A run whose binding carries a token/`connectUrl` leaks it into **no** event, projection row, log line, SSE frame, or trace artifact. | `Integration/CredentialLeakTests` (browserless token, browserbase apiKey, failed-connect `connectUrl`, framework-category log) asserts a sentinel appears in no typed event, no **raw** `data::text` from `mt_events`/`mt_doc_run`, no projection/doc row, no captured log line, and not in the response — driving the real WP1 adapters against loopback servers. Reinforced by `Integration/RemoteBackendConnectTests` (scrubbed connect-failure terminals) and units `Unit/{CredentialScrubberTests,RunEventScrubberTests,RunSecretScopeTests,SecretStoreTests}`. Boundary doc: `docs/THREAT_MODEL.md` (per-sink chokepoint table). | **PROVEN** for events/projections/logs/response. **GAP:** the criterion also names **SSE frames** and **trace artifacts** — neither sink exists yet (Phase 5). They are scrubbed *by construction* (both render from already-scrubbed events/projections; `docs/THREAT_MODEL.md` "What is scrubbed, and where"), so there is **no executable SSE/trace leak test until Phase 5 builds those sinks**. |
| **(c)** | One **live canary**: a single real record scraped against the **live** Accela portal behind a feature flag, honoring the 2 s throttle + host/resource blocking, produces a valid `RecordScrapedV1`; nightly/manual, never in the fast loop. | `Integration/LiveCanaryTests` — the gated `[LiveCanaryFact]` (`Category=LiveCanary`) drives `scrape-full.json` **verbatim** through `POST /runs` on the real `"local"` adapter (real Chromium, real network, session policy applied for real by `SessionPolicy.FromConfig`, concurrency 1), asserts `status:"succeeded"`, and validates the `RecordScrapedV1` **shape** (`LiveCanary.AssertValidRecordScrapedV1` — required keys/types + non-empty `recordNumber`/`recordType`, **not** golden equality, because live data drifts). Wiring is proven short of the live hit by `Integration/CanaryWiringTests` (the **identical** helpers against the fixture-site `"local"` adapter, zero live traffic). Scheduled/manual: `.github/workflows/canary.yml`. | **WIRING + SHAPE-GATE PROVEN** (zero live traffic). **GAP by design:** the **actual live-portal hit** is the operator's nightly/manual action — it is **never** executed by CI or the dev loop (the whole point of the gate). So "valid `RecordScrapedV1` **from the live portal**" is proven for the engine/wiring and **deferred to the first operator run** for the live-data assertion. |
| **(d)** | Browserbase `connectUrl`-format re-check completed (ship-blocker); security copy reflects "not safe to leak." | **Live-primary re-verified 2026-08-08** (CD-4): a single real Browserbase session-create confirmed the connectUrl shape is now `wss://connect.<region>.browserbase.com/?signingKey=<JWT>` (a per-session JWT — it does **not** embed the account apiKey; the documented `?apiKey=…&sessionId=…` shape had drifted). `docs/THREAT_MODEL.md` ("Provider connect strings are not safe to leak") records both providers' confirmed shapes and the scrub re-confirmation. Scrubbing is proven by `Unit/CredentialScrubberTests` (the live signingKey shape → redacted; the apiKey-bearing variant still redacted, `sessionId` preserved), `RemoteBackendConnectTests`, and `CredentialLeakTests` (connectUrl mode). | **LIVE-PRIMARY PROVEN.** The GAP is **closed**: the re-check is now against a live primary Browserbase call (Browserless connect shape re-verified live the same day). |

## Phase 5 — the managed-payload product surface

Phase 5 is **product, not correctness** (the MVP acceptance criterion was met at P3 and re-confirmed at P4). Its gates run
against the record/replay **fake** backend on a shared **durable** Alba host (the executor saga, SSE, cancellation,
checkpoint resume, replay, timeline) — **no Chromium, no live traffic** — with two called-out exceptions: the real-Chromium
screenshot path, and the credential leak suite that drives the **real** WP1 remote adapters against loopback servers. Every
Phase 5 criterion mapped to the test(s) that prove it; caveats are flagged, not papered over.

| # | Acceptance criterion | Proven by | Status / caveat |
|---|---|---|---|
| **(a)** | Revising a payload yields a diffable new revision; a historical run still reports its pinned revision and re-runs against it; moving the head is reported as drift. | Versioning/diff: `PayloadVersioningTests` (`Revise_appends_a_new_revision_with_a_note`, `Diff_between_two_revisions_returns_both_scripts_and_a_minimal_change_set`, plus list/get/rename/archive). Pinning + drift: `RunPinningTests.Pinning_a_revision_executes_that_revisions_script_and_drift_tracks_the_head` — the two revisions differ only in `result` (`'v1'`/`'v2'`), so pinning is proven by observably different output: after the head moves, the historical run still reports `pinnedRevision:1` and re-running at revision 1 still yields `'v1'` while the head yields `'v2'`; `drifted` flips true with differing hashes. Replay of a pinned run: `RunObservabilityTests.Replay_re_executes_a_pinned_runs_revision_with_resupplied_inputs` (a NEW run pinned to the original's revision; inputs resupplied because input values are never persisted (docs/THREAT_MODEL.md)). | **PROVEN** (fake backend; the assertions are output-level, so backend-agnostic). |
| **(b)** | A long `SearchEnforcementRecords` run streams ordered step events over SSE; killing the host mid-run and restarting resumes from the last checkpoint (not step 0, not by event-replay into a browser) and completes with the same `result`. | SSE ordered/backfill/reconnect/live-tail: `RunObservabilityTests` (`Events_backfills_the_whole_terminal_stream_then_closes` — frames strictly ordered by stream version, terminal frame closes the stream; `Events_reconnect_with_last_event_id_continues_exactly_without_loss_or_duplication`; `Events_connected_mid_run_tails_live_frames_through_the_terminal`). Kill-and-restart: `DurableRunTests.Killed_run_resumes_from_the_last_checkpoint_without_refetching_earlier_pages` — an honest host dispose mid-crawl (past ≥2 checkpoints, page 3 unfetched), then a **fresh** host on the same schema/durable queues recovers and resumes, producing the **byte-identical** full-crawl golden while its page-fetch recorder proves page 1 is **never re-fetched** and pages 2–3 are; the trace carries `RunCheckpointReached`×≥2 + a `RunResumed` marker. | **PROVEN against the fake.** **Caveat:** resume is exercised through the fake's page-fetch recorder (which proves the checkpoint semantics — no re-fetch, same result); the real-backend session **reconnect-by-id** path (docs/SPEC.md) is designed, not gated here. The gate proves a *fresh* session re-establishing from the cursor, which is v1's shipped behaviour. |
| **(c)** | `CancelRun` mid-run tears down the backend session and returns `{status:"cancelled", partial}`; no orphaned browser session remains. | `DurableRunTests.Cancel_mid_run_tears_the_session_down_and_reports_a_partial` — cancel while blocked mid-crawl, then the run reaches `cancelled` with a well-formed `partial` (pages so far, `crawledToEnd:false`, non-empty `newLinks`), the fake `GatedSession.Disposed` close-tracking asserts **no orphaned session**, and the trace carries `RunCancellationRequested` + `RunCancelled`. Edges: cancel-unknown → 404, cancel-after-completion is a no-op, and a cancel whose partial-result expression faults reports no partial. Deadline sibling: `A_run_that_outruns_its_deadline_fails_terminally` (terminal `run_deadline_exceeded`). | **PROVEN against the fake** (the close-tracking is the fake's `GatedSession`; the real adapters tear down via `await using`, exercised by the leak suite's real-adapter runs). |
| **(d)** | Observability contains **no** raw credentials or bulk PII (events store refs/metadata only; screenshots/extracted PII in deletable blob storage) — re-assert the credential-scrubbing invariants. | See the dedicated re-assertion table below. | **PROVEN**, now including the WP3 SSE/timeline/screenshot sinks (the P4 gap) **and** the durable at-rest surfaces. |

### Criterion (d) — the credential-scrubbing re-assertion, surface by surface

Every Phase 5 observability + durable surface swept for both a credential sentinel and non-credential bulk PII:

| Surface | Test | What it proves |
|---|---|---|
| Run event stream + raw `data::text` (`mt_events`, `mt_doc_run`, `mt_doc_runprogress`) | `CredentialLeakTests` (all variants) | a resolved-credential sentinel, adversarially echoed into a `log` + `result`, appears in no event, projection/doc row, log line, or response — driving the **real** browserless/browserbase adapters against loopback. |
| SSE frames, `RunTimeline` row, screenshot key + bytes | `CredentialLeakTests.AssertRunLeaksNothingAsync` (used by every variant, incl. `Browserless_async_failing_run_captures_a_clean_screenshot_and_leaks_nothing` — a **real** captured screenshot on the **failure** path) | the WP3 sinks the P4 gate deferred now exist and carry nothing credential-bearing. |
| Durable checkpoint (cursor + accumulated-var snapshot) | `Browserless_async_run_with_a_checkpoint_leaks_the_token_into_no_sink` | the state a resumed run restores from is scrubbed. |
| `RunExecutorSaga` document + Wolverine envelope bodies | `An_async_by_reference_run_keeps_the_resolved_secret_out_of_the_saga_and_wolverine_envelopes` (**new**) | a by-reference credential reaches none of the durable at-rest stores; the `credentialRef` *is* present, the resolved secret is not (`docs/THREAT_MODEL.md` "Durable state at rest"). |
| Full trace stream for **non-credential bulk PII** | `RunObservabilityTests.The_trace_stream_holds_no_raw_extracted_or_input_value_only_metadata_refs` (**new**) | a known extracted value reaches the result body but **no** trace event, SSE frame, or timeline row — the metadata-only discipline itself (the scrubber never touches this value, so its absence is not a redaction). |
| Payload script at save | `PayloadVersioningTests` (`A_credential_in_a_drafted_script...`, `A_credential_revised_into_a_script...` (**new**)) | a credential in a drafted/revised script is scrubbed in the stored event and every response echoing it (the revision GET, the diff, the list). |
| Scrub chokepoint + ref discipline (units) | `RunEventScrubberTests`, `RunTraceEmissionTests` (PII-safe shape ref per value kind), `RunScreenshotTests`, `RunTimelineProjectionTests` | per-event scrub + the `Extracted`/`Downloaded`/`StepFailed` metadata-only ref discipline, exhaustively. |

### Phase 5 test topology + honest caveats
- **Fake backend, real path.** The durable/SSE/cancel/replay/timeline gates drive the real `POST /runs` + executor saga
  through the record/replay fake on a shared durable host (`DurableFixture`; `RunObservabilityTests`/`DurableRunTests`) —
  deterministic, no Chromium. Correctness is output-level (byte-identical goldens), so the backend is immaterial to it.
- **Real Chromium** appears once in Phase 5: `RealChromiumScreenshotTests` captures a genuine Playwright PNG on a failing
  async run (the fake's 8-byte stand-in cannot). The credential leak suite drives the **real** WP1 adapters, but against
  **loopback** servers (a Playwright `run-server`; a local CDP endpoint + session-create stub) — zero live traffic.
- **Not gated at real-browser fidelity in P5:** SSE/cancel/resume/replay/timeline are backend-agnostic observability over
  the trace; they are proven against the fake and inherit the P4 fake≡real parity for the underlying execution.
- **Parallelism cap — why `xunit.runner.json` sets `maxParallelThreads: 2`.** Strict JSON forbids a comment in that file,
  so the rationale lives here (and beside the file's `Content` include in `Crawldad.Tests.csproj`): the
  integration/parity/leak/durable collections each build an Alba host — and migrate its Postgres schema — at fixture init;
  unbounded (CPU-count) parallelism makes those concurrent schema migrations contend and flake. A cap of 2 keeps the suite
  fast while the host builds do not race. Do not raise it without re-checking migration contention.

## CD-15 — synchronous-run cap + auto-upgrade to async (shipped 2026-08-08)

**Why (ingress rationale).** The default synchronous `POST /runs` holds the caller's HTTP connection for the whole run, but
every viable Azure ingress kills a long request first — Azure Front Door and Container Apps' Envoy ingress cancel at **240 s**,
App Service at **~230 s** (`docs/ARCHITECTURE.md`, ingress constraints). So Crawldad caps synchronous execution at **120 s of wall clock**
(`Crawldad:Limits:SyncUpgradeThresholdMs`, default 120 000 ms) and, on crossing it, **auto-upgrades the run to async rather than
failing it**: the same run keeps executing on the durable surface, the caller gets `202 { runId, status:"running" }` at the
moment of upgrade, and then follows `GET /runs/{id}` / SSE / cancel. Deployment must set the ingress request timeout above the
window + headroom yet comfortably under 240 s (~180 s with the 120 s default). Tests drive a tiny window and hold the run open
past it with a deterministic gate (never a sleep), so the upgrade is provoked without a real 120 s wait.

| # | Done-when criterion | Proven by | Status |
|---|---|---|---|
| **(a)** | A sync run finishing **inside** the window is byte-identical to today (goldens unchanged); it writes no async read model. | The whole P1–P4 acceptance suite (`RunEndpointTests`, `ScrapeRecordAcceptanceTests`, `SearchAcceptanceTests`) still runs sync at the default window and is unchanged. Plus `RunEndpointTests.A_sync_run_under_the_window_writes_no_progress_row_so_get_is_404` — a fast sync run stays fully synchronous (no `RunProgress`, so `GET /runs/{id}` is 404). | **PROVEN** (the fast path is the exact pre-CD-15 inline code). |
| **(b)** | A sync run **crossing** the window returns `202 {runId, status:"running"}` and subsequently completes with the same terminal result async would have (golden via `GET /runs/{id}`). | `SyncCapTests.A_sync_run_crossing_the_window_returns_202_running_then_completes_with_the_golden` — a default (non-async) POST held past a tiny window is auto-upgraded (202 running, no result), `GET` reports it running mid-flight, and after release it completes with the **byte-identical** full-crawl golden. `An_upgraded_run_replays_its_buffered_log_into_a_lean_stream_pollable_over_sse` proves the lean synchronous engine's buffered log is replayed into the (pollable + SSE-tailable) stream. | **PROVEN** against the fake. |
| **(c)** | The upgraded run **follows the existing async surface** — cancel, the wall-clock deadline, and SSE all hold. | `SyncCapTests.An_upgraded_run_can_be_cancelled` (a forcible cancel of the observer-less run reaches `cancelled` with the fake session torn down cleanly), `An_upgraded_run_honours_the_wall_clock_deadline` (the saga's `RunDeadline` caps it to terminal `run_deadline_exceeded`), and the SSE assertions in the (b) log test. `An_upgraded_run_that_faults_unexpectedly_fails_terminally` covers the unexpected-fault path (terminal `internal_error`, never a stuck "running"). | **PROVEN** against the fake. |
| **(d)** | The credential boundary holds across the request→background handoff. | `SyncCapTests.An_upgraded_run_keeps_a_run_secret_out_of_every_sink` — a backend registers a run secret at connect, the run echoes it into a log + result, is upgraded, and is finalised by the background supervisor; the secret still appears in **no** sink (result scrubbed to `[redacted]`, event stream, SSE) — proving the run's ambient secret scope is in effect at background finalisation, not just inline. | **PROVEN** against the fake (with a secret-registering backend). |
| **(e)** | The window is a config knob with a documented 120 s default; the ingress rationale is in the docs. | `RunLimitsOptionsValidatorTests` (`Accepts_an_immediate_sync_upgrade`, `Rejects_a_negative_sync_upgrade_threshold`); the tests drive the knob via `Crawldad:Limits:SyncUpgradeThresholdMs`. Docs: `docs/ARCHITECTURE.md` (ingress + run lifecycle), `docs/SPEC.md` (timeout hierarchy), `docs/THREAT_MODEL.md`. | **PROVEN** + documented. |

## Zero live third-party traffic — guaranteed, not assumed

The fast loop (`dotnet test`, and CI's `Category!=LiveCanary`) makes **no** third-party network request. Note
the Phase 4 correction: the product host now **does** register network-capable adapters
(`RunsModule.AddRealBrowserBackends` — `local`/`browserless`/`browserbase`), so the guarantee is no longer
"there is no network backend." It is enforced structurally, four ways:

1. **Default-deny fixture routing.** The real-Chromium parity/wiring runs drive real Chromium, but every page is
   answered by a `page.RouteAsync` handler (`Support/FixtureChromiumBackend.HandleRouteAsync`) that **fulfils**
   only the canonical Accela origin from local files and `AbortAsync`s everything else except the loopback
   download listener. A route-aborted request **never opens a socket**, so there is nothing to reach the live
   site with.
2. **Loopback-only connect harnesses.** The `browserless` adapter connects to a local Playwright `run-server`
   (`RealChromiumFixture.StartRunServerAsync`, `ws://127.0.0.1:…`); the `browserbase` adapter connects to a
   locally launched CDP endpoint (`127.0.0.1`) with its session-create call answered by a loopback
   `Support/LocalSite` stub (`LeakHost`). The endpoint template / API base are overridden to these loopback URLs
   in every test that connects.
3. **Live defaults never reached.** The few tests that construct an adapter with its **live default** endpoint
   (`RemoteBackendConnectTests.Browserless_honours_a_cancelled_token` / `…requires_a_credential_ref` and the
   Browserbase equivalents) pass an already-cancelled token or omit the credential ref, so `ConnectAsync` throws
   **before** any socket is opened.
4. **The canary is env-gated.** `LiveCanaryTests` self-skips unless `CRAWLDAD_LIVE_CANARY=1` **and**
   `CRAWLDAD_CANARY_LINK` are set (`LiveCanaryFactAttribute`), and carries `Category=LiveCanary` so CI filters it
   out explicitly too. It is the **only** code that can hit the live portal, and only the scheduled/manual
   `canary.yml` sets those vars.

Also unchanged from P3: a payload naming an **unregistered** adapter is a terminal `unknown_backend_adapter`
raised **before** any connection (`Integration/RunEndpointTests.Unknown_backend_adapter_is_a_terminal_failure`),
and a missing/ill-typed binding is terminal `invalid_backend_binding`
(`Unit/RunInterpreterTests.Backend_binding_must_resolve_to_an_adapter`).

**Verified, not just argued** (and one honest nuance, not papered over). (i) A source audit (`grep` for
`accela.com|browserless.io|browserbase.com` across `src`+`tests`) finds the live hosts **only** as
fixture-origin constants the route handler intercepts, default endpoint-template constants tests override, or
pure string inputs to expression-language unit tests (`urlHost`/`urlScheme`/scrubber) — never as a live **data**
fetch target. (ii) A process-attributed outbound-TCP sample over a full run confirms the guarantee that matters:
**no HTTP request for scrape data reaches any live target** — the backend adapters connect only to loopback, and
every page request is fulfilled from local files or aborted, so the moat/ToS-relevant "no load on the live site"
holds.

**Packet-level guarantee (gap found by WP4's socket capture, closed in-phase):** navigating *real* Chromium to
the canonical `https://aca-prod.accela.com/…` URL used to trigger (a) Chromium's speculative DNS-prefetch +
**TCP/TLS preconnect** to the origin edge (observed at `104.16.44.23:443`, a Cloudflare address, before the
intercepted request was fulfilled locally — a bare handshake carrying no record data), and (b) worse, the
record-09 guard-redirect's fulfilled **302 was followed by Chromium outside route interception**, sending a real
`Error.aspx` GET to the live edge. Both are eliminated: the parity/canary-wiring launch pins the canonical origin
to loopback (`--host-resolver-rules=MAP aca-prod.accela.com 127.0.0.1` in `FixtureChromiumBackend`), and the
redirecting state is now served with a parse-time same-origin `history.replaceState` instead of a 302
(`FixtureSite.InjectReplaceState`), so every request either resolves to loopback or is fulfilled/aborted by the
route handler — nothing can reach the live edge even speculatively. Separately, `dotnet test`'s own
build/restore/telemetry reaches NuGet/Azure (toolchain, not suite behavior; silence with
`DOTNET_CLI_TELEMETRY_OPTOUT=1` + an offline restore).

## How to run
```
dotnet test                          # full suite + the coverlet 100% line+branch gate (canary self-skips)
dotnet build -warnaserror            # gate 1: 0 warnings / 0 errors
dotnet format --verify-no-changes    # gate 3: formatting/style clean
dotnet test /p:CollectCoverage=false # fast local loop, no coverage gate
```
The two gates above plus `dotnet test` are the fast loop; CI runs them in `.github/workflows/ci.yml`
(with `--filter "Category!=LiveCanary"` as defense in depth). Real-browser tests need Chromium —
install it pwsh-free with `eng/install-browsers.sh` after a build.

**Run the live canary manually (the one command):**
```
CRAWLDAD_LIVE_CANARY=1 \
CRAWLDAD_CANARY_LINK='https://aca-prod.accela.com/LJCMG/Cap/CapDetail.aspx?Module=Enforcement&capID=<REC>&agencyCode=LJCMG' \
dotnet test --filter Category=LiveCanary /p:CollectCoverage=false
```
Optionally add `CRAWLDAD_CANARY_PUBLISH_DATE=YYYY-MM-DD` and `CRAWLDAD_CANARY_REGION=<tag>`. This hits the
**live** Accela portal (real network); it is the nightly/manual drift signal, never the fast loop. The
scheduled/manual `.github/workflows/canary.yml` runs exactly this; a failing run (status ≠ succeeded, or a
broken shape) is the drift alert.
