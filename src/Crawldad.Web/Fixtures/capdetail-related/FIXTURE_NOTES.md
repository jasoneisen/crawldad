# capdetail-related fixture — provenance & fidelity notes

## What this is
A **synthesized** (not captured) record/replay fixture for the LJCMG CapDetail **RELATED RECORDS**
region (`LJCMGClient.cs:625-697`) — indentation-based parent resolution over a multi-level tree.
Single-state manifest; `input.link` is the `resolveUrl` base for the relative anchors. Read by
`related-fragment.json`. Phase 2 WP3 acceptance fixture — this is the acceptance heart
("parentRecordNumber resolves correctly for a multi-level tree").

## The tree (why it proves the walk)
`#tableCapTreeList > tbody > tr:not(:first-child)` yields **7** rows (the first `<tr>` is the header,
excluded). Each row's `td:first-child` holds an inner table: `td:first-child` = the indent spacer
(`width="Npx"`), `td:last-child` = the record number. `parents` is a map `indent → recordNumber` that
mutates as rows are visited; each row's parent is `parents[max(keys < indent)]`.

| Row | width | indent | class | recordNumber | parent | note |
|---|---|---|---|---|---|---|
| R1 | `0px` | 0 | Normal | `REC-ROOT` | `` | no shallower indent |
| R2 | `25px` | 25 | Normal | `REC-A` | `REC-ROOT` | |
| R3 | `50px` | 50 | **Highlight** | `REC-B` | → **`parentRecordNumber = REC-A`** | greatest indent < 50 is 25 → `REC-A` |
| R4 | `25px` | 25 | Normal | `REC-C` | `REC-ROOT` | sibling back at 25; **overwrites** `parents[25]` |
| R5 | `50px` | 50 | Normal | `REC-D` | **`REC-C`** | greatest indent < 50 is *now* 25 → `REC-C`, **not** `REC-A` |
| R6 | `foo` | 0 (fallback) | Normal | `REC-E` | `` | garbled width → **error log**, `isInt(indentStr)?…:0` |
| R7 | `75px` | 75 | *(neither)* | `REC-F` | — | unknown class → **error log**, not added |

**R3 and R5 both sit at indent 50 but resolve to different ancestors** (`REC-A` vs `REC-C`) because R4
rewrote `parents[25]` between them. That non-trivial, order-dependent resolution over a mutating map is
exactly the multi-level walk the P2 gate names. `parentRecordNumber` (set only by the Highlight row)
is `REC-A`; `relatedRecords` = `[R1, R2, R4, R5, R6]` (Highlight and unknown-class rows are excluded).
Two `LogEmitted` **error** events fire (R6 indentation, R7 class) — asserted.

## ENGINE FIX this fixture depends on — leading-combinator scoping
The reference reads `relatedBlock.Locator("> td:nth-child(2)")` (and `(3)`, `(4)`, `> td:last-child a`)
— **direct-child** cells (Playwright scopes a leading `>` to the locator element). AngleSharp's
`element.QuerySelectorAll("> td:nth-child(2)")` **ignores the leading `>`** and scans descendants, so it
returns the record-number cell from the *nested* indent table instead of the record-type cell (verified:
it returned `REC-ROOT` where Playwright returns `Root Type`). `FakeLocatorHandle.ScopeRelative` now
rewrites a leading `>` to `:scope >`, recovering Playwright's semantics. The golden's `recordType =
"Root Type"` (not `"REC-ROOT"`) is the proof the fix is active.

## resolveUrl (byte-matched to C#)
Links use `resolveUrl(input.link, "CapDetail.aspx?x=…")` = `new Uri(new Uri(link.Id), rel).ToString()`
(`:672`). With `input.link = …/LJCMG/Cap/CapDetail.aspx?Module=…&capID=24ENF-00004&…`, the relative
`CapDetail.aspx?x=root` resolves against the base directory `/LJCMG/Cap/`, replacing the last segment +
query → `https://aca-prod.accela.com/LJCMG/Cap/CapDetail.aspx?x=root`. This is the same base/rel shape
as the pinned `ResolveUrlTests` (relative path replaces the base's last segment and query); `attr(href)`
returns the **raw** attribute (AngleSharp `GetAttribute`, not the resolved DOM property), matching the
C# `GetAttributeAsync("href")`.

## Ordering guarantees
`relatedRecords` is in document (row) order; `parents` reflects the **most recent** row seen at each
indent (map upsert), which is what makes R5's parent `REC-C` rather than `REC-A`.

## golden.json provenance
Hand-derived from `LJCMGClient.cs:637-688`, cross-checked against the interpreter over the fake backend
(byte-identical, with the `ScopeRelative` fix). Phase 4 re-gates against real Chromium.

## B.2 PAYLOAD DISCREPANCY found here (reported loudly)
Appendix B.2's scrape `vars` initialize `"description": ""` and `"parentRecordNumber": ""`. A `vars`
value is an **expression** (§4), and the empty string `""` is an **empty expression** — the parser
correctly rejects it (`syntax_error: unexpected token '<end of input>'`), so the full B.2 payload would
fail at var-init before executing a single step. The correct empty-string initializer is `"''"` (a
string-literal expression). This fragment uses `"parentRecordNumber": "''"`; the full-payload P2 WP must
fix both `description` and `parentRecordNumber` in B.2 the same way.
