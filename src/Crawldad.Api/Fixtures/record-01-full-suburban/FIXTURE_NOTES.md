# record-01-full-suburban — provenance & fidelity notes

## What this is
A **synthesized** (not captured) FULL CapDetail enforcement record — the **whole-program** acceptance
fixture for `ScrapeEnforcementRecord` (Appendix B.2 / `LJCMGClient.cs:177-725`). Every region of
`RecordScrapedV1` is populated at once, so this is the primary proof that the assembled payload equals
the reference field-for-field. Read by `ScrapeRecordAcceptanceTests`. No Chromium, no live traffic.

`input.link` = `…/CapDetail.aspx?Module=Enforcement&capID=24ENF-00001&agencyCode=LJCMG`;
`input.publishDate` = `2025-06-15`.

## Golden derivation (hand-executed from the C# reference)
Each field was derived by executing the reference's algorithm over this DOM; every value below cites the
C# lines it reproduces. Leaf cells the payload reads **without** a `trim()` (the record labels, the
address `td:nth-child(2)`, the processing category + line-block `<td>`, the parcel col2 cells) are written
GAP-FREE so `TextContent`/`innerText`/`InnerHTML` carry no stray whitespace.

- **link / recordNumber / recordType / recordDate / status** (`:272-289,701-706`): `recordNumber` =
  `#…lblPermitNumber` textContent (untrimmed) = `24ENF-00001`; `recordType` = `#…lblPermitType` =
  `Property Maintenance`; `status` = `#…lblRecordStatus` (present) = `Open - Active`; `recordDate` =
  `publishDate` = `2025-06-15`.
- **projectName** (`:283-287`): `#tableCapTreeList` exists and has an `ACA_RelatedCap_Highlight` row, so
  projectName = `trim(text(Highlight > td:nth-child(3)))` = **`River Road Cleanup`**.
- **description** (`:297-300`): the ownerDetails **Description** section → `text(detail,'table td:nth-child(2)')`
  with `*` stripped and trimmed = `Illegal dumping of construction debris.`.
- **locations** (`:229-268`): one primary row, 3 `<br>` ⇒ `addressLines == 3`. `address1` =
  `span:first-child` with `*` removed = `100 RIVER RD`; the 3-branch cityStateZip =
  `Split(Split(html,"<br>")[1], "<span")[0]` = `LOUISVILLE, KY 40202`; address2 = `""`.
- **owners** (`:305-347`): one owner block. `name` = the inner data table's `tr:first-child td` with `*`
  removed = `RIVERSIDE HOLDINGS LLC` (`substring(name,1,2) != ")"`, so no contact-index strip);
  `ownerLines` (`table tr` count) = 3, so address2 stays `""`; address1 = `tr:nth-child(2) td` =
  `500 RIVER RD`; cityStateZip = `tr:last-child td` = `LOUISVILLE, KY 40202`. **These reads depend on
  the `:scope` descendant-scoping fix** (below).
- **violations** (`:363-412`): one VIOLATIONS table, one data row, the nine `k*2+1`/`k*2+2` key/value
  pairs map to title…referralResult exactly (values distinct per position). ⇒
  `Illegal Dumping / Open / PM-17 / 2025-07-01 / Cleanup required / Internal / 2025-07-05 / Standard / Pending`.
- **parcels** (`:436-449`): one parcel block; each value cell is `": <value>"` and the payload takes
  `Split(":")[1].Trim()` ⇒ `parcelNumber=072D00450000, block=12, lot=45, subdivision=RIVERSIDE ESTATES`
  (lot/subdivision are the col2's `div:first-child`/`div:last-child`).
- **processingStatus** (`:462-525`): one expandable activity (`Notice Issued`, the head row's untrimmed
  `td:last-child`). The line block's `<br>` becomes `\n` (FakeInnerText), split into two lines:
  `Due on 06/20/2025, assigned to Officer Ramirez` ⇒ dueDate `06/20/2025`, assignedTo `Officer Ramirez`;
  `Marked as Sent on 06/22/2025 by Clerk Adams` ⇒ status `Sent`, statusDate `06/22/2025`, statusBy
  `Clerk Adams`. The additional-comment row `Method:` ⇒ trailing-colon strip ⇒ `lines = { "Method":
  "Certified Mail" }`.
- **attachments** (`:540-594`): the grid is served inside the iframe (`states.detail.frames`); one
  downloadable row. `filename` = `Inspection Report.pdf`, `type`/`size`/`latestUpdate` = td 5/6/7. The
  in-frame file-link click downloads `sample.bin`; the engine hashes it to the pinned `contentId`
  **18dc2ee2-6e62-f5c6-8ec1-648aa28b2f48** (SHA-256 first-16 → GUID, = `AttachmentHashing`). `dl.stored`
  is true, so `internalFilename` = `{contentId}` + `.` + `substringAfterLast(filename,'.')` = `{contentId}.pdf`
  (from the **scraped** name, `:576`, not the download's suggested `report.pdf`).
- **relatedRecords / parentRecordNumber** (`:631-694`): a 3-node CapTree. Row1 (Normal, indent 0) and
  Row3 (Normal, indent 40) are added; the resolveUrl links resolve the relative anchors against
  `input.link`'s `/LJCMG/Cap/` directory. Row2 is the **Highlight** (indent 20) and sets the record's
  `parentRecordNumber` = `parents[max(key<20)]` = `parents[0]` = **24ENF-00090**. Row3's own parent =
  `parents[max(key<40)]` = `parents[20]` = `24ENF-00001`.

## ENGINE FIX this fixture exposed and depends on — `:scope` descendant-scoping
The owner block is itself a `<table class="table_child">`, and the reference reads its cells with the
chained locator `ownerBlock.Locator("table tr:first-child td")` / `"table tr"`. Playwright scopes a
chained locator to the base's **strict descendants**; AngleSharp's `element.QuerySelectorAll` matches the
leftmost compound against the whole document (the querySelectorAll ancestor-leakage gotcha), so the
leftmost `table` matched the owner block itself and read its own wrapper cell — the whole nested block —
instead of the inner name cell (verified: `name`/`address1`/`cityStateZip` all came back as the entire
block, and `ownerLines` counted 4 rows not 3). `FakeLocatorHandle.ScopeRelative` now anchors **every**
relative selector with `:scope ` (which also subsumes the leading-`>` child combinator), reproducing
Playwright's semantics. Pinned by `FakeBackendTests.Chained_child_locator_scopes_to_strict_descendants…`
and re-gated against real Chromium in Phase 4. The golden's `name = "RIVERSIDE HOLDINGS LLC"` (not the
concatenated block) is the proof the fix is active.

## golden.json provenance
Hand-derived from `LJCMGClient.cs:229-716`, then cross-checked by executing the full payload through the
interpreter over the fake backend (byte-identical). Phase 4 re-gates against real Chromium.
