# Crawldad acceptance suite (Phase 3 MVP gate)

**The two LJCMG payloads _are_ the acceptance suite** (`CRAWLDAD_PLAN.md` §Testing strategy). The MVP
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

## Phase 4 — success criteria → proof (plan lines 168–177)

Every Phase 4 gate mapped to the named test(s)/artifact(s) that prove it. Unproven items are flagged as
**GAP**, not papered over.

| # | Plan criterion (lines 168–177) | Proven by | Status |
|---|---|---|---|
| **(a)** | Both acceptance payloads run against the **local fixture site via real Chromium** and equal the P3 golden (fake ≡ real). | `Integration/RealChromiumScrapeParityTests` (10 golden records + 2 terminals, byte-identical to the P3 `RecordScrapedV1` goldens) and `RealChromiumSearchParityTests` (M = 6 searches) — mirror the fake theories line-for-line, changing only `backend.adapter` `fake`→`local`. The §8.1 route policy (block/cache/throttle) is exercised honestly over real HTTP by `Integration/LocalBackendTests`. Harness: `ParityAppFixture`, `Support/{FixtureChromiumBackend,FixtureSite,RealChromiumFixture}`. | **PROVEN** |
| **(b)** | A run whose binding carries a token/`connectUrl` leaks it into **no** event, projection row, log line, SSE frame, or trace artifact. | `Integration/CredentialLeakTests` (browserless token, browserbase apiKey, failed-connect `connectUrl`, framework-category log) asserts a sentinel appears in no typed event, no **raw** `data::text` from `mt_events`/`mt_doc_run`, no projection/doc row, no captured log line, and not in the response — driving the real WP1 adapters against loopback servers. Reinforced by `Integration/RemoteBackendConnectTests` (scrubbed connect-failure terminals) and units `Unit/{CredentialScrubberTests,RunEventScrubberTests,RunSecretScopeTests,SecretStoreTests}`. Boundary doc: `SECURITY.md` (per-sink chokepoint table). | **PROVEN** for events/projections/logs/response. **GAP:** the criterion also names **SSE frames** and **trace artifacts** — neither sink exists yet (Phase 5). They are scrubbed *by construction* (both render from already-scrubbed events/projections; `SECURITY.md` §"What is scrubbed, and where" rows 5–6), so there is **no executable SSE/trace leak test until Phase 5 builds those sinks**. |
| **(c)** | One **live canary**: a single real record scraped against the **live** Accela portal behind a feature flag, honoring the 2 s throttle + host/resource blocking, produces a valid `RecordScrapedV1`; nightly/manual, never in the fast loop. | `Integration/LiveCanaryTests` — the gated `[LiveCanaryFact]` (`Category=LiveCanary`) drives `scrape-full.json` **verbatim** through `POST /runs` on the real `"local"` adapter (real Chromium, real network, §8.1 policy applied for real by `SessionPolicy.FromConfig`, concurrency 1), asserts `status:"succeeded"`, and validates the `RecordScrapedV1` **shape** (`LiveCanary.AssertValidRecordScrapedV1` — required keys/types + non-empty `recordNumber`/`recordType`, **not** golden equality, because live data drifts). Wiring is proven short of the live hit by `Integration/CanaryWiringTests` (the **identical** helpers against the fixture-site `"local"` adapter, zero live traffic). Scheduled/manual: `.github/workflows/canary.yml`. | **WIRING + SHAPE-GATE PROVEN** (zero live traffic). **GAP by design:** the **actual live-portal hit** is the operator's nightly/manual action — it is **never** executed by CI or the dev loop (the whole point of the gate). So "valid `RecordScrapedV1` **from the live portal**" is proven for the engine/wiring and **deferred to the first operator run** for the live-data assertion. |
| **(d)** | Browserbase `connectUrl`-format re-check completed (§3.5/§9 ship-blocker); security copy reflects "not safe to leak." | `SECURITY.md` §"The Browserbase `connectUrl` is NOT safe to leak (re-verified)" records the verified shape `wss://connect.browserbase.com?apiKey=bb_live_…&sessionId=ses_…` (embeds the account apiKey ⇒ scrubbed exactly like an apiKey). That it *is* scrubbed is proven by `Unit/CredentialScrubberTests` (the exact `connectUrl` → apiKey redacted, `sessionId` preserved), `RemoteBackendConnectTests.Browserbase_connect_failure_does_not_leak_the_apiKey_embedding_connect_url`, and `CredentialLeakTests.A_failed_connect_carrying_the_credential_leaks_into_no_sink` (connectUrl mode). | **DOC + SCRUB PROVEN.** **GAP:** the re-check is against the **documented** shape (§3.5, confidence MED-HIGH), not a **live primary** Browserbase call (this environment sends zero live traffic). A live primary re-verification remains an operator action before GA. |

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
