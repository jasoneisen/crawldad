# record-03-five-line-harbor — provenance & fidelity notes

## Variety focus
5-`<br>` address branch, **no** record-status label (`status == ""`), **no** CapTree
(`projectName == recordType`, relatedRecords empty), one iframe attachment download with a
**distinct** content vector. `input.link` capID = `24ENF-00003`; `publishDate` = `2025-05-25`.

## Golden derivation (hand-executed from `LJCMGClient.cs`)
- **locations** (`:249-254`, 5-branch): cell innerHTML `…</span><br>BLDG C<br>NEWPORT, KY 41071<br>USA<span…><br>`
  splits into 5 segments. address1 = `88 HARBOR DR`; address2 = `Split[1]` = `BLDG C`; cityStateZip =
  `Split[2]` = `NEWPORT, KY 41071` — the 5-branch takes `[2]` **directly, with no `Split("<span")`**
  (only the dead `country = Split[3]` segment, omitted per B.2, would carry the trailing `<span>`).
- **status** (`:278-280`): no `#…lblRecordStatus` element ⇒ `count == 0` ⇒ `status == ""`.
- **projectName** (`:283-284`): no `#tableCapTreeList` ⇒ projectName = `recordType` =
  `Shoreline Enforcement`; relatedRecords is `[]` (the `count(relatedBlocks) > 1` guard is false).
- **owners** (`:314-347`): one 3-line owner ⇒ `HARBOR VIEW TRUST / 88 HARBOR DR / "" / NEWPORT, KY 41071`.
- **attachments** (`:540-594`): one row `Dock Permit.pdf`, type `PDF`, size `1.1 MB`, latestUpdate
  `05/10/2025`. The download body `harbor.bin` hashes to `contentId` (below); `internalFilename` =
  `{contentId}` + `.pdf` (scraped extension, `:576`).

## contentId computation (§9.3, = `AttachmentHashing.AttachmentIdFromHash`)
`harbor.bin` = ASCII `"Crawldad harbor permit attachment v1\n"` (37 bytes).
`contentId = new Guid(SHA256(bytes)[0..16])` (mixed-endian) = **`8597a392-983c-caea-6e98-696dd59c00b7`**.
The download's suggested name is `dock.pdf` (engine `storedAs = {contentId}.pdf`), deliberately distinct
from the scraped `Dock Permit.pdf` the payload uses for `internalFilename` — both `.pdf` here, same GUID.

## golden.json provenance
Hand-derived from the reference and cross-checked byte-identical against the interpreter over the fake.
