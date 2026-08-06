# caphome-multipage fixture — provenance & fidelity notes

## What this is
A **synthesized** (not captured) record/replay fixture for a **3-page** LJCMG CapHome enforcement
search, driving the FULL Appendix B.1 `SearchEnforcementRecords` payload. It exercises the
tension-#1 early-termination reshaping: `knownUrls` + `priorCrawlComplete` inputs replacing the
`goToNextPageCallback`. No Chromium, no live traffic.

- `manifest.json` — `form → page1 → page2 → page3`. The search click and each pagination click emit a
  `POST CapHome.aspx` (the postback `waitForRequest` matches).
- `page1.html` / `page2.html` — 4 / 5 data rows, each WITH a `table.aca_pagination td:last-child a`
  anchor (`hasMorePages` true).
- `page3.html` — 4 data rows, NO pagination anchor (`hasMorePages` false → last page).
- `golden-a-full.json` / `golden-b-early-stop.json` / `golden-c-continue.json` — the expected `result`
  for the three multi-page cases.

Each grid holds `3 header + N data + 2 footer` rows, so the reference loop `for (i=3; i < count-2; i++)`
(`LJCMGClient.cs:127`) visits exactly the N data rows. Row urls are the naive `scheme://host + href`
concat (`:130`); the results states all report the same CapHome.aspx url, so scheme/host resolve to
`https://aca-prod.accela.com`. The distinct record urls are `?id=p{page}-{row}`.

## The reference algorithm being reproduced (derive goldens from THIS, not intuition)
`HistoricalCrawler.goToNextPageCallback` (`:85-104`) is the callback the `LJCMGClient` do/while
(`:121-167`) invokes per page. Flattened, per page:

```
if (!HasMorePages) crawledToEnd = true;              // :87   last page flips the flag
if (Results.Count == 0) return false;                // :89   empty page => stop (payload breaks BEFORE pushing pages)
foreach (result in Results)                          // :91
    if (knownUrls.Contains(result.Url)) return !crawledToEnd;  // :93-95  stop scanning at the first known url
    else newLinks.Add(result.Url);                   // :99
return HasMorePages;                                 // :103  ran the whole page clean => continue iff more pages
```

The B.1 payload's break is the EXACT negation of the callback's return:
`break when hitKnown ? crawledToEnd : !hasMorePages` ≡ stop when `!(hitKnown ? !crawledToEnd : hasMorePages)`.
`pages` accumulates the full per-page row array for every non-empty page reached (pushed AFTER the
scan, so it includes the page where a known url was hit); the empty-page stop breaks before that push.

## Per-case derivation (hand-executed over this fixture)
`KNOWN` = `https://aca-prod.accela.com/LJCMG/Cap/CapDetail.aspx?id=p2-3` (page 2, row 3 — mid-page).

### (a) Full crawl — `knownUrls=[]`, `priorCrawlComplete=false` → `golden-a-full.json`
| page | `!HasMorePages`→crawledToEnd | rows added | return (:103/:95) |
|---|---|---|---|
| 1 | stays false | p1-1..p1-4 | `HasMorePages`=true → continue |
| 2 | stays false | p2-1..p2-5 | true → continue |
| 3 | **→ true** (:87) | p3-1..p3-4 | `HasMorePages`=false → stop |

`newLinks` = all 13 in order; `crawledToEnd` = **true** (flipped on page 3); `pages` = `[4,5,4]`.
3 pages ⇒ goto + search + 2 pagination = **4** requests.

### (b) Early stop, prior complete — `knownUrls=[KNOWN]`, `priorCrawlComplete=true` → `golden-b-early-stop.json`
`crawledToEnd` starts **true**. Page 1: all added, continue. Page 2: p2-1, p2-2 added, then p2-3 is
KNOWN → `return !crawledToEnd` = `!true` = **false** ⇒ stop. The known url is NOT added; the rest of
page 2 (p2-4, p2-5) is skipped; **page 3 is never visited**.
`newLinks` = `[p1-1..p1-4, p2-1, p2-2]` (6); `crawledToEnd` = **true**; `pages` = `[4,5]` (page 2's
FULL 5-row array — that is what the reference handed the callback). goto + search + 1 pagination = **3**.

### (c) The `!crawledToEnd` nuance — same `knownUrls`, `priorCrawlComplete=FALSE` → `golden-c-continue.json`
`crawledToEnd` starts **false**. Page 2: p2-1, p2-2 added, p2-3 KNOWN → `return !crawledToEnd` =
`!false` = **true** ⇒ CONTINUE. The inner scan still breaks at the known url, so p2-4/p2-5 are skipped,
but the crawl advances to page 3 (all added; `crawledToEnd` flips true on the last page).
`newLinks` = `[p1-1..p1-4, p2-1, p2-2, p3-1..p3-4]` (10); `crawledToEnd` = **true**; `pages` = `[4,5,4]`.
goto + search + 2 pagination = **4** requests. **This is the whole point of the gate**: (b) and (c) share
the identical known url and differ ONLY in `priorCrawlComplete`, yet (b) stops at page 2 while (c) runs
to page 3 — the `!crawledToEnd` branch made observable.

### (d) Empty results — the `caphome-empty` sibling fixture, `knownUrls=[]`, `priorCrawlComplete=false`
Single results page, zero data rows, no pagination anchor. `!HasMorePages` → `crawledToEnd = true`
(:87) fires BEFORE `Results.Count == 0 ⇒ return false` (:89). So `newLinks` = `[]`, `crawledToEnd` =
**true**, `pages` = `[]` (the payload breaks on the empty page before pushing it). goto + search = **2**.
(See `../caphome-empty/`.)

## Golden provenance
`golden-*.json` were generated by executing the reference algorithm above (the callback + the do/while
loop) directly over this fixture's rows — the same derivation a standalone C# harness produces. The
tests assert Crawldad's output is byte-identical (canonicalized). Real-Chromium parity is the Phase 4
gate (fake ≡ real). Clean cells (no whitespace/entity edge cases — those live in `caphome-search`)
keep the goldens obviously correct; this fixture's job is the pagination + early-termination logic.
