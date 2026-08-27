# caphome-empty fixture — provenance & fidelity notes

## What this is
A **synthesized** (not captured) record/replay fixture for an LJCMG CapHome enforcement search that
returns **zero records** — case (d) of the Phase 2 WP4 multi-page gate. It is the sibling of
`caphome-multipage`; see that fixture's `FIXTURE_NOTES.md` for the full reference-algorithm derivation.
No Chromium, no live traffic.

- `manifest.json` — `form → results` (single page; the search click emits `POST CapHome.aspx`; no
  pagination transition).
- `empty.html` — a grid with `3 header + 0 data + 2 footer = 5 <tr>` and **no** pagination anchor.
- `golden.json` — the expected `result`: `{ "newLinks": [], "crawledToEnd": true, "pages": [] }`.

## Derivation (the empty-results stop)
The grid has 5 rows, so `for (i=3; i < count-2 = 3; i++)` (`LJCMGClient.cs:127`) runs **zero** times →
`Results.Count == 0`. `table.aca_pagination td:last-child` has no `<a>`, so `hasMorePages` is false.
In the callback, order matters (`HistoricalCrawler.cs:87-89`):

```
if (!HasMorePages) crawledToEnd = true;   // :87  fires FIRST — this is the last (only) page
if (Results.Count == 0) return false;     // :89  THEN the empty-results stop
```

So `crawledToEnd` is set **true** before the empty stop returns false. In the B.1 payload the
`{ "set": "crawledToEnd" = true }` (under `!hasMorePages`) runs before `break when count(pageResults) == 0`,
and that break fires **before** `push pageResults into pages` — so `pages` stays empty.

Result: `newLinks = []`, `crawledToEnd = **true**`, `pages = []`. Requests: goto + search = **2**.

Had this page instead carried a pagination anchor (`hasMorePages` true) with zero rows, `crawledToEnd`
would stay at its initial `priorCrawlComplete` — but the real "no records" page is the last page, which
is what this fixture models.
