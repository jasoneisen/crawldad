# record-04-no-owners — provenance & fidelity notes

## Variety focus
**Zero owners**, an attachments iframe whose only data row is the **"No records found."** placeholder,
3-`<br>` address, `projectName == recordType`. `input.link` capID = `24ENF-00004`; `publishDate` =
`2025-04-01`.

## Golden derivation (hand-executed from `LJCMGClient.cs`)
- **owners** (`:291-355`): `ownerDetails` matches one `<td>` — a **Description** section only (no Owner
  heading). The `description` branch sets `description = Accumulation of junk and debris on vacant lot.`
  (`:297-300`); no Owner branch runs ⇒ `owners == []`. (Note this exercises the description path with an
  empty owner list — the reference's `owners` stays empty because no `owner`-headed detail exists.)
- **attachments** (`:544-548`): the grid has a header row + one `No records found.` data row.
  `count(attRows) > 1` is true (2 rows), so the per-row loop runs once for `tr:nth-child(2)`, whose
  trimmed textContent equals `No records found.` ⇒ `continue` (`:548`). No file link is clicked ⇒
  `attachments == []`. No pagination row ⇒ the do-while stops after one page.
- **locations** (3-branch): `15 CEDAR LN / "" / SHELBYVILLE, KY 40065`.
- **projectName** (`:283-284`): no `#tableCapTreeList` ⇒ `Nuisance Abatement` (= recordType);
  relatedRecords `[]`. **status** = `Closed`. No violations / parcels / processing ⇒ `[]`.

## golden.json provenance
Hand-derived from the reference and cross-checked byte-identical against the interpreter over the fake.
