# record-06-multipage-attach — provenance & fidelity notes

## Variety focus
**Multi-page attachments**: two in-frame pagination pages with a **download on each page**. `input.link`
capID = `24ENF-00006`; `publishDate` = `2025-05-05`.

## Golden derivation (hand-executed from `LJCMGClient.cs:538-621`)
The attachments `do…while` runs twice:
- **Page 1** (state `att1`, `frame-p1.html`): one downloadable row ⇒ attachment
  `Photo Front.jpg / Photo / 800 KB / 05/01/2025`, bytes `photo-front.bin`. The pagination row's
  `td:last-child` holds a `Next` anchor ⇒ `hasNextPageLink` true ⇒ the in-frame Next click swaps the frame
  to page 2 (`:605-614`; the computed page-number `waitFor` is a no-op "visible" wait in the fake).
- **Page 2** (state `att2`, `frame-p2.html`): one downloadable row ⇒ `Photo Rear.jpg / Photo / 750 KB /
  05/02/2025`, bytes `photo-rear.bin`. The pagination row's `td:last-child` is the `SelectedPageButton`
  (no anchor) ⇒ `hasNextPageLink` false ⇒ the loop stops.

Both file-link clicks and the Next click are **in-frame** (`on.in` = the iframe selector), so they never
fire a page-level transition. `internalFilename` = `{contentId}.jpg` from each scraped `.jpg` name (`:576`).

## contentId computation (§9.3, = `AttachmentHashing`)
`contentId = new Guid(SHA256(bytes)[0..16])` (mixed-endian):
- `photo-front.bin` = `"Crawldad site photo FRONT elevation v1\n"` (39 bytes) ⇒
  **`ae20275b-b3e3-a1d3-d1d2-befafd8f1819`**.
- `photo-rear.bin` = `"Crawldad site photo REAR elevation v1\n"` (38 bytes) ⇒
  **`1ece3680-89f3-c1f1-f687-0cd4d3132570`**.
Distinct bytes ⇒ distinct content ids, proving per-download hashing across the pagination loop.

## Other regions
3-branch address `400 INDUSTRIAL PKWY / "" / LOUISVILLE, KY 40210`; one 3-line owner `BUILDER CORP`;
`projectName == recordType` (no CapTree); `status == Open`; no violations / parcels / processing.

## golden.json provenance
Hand-derived from the reference and cross-checked byte-identical against the interpreter over the fake.
