# record-08-attach-cap — provenance & fidelity notes

## Variety focus
The **attachment 50-page safety cap** (`LJCMGClient.cs:603,616-618`): cyclic in-frame pagination whose
`Next` link never disappears, so `attachmentPagesVisited` climbs to the cap and the payload logs one
warning while still returning a **complete** record. `input.link` capID = `24ENF-00008`; `publishDate` =
`2025-02-10`.

## Cap derivation (hand-executed from the reference)
`frame-cap.html` is served on both cyclic states (`att_a` ↔ `att_b`). Each page has:
- the `No records found.` placeholder as its only data row ⇒ the per-row loop `continue`s (`:548`), so **no
  download ever fires** and `attachments == []`; and
- a **persistent** `Next` anchor ⇒ `hasNextPageLink` is always true.

The `do…while` therefore advances every iteration: `attachmentPagesVisited` = 1, 2, … The termination is
`hasMoreAttachmentPages = hasNextPageLink && attachmentPagesVisited < 50` (`:603`). On the 50th page,
`50 < 50` is false ⇒ `hasMoreAttachmentPages` is false while `hasNextPageLink` is still true, so the
`else if (hasNextPageLink)` branch fires the single warning
`Attachment pagination hit safety cap (50 pages) for {link}` (`:616-618`) and the loop exits. The run
**succeeds** with a complete `RecordScrapedV1` (attachments empty). The loop's generic `maxIterations`
(100000) is never the limiter — the domain cap (`< 50`) stops it first. The page-number `waitFor` is a
no-op "visible" wait in the fake, so the SelectedPageButton text is irrelevant to the fake run. Under
**real Chromium** (Phase 4 WP2 parity) that `waitFor` genuinely waits, so the fixture site renders the
SelectedPageButton to the real pagination position (+1 per in-frame nav) — the static `1` here is
untouched (it feeds only the fake); see `tests/Crawldad.Tests/ACCEPTANCE.md` § Phase 4 WP2.

## Other regions
3-branch address `900 TENANT WAY / "" / LOUISVILLE, KY 40215`; one 3-line owner `OCCUPANCY HOLDINGS`;
`projectName == recordType` (no CapTree); `status == Open`; no violations / parcels / processing.

## golden.json provenance
Hand-derived from the reference and cross-checked byte-identical against the interpreter over the fake;
the acceptance test additionally asserts the cap warning event and `attachments.length == 0`.
