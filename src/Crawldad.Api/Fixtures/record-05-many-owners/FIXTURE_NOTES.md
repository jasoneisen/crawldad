# record-05-many-owners — provenance & fidelity notes

## Variety focus
**Many owners** (three owner blocks → the `MULTIPLE OWNERS` warning), **two locations**, one violation.
`input.link` capID = `24ENF-00005`; `publishDate` = `2025-06-01`.

## Golden derivation (hand-executed from `LJCMGClient.cs`)
- **owners** (`:307-347`): the Owner section's outer `table.table_child` holds three inner
  `table.table_child` blocks, so `count(ownerBlocks) == 3`. `count > 1` ⇒ a single `LogEmitted` warning
  `MULTIPLE OWNERS: {link}` (`:307-310`), asserted by the acceptance test. The three blocks yield, in
  document order:
  - `OWNER ONE LLC / 1 FIRST ST / "" / LOUISVILLE, KY 40201` (3 lines),
  - `OWNER TWO INC / 2 SECOND ST / "" / LOUISVILLE, KY 40202` (3 lines),
  - `OWNER THREE / 3 THIRD ST / APT 9 / LOUISVILLE, KY 40203` (**4 lines** ⇒ address2 set, `:330-333`).
- **locations** (`:233-264`): the comma-union locator matches the primary `tr:first-child` **and** the
  `tr[tips='tr_additional_locations']` row (document order), both 3-branch ⇒
  `[10 MAIN ST / "" / LOUISVILLE, KY 40201, 20 OAK AVE / "" / LOUISVILLE, KY 40202]`.
- **violations** (`:389-397`): `Overgrown Lot / Open / PM-9 / 2025-06-30 / Mow required / Internal /
  2025-07-01 / Standard / Pending`.
- **projectName** = `Property Maintenance` (no CapTree). **status** = `Open`. No parcels / processing /
  attachments ⇒ `[]`.

## golden.json provenance
Hand-derived from the reference and cross-checked byte-identical against the interpreter over the fake.
