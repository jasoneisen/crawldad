# record-02-four-line-owner — provenance & fidelity notes

## Variety focus
4-`<br>` address branch, a **contact-index-prefixed 4-line owner**, one violation, and a Normal-only
CapTree that drives `projectName == ""`. `input.link` capID = `24ENF-00002`; `publishDate` = `2025-05-20`.

## Golden derivation (hand-executed from `LJCMGClient.cs`)
- **locations** (`:244-248`, 4-branch): cell innerHTML `…</span><br>APT 4B<br>FRANKFORT, KY 40601<span…><br>`
  splits on `<br>` into 4 segments (length 4). address1 = `250 ELM ST` (`span:first-child`, `*` stripped);
  address2 = `Split[1]` = `APT 4B`; cityStateZip = `Split(Split[2],"<span")[0]` = `FRANKFORT, KY 40601`.
- **owners** (`:314-347`): the raw name cell is `1)ELM STREET PARTNERS`; `name.Length>=2 &&
  name.Substring(1,1)==")"` holds, so `name = name[2..].Trim()` = **`ELM STREET PARTNERS`** (the
  `substring(name,1,2)==')'` / `trim(substring(name,2))` strip, `:317`). `ownerLines == 4`, so address2 =
  `tr:nth-child(3) td` = `SUITE 100` (`:330-333`); address1 = `250 ELM ST`; cityStateZip =
  `tr:last-child td` = `FRANKFORT, KY 40601`.
- **projectName** (`:283-287`): `#tableCapTreeList` exists but has **no** `ACA_RelatedCap_Highlight` row,
  so the middle ternary yields **`""`**.
- **relatedRecords / parentRecordNumber** (`:635-688`): two Normal rows (indent 0 then 25). R1 parent =
  `""` (no shallower indent); R2 parent = `parents[max(key<25)]` = `parents[0]` = `24ENF-00050`. The
  record's own `parentRecordNumber` stays `""` (no Highlight row sets it). Links resolve relative
  `CapDetail.aspx?capID=…` against `input.link`'s `/LJCMG/Cap/` directory (`:672`).
- **violations** (`:389-397`): one row, the nine key ladders ⇒ `Fence Height / Open / ZON-4.2 / 2025-06-10
  / Exceeds 4ft / Internal / 2025-06-12 / Standard / Open`.
- **description** = `Fence exceeds height limit in front yard setback.` (`:299`); **status** =
  `Under Review`; **recordDate** = `2025-05-20`. No attachments iframe ⇒ `attachments == []`; no parcels
  / processing ⇒ `[]`.

## golden.json provenance
Hand-derived from the reference and cross-checked byte-identical against the interpreter over the fake.
