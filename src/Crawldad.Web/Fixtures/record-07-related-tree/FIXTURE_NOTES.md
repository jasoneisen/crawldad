# record-07-related-tree — provenance & fidelity notes

## Variety focus
A **non-trivial related-record tree** — the multi-level indentation parent-resolution walk (reused from
`capdetail-related`), embedded in a full record. Also a 4-`<br>` address, one owner, one violation.
`input.link` capID = `24ENF-00007`; `publishDate` = `2025-03-01`.

## The tree (why it proves the walk) — `LJCMGClient.cs:635-694`
`#tableCapTreeList > tbody > tr:not(:first-child)` yields 7 rows. `parents` is a map `indent → recordNumber`
that mutates as rows are visited; each row's parent = `parents[max(key < indent)]`.

| Row | width | indent | class | recordNumber | parent | note |
|---|---|---|---|---|---|---|
| R1 | 0px | 0 | Normal | REC-ROOT | `""` | no shallower indent |
| R2 | 25px | 25 | Normal | REC-A | REC-ROOT | |
| R3 | 50px | 50 | **Highlight** | REC-B | → record's **parentRecordNumber = REC-A** | max(key<50)=25 |
| R4 | 25px | 25 | Normal | REC-C | REC-ROOT | **overwrites** parents[25] |
| R5 | 50px | 50 | Normal | REC-D | **REC-C** | max(key<50) is now 25 → REC-C, not REC-A |
| R6 | foo | 0 (fallback) | Normal | REC-E | `""` | garbled width → **error log**, `isInt?…:0` |
| R7 | 75px | 75 | *(neither)* | REC-F | — | unknown class → **error log**, excluded |

So `relatedRecords = [R1, R2, R4, R5, R6]` (Highlight and unknown-class rows excluded), and the record's
`parentRecordNumber = REC-A`. R3 and R5 both sit at indent 50 yet resolve to different ancestors because
R4 rewrote `parents[25]` between them — the order-dependent walk the gate names. Two `LogEmitted` **error**
events fire (R6 indentation, R7 class), asserted by the acceptance test.

- **projectName** (`:285-287`): the Highlight row's `> td:nth-child(3)` = **`Highlighted Project`**.
- **links** (`:672`): each `resolveUrl(input.link, "CapDetail.aspx?x=…")` replaces `input.link`'s last
  path segment + query within `/LJCMG/Cap/` ⇒ `…/CapDetail.aspx?x=root` etc. (independent of the base's
  own `capID`, so identical to the `capdetail-related` golden).
- The `> td:nth-child(N)` direct-child reads depend on the `:scope`-anchoring in `FakeLocatorHandle`
  (leading `>` ⇒ `:scope >`).

## Other regions
4-branch address `77 COURT ST / FLOOR 2 / LEXINGTON, KY 40507`; owner `COURT STREET LLC`; one violation
`Multiple Violations / Open / MULTI-1 / …`; `status == Open`; no parcels / processing / attachments.

## golden.json provenance
Hand-derived from `LJCMGClient.cs:637-688` and cross-checked byte-identical against the interpreter over
the fake (the related region matches the pinned `capdetail-related` golden).
