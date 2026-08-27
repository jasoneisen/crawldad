# caphome-search fixture — provenance & fidelity notes

## What this is
A **synthesized** (not captured) record/replay fixture for the LJCMG Accela CapHome enforcement
search, reproducing the shape `LJCMGClient.SearchEnforcementRecords` (`LJCMGClient.cs:75-175`)
iterates. It drives `FakeBrowserBackend` deterministically with **no Chromium and no live traffic** —
the Phase 1 acceptance gate.

- `manifest.json` — the record/replay script (states + transitions). See schema below.
- `form.html` — the initial search-form state (two date inputs, the search button, hidden `#divGlobalLoading`).
- `results.html` — the results grid state.
- `golden.json` — the expected `result` body for the Phase 1 fragment payload.

## golden.json provenance (derived-by-reference-semantics, NOT captured)
`golden.json` was derived by **hand-executing the C# reference algorithm** (`LJCMGClient.cs:127-142`)
over the `results.html` DOM:

- `url = $"{scheme}://{host}{href}"` where `href = td:nth-child(3) a` `GetAttribute("href")?.Trim() ?? ""`
  and `scheme`/`host` come from `new Uri(page.Url)` (the results state URL in `manifest.json`).
  This is the reference's **naive concat** (no RFC resolution) — `LJCMGClient.cs:130`.
- Each cell = `TextContent?.Trim() ?? ""` for `td:nth-child(2)` (date), `td:nth-child(3) a`
  (recordNumber), `td:nth-child(4..7)` (type/address/status/shortNotes).
- `hasMorePages = count("table.aca_pagination td:last-child a") > 0`.

The derivation was cross-checked with a standalone AngleSharp harness executing that exact algorithm;
`golden.json` is byte-identical (canonicalized) to its output. **Real-Chromium parity and a real
captured page are the Phase 4 gate** (fake ≡ real, against a local fixture site) — this file is a
faithful synthesis, not a capture.

## golden-full.json provenance (the full-payload search shape — Phase 3 acceptance)
`golden.json` above is the **Phase 1 fragment** shape (`{ results, hasMorePages }`, driven by
`search-fragment.json`). `golden-full.json` is the **full** `SearchEnforcementRecords` result shape
(`{ newLinks, crawledToEnd, pages }`, Appendix B.1, driven by `search-full.json`) over the **same**
`results.html` DOM — this fixture's rich single-page grid is **reused** as the acceptance corpus'
single-page case (`SearchAcceptanceTests`; `knownUrls=[]`, `priorCrawlComplete=false`). It is derived
from `golden.json` by the B.1 payload's shaping:

- `pages` = `[ <the 10 rows of golden.json's "results", verbatim> ]` — the per-row push expression is
  byte-identical between `search-fragment.json` and `search-full.json` (`:60`/`:34`).
- `newLinks` = those 10 rows' urls, in order, through `distinct(...)` (`:901`). All 10 are already
  distinct here (row 3's missing anchor yields `https://aca-prod.accela.com`, itself unique), so
  `distinct` is the identity — the de-dup *collapse* is exercised by the sibling `caphome-dedup` fixture.
- `crawledToEnd` = **true**: `table.aca_pagination td:last-child` has no `<a>` → `hasMorePages` false →
  the last-page branch sets `crawledToEnd = true` (`HistoricalCrawler.cs:87`).
- Requests: `goto` + search = **2** (single page; the payload breaks before any pagination postback).

## Deliberate edge cases baked into results.html (the C# semantics must survive all of them)
| Data row | Edge case | Expected (golden) |
|---|---|---|
| 1 | leading/trailing whitespace in the date cell | trimmed to `01/03/2024` |
| 2 | HTML entities `&amp;` / `&#39;` in address & notes | decoded to `&` / `'` |
| 3 | `td:nth-child(3)` has **no** `<a>` | `href`→null→`''` → `url` = scheme://host only; `recordNumber` = `""` |
| 4 | empty `<td></td>` (address) | `""` |
| 5 | `&nbsp;`-padded status; multi-`<span>` notes | `.Trim()` drops U+00A0 → `Closed`; textContent concat → `Part A Part B` |
| 6 | all-whitespace notes cell | trims to `""` |
| 7 | `&amp;` in address + trailing whitespace | `700 B&B Lane` |
| 8 | `&amp;` inside the `href` attribute | attribute decodes → `...?Module=Enforcement&id=8` |
| 9 | multi-node address (`<span>`s + text) | textContent concat → `900 Birch Way` |
| 10 | plain trailing row | `1000 Walnut Blvd` |

The grid `#...gdvPermitList` holds **exactly 15 `<tr>`**: 3 header + 10 data + 2 footer, so
`for (i=3; i < count-2; i++)` visits data rows 3..12. `table.aca_pagination` is a **separate sibling**
table (its rows are not counted by `#...gdvPermitList tr`) whose `td:last-child` has **no** `<a>` —
the last-page state, so `hasMorePages` is `false`.

## manifest.json schema (v1 — minimal, extensible)
```jsonc
{
  "manifest": "1",
  "initialState": "<stateName>",              // loaded when a goto matches no state's gotoUrl
  "states": {
    "<name>": {
      "gotoUrl": "<url>",                      // optional: goto to this url loads this state
      "url": "<url>",                          // page.Url reported while in this state
      "html": "<file.html>"                    // DOM served for this state (relative to the fixture dir)
    }
  },
  "transitions": [
    {
      "from": "<state>",                       // active state the transition applies in
      "on": { "click": "<css selector>" },     // trigger: a click on a matching element
      "to": "<state>",                         // state to swap to
      "emit": { "url": "<url>", "method": "POST" }  // a recorded request observed during the click
    }
  ]
}
```
**Phase 2 extension points (designed for, not built):** a transition may later carry `delayMs` or a
`fail` block (injected `timeout`/`pageCrashed`), and a state may carry an `inject` block, without
restructuring — the reader ignores unknown keys today.
