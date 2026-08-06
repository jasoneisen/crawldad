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

## No live traffic — guaranteed, not assumed
- The host registers **only** the `"fake"` `IBrowserBackend` (`Features/Runs/RunsModule.cs:42-44`);
  Phase 4 adds `browserless`/`browserbase`/self-hosted. There is **no network-capable backend** in the
  test/host build, and the fake reads only local fixture files.
- A payload naming any **unregistered** adapter is a **terminal** `unknown_backend_adapter` failure,
  raised **before** any connection — proven end-to-end through `POST /runs` by
  `Integration/RunEndpointTests.Unknown_backend_adapter_is_a_terminal_failure` (adapter
  `"does-not-exist"` ⇒ `status:failed`, no `result`). A missing/ill-typed binding is the terminal
  `invalid_backend_binding` (`Unit/RunInterpreterTests.Backend_binding_must_resolve_to_an_adapter`),
  and the download-sink registry rejects unknown kinds (`Unit/DownloadNodeTests`). Nothing in the suite
  reaches the network.

## How to run
```
dotnet test
```
`dotnet test` runs the full suite and enforces the coverlet **100% line + branch** gate over
`Crawldad.Web` + `Crawldad.Contracts` (see `Crawldad.Tests.csproj`). `dotnet build -warnaserror` and
`dotnet format --verify-no-changes` are the other gates. For a fast local loop without the coverage
gate: `dotnet test /p:CollectCoverage=false`.
