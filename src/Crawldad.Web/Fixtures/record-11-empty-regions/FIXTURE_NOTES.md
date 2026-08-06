# record-11-empty-regions — provenance & fidelity notes

## Variety focus
The **all-empty-regions** edge: only the mandatory record number + type are present; every region guard is
false, so every list is empty and the scalar fields take their empty/default values. Also exercises the
attachments frame binding an **empty document** (no `frames` map). `input.link` capID = `24ENF-00011`;
**no** `publishDate`.

## Golden derivation (hand-executed from `LJCMGClient.cs`)
- `#tbl_worklocation` absent ⇒ `count == 0` ⇒ `locations == []` (`:231`).
- `recordNumber = 24ENF-00011`, `recordType = Vacant Property` (labels present, guards pass, `:272-276`).
- No `#…lblRecordStatus` ⇒ `status == ""` (`:278-280`); no `publishDate` input ⇒ `recordDate == ""`
  (`coalesce(input.publishDate, '')`, `:289`).
- No `#tableCapTreeList` ⇒ `projectName == recordType = Vacant Property` (`:283-284`); `relatedRecords ==
  []`; `parentRecordNumber == ""`.
- `ownerDetails` absent ⇒ `count == 0` ⇒ `owners == []`, `description == ""` (`:291`).
- `#trASITList` absent ⇒ `violations == []` (`:361`); `#trParcelList` absent ⇒ `parcels == []` (`:429`).
- `#divProcessingTable` absent ⇒ `processingRows` count 0 ⇒ `processingStatus == []` (`:457-462`).
- The attachments frame selector has **no** `frames` entry, so the frame document is empty ⇒
  `attachmentRows` count 0 ⇒ the per-row loop is skipped, no next link ⇒ the do-while runs once and stops;
  `attachments == []` (`:540-621`).

## golden.json provenance
Hand-derived from the reference and cross-checked byte-identical against the interpreter over the fake.
