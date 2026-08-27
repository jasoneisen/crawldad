# capdetail-processing fixture — provenance & fidelity notes

## What this is
A **synthesized** (not captured) record/replay fixture for the LJCMG CapDetail **PROCESSING STATUS**
region (`LJCMGClient.cs:455-529`) — the paired head/detail rows and the `Due on … / Marked as …` split
chains. Single-state manifest; read by `processing-fragment.json`. Phase 2 WP3 acceptance fixture.

## Structure — five top rows (odd, on purpose)
`#divProcessingTable > table > tbody > tr` has **5** direct-child rows (`> ` combinators keep the
nested detail-table rows out of the count). Paired as head `i*2` / detail `i*2+1`:

| i | head row | detail row | outcome |
|---|---|---|---|
| 0 | `Violation Notice` (has `<a>`) | 2 lines + 2 additional-comment rows | activity **A** |
| 1 | `Inspection` (has `<a>`) | 2 lines, no comments | activity **B** (`lines: {}`) |
| 2 | `Closed Activity` (**no `<a>`**) | — (absent) | **skipped** (`count(a) == 0`) |

Total is **odd (5)**, so at `i = 3` the payload's defensive `break when i*2+1 > procRowCount`
(`7 > 5`) fires before any `Nth(5)`/`Nth(7)` access — reproducing the C# loop bound
`for (i = 0; i*2+1 <= rowCount; i++)` (`:462`). B.2 expresses this as a `for … exclusiveTo` plus the
break; kept verbatim.

Activity A's additional-comment rows exercise both paths: `Comment:` → the trailing-colon strip
(`endsWith(h,':') ? substring(h,0,length(h)-1) : h`) yields key `Comment`; a row with an **empty
heading** is unparseable → `LogEmitted` warning `Could not parse additional comment lines: <link>`
(asserted). So `A.lines = { "Comment": "Follow-up scheduled" }`.

## THE INNERTEXT TRAP — decision and reasoning
The payload does `split(innerText(lineBlock), '\n')`, and each line block is
`Due on …, assigned to …<br>Marked as … on … by …`. The two lines are separated by a **`<br>`**.

- **The gap:** AngleSharp does no layout and has no rendered `innerText`; the fake previously returned
  raw `TextContent`, which concatenates the two lines with **no separator** — so `split('\n')` yields
  ONE line and the `lines[1]` access throws. That is unfaithful to a real browser.
- **What Chromium does:** `innerText` turns a `<br>` (and block boundaries) into `\n`.
- **Decision (option A — implement a faithful approximation):** `FakeInnerText` now renders
  `<br>` → `\n`, block-element boundaries → a single `\n` (adjacent/nested boundaries collapse, matching
  a browser; `<br>`s stack to preserve deliberate blank lines), inline whitespace collapses, and
  leading/trailing blank lines drop. For the captured markup this yields **exactly**
  `Due on …\nMarked as …`, identical to Chromium.
- **Why not option B (text nodes with literal newlines):** a literal newline in a normal-flow text node
  is collapsed to a space by a real browser, so it would **not** be honest to Chromium. Only `<br>`
  (or a block boundary) produces a real line break — hence the renderer.
- **Fidelity limits (re-gated in Phase 4):** `FakeInnerText` is layout-free — no `white-space:pre`, no
  `display` overrides, no table-cell tab separators, and it collapses only ASCII whitespace. It also
  assumes no source whitespace *around a `<br>`* inside a line block; so each line-block `<td>` and each
  head-row category `<td>` is written **GAP-FREE on one source line**. (The C# `Category = heading` is
  the **untrimmed** `td:last-child` textContent, so the category cell must be gap-free too.)

## Split-chain values (line 0 / line 1)
`Due on 04/15/2025, assigned to John Smith` → `dueDate=04/15/2025`, `assignedTo=John Smith`;
`Marked as Completed on 04/20/2025 by Jane Doe` → `status=Completed`, `statusDate=04/20/2025`,
`statusBy=Jane Doe`. Values avoid the split delimiters (`", "`, `" on "`, `" by "`).

## Ordering guarantees
`processingStatus` is in head-row order (A then B). Each `lines` map preserves **insertion order** of
the additional-comment rows (document order); A has a single key `Comment`.

## golden.json provenance
Hand-derived from `LJCMGClient.cs:464-525`, cross-checked against the interpreter over the fake backend
(byte-identical, including `FakeInnerText`). Phase 4 re-gates the innerText approximation against real
Chromium.
