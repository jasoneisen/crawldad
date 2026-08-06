# record-12-owner-empty-block — provenance & fidelity notes

## Variety focus
The owner **empty-block skip** (`LJCMGClient.cs:319-323`) and the **header-only** attachments grid (a
third empty-attachments mechanism, distinct from record-04's "No records found." row and record-11's
absent frame). `input.link` capID = `24ENF-00012`; `publishDate` = `2025-07-10`.

## Golden derivation (hand-executed from the reference)
- **owners** (`:312-347`): the Owner section holds **two** blocks — one real (3 lines) and one empty
  (a single blank row). Because there are two blocks, `count(ownerBlocks) > 1` ⇒ the `MULTIPLE OWNERS`
  warning fires (`:307-310`, faithful to the reference even though only one owner survives). Processing:
  - Real block ⇒ `name = MAINTENANCE CO` (`*` stripped), `ownerLines = 3`, address1 `600 KEEP ST`,
    address2 `""`, cityStateZip `LOUISVILLE, KY 40220` ⇒ pushed.
  - Empty block ⇒ `name = ""` (empty cell), `ownerLines = 1` ⇒ `isNullOrWhitespace(name) && ownerLines
    == 1` is true ⇒ `continue` (`:320-323`), so it is **dropped**.

  ⇒ `owners` has exactly the one real entry.
- **attachments** (`:540-544`): the grid has **only a header row** (no data rows, no pagination). So
  `attachmentRows` (`tr:not(.ACA_Table_Pages)`) counts 1, `count(attRows) > 1` is false, the per-row loop
  is skipped, and with no next link the do-while stops ⇒ `attachments == []`.
- **locations** (3-branch): `600 KEEP ST / "" / LOUISVILLE, KY 40220`. **description** = `Peeling paint
  and broken windows on rental property.`. **projectName** = `Property Maintenance` (no CapTree).
  **status** = `Open`. No violations / parcels / processing ⇒ `[]`.

## golden.json provenance
Hand-derived from the reference and cross-checked byte-identical against the interpreter over the fake.
