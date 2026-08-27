# capdetail-address fixture — provenance & fidelity notes

## What this is
A **synthesized** (not captured) record/replay fixture for the LJCMG CapDetail **LOCATION** region
(`LJCMGClient.cs:229-268`) — the 3/4/5-`<br>` address branch. Single-state manifest; the
address-fragment payload (`tests/Crawldad.Tests/Fixtures/Payloads/address-fragment.json`) reads it
with no transitions. Phase 2 WP3 acceptance fixture: no Chromium, no live traffic.

## The four branches exercised
`addressLines = InnerHTML.Split("<br>").Length` selects the branch. `page.html` has four matched rows
(`#tbl_worklocation tr:first-child, #tbl_worklocation tr[tips='tr_additional_locations']`):

| Row | `<br>` count | addressLines | Branch | address1 | address2 | cityStateZip |
|---|---|---|---|---|---|---|
| 1 (`tr:first-child`) | 2 | 3 | `==3` | `100 FIRST ST` | `` | `LOUISVILLE, KY 40202` |
| 2 (additional) | 3 | 4 | `==4` | `200 SECOND AVE` | `SUITE 5` | `PORTLAND, OR 97201` |
| 3 (additional) | 4 | 5 | `==5` | `300 THIRD BLVD` | `UNIT 12` | `SEATTLE, WA 98101` |
| 4 (additional) | 1 | 2 | `else` | `400 FOURTH ST` | `` | `` |

Row 4 is the exceptional-count case. Read the C# carefully: `address1` is still computed from
`span:first-child` **before** the branch, and the default only logs a warning — so what gets pushed is
`{ address1: <span text>, address2: "", cityStateZip: "" }`, and a `LogEmitted` warning
`Exceptional location address lines (2): <link>` fires. The integration test asserts both.

## Deliberate DOM choices (honest to Chromium)
- **`*` characters** in the first `<span>` exercise `.Replace("*","")`; a trailing `<span> View Map</span>`
  glued onto the city/state/zip line exercises `.Split("<span")[0]` (rows 1, 2, 4). In the `==5` branch
  the C# does **not** `.Split("<span")` the cityStateZip segment (it splits the *country* segment
  instead), so row 3's cityStateZip is clean.
- **`td:nth-child(2)` cells are written GAP-FREE on one source line.** `InnerHTMLAsync` returns the
  serialized DOM including whitespace text nodes (exactly as a browser does), so any source indentation
  inside the cell would land in the `<br>`-split segments. Writing them gap-free makes the split
  arithmetic exact. Verified against AngleSharp: `<span>…</span><br>…<span>…</span>` round-trips
  verbatim, and `<br>`/`<span` serialize lowercase with no self-closing slash — matching the C# split
  targets `"<br>"` / `"<span"`. (`.Trim()` on each field absorbs any residual edge whitespace anyway.)

## Ordering guarantee
`locations` follows **document order** of the matched rows: the comma-union locator
(`tr:first-child, tr[tips=…]`) returns matches in document order (AngleSharp `QuerySelectorAll`), so the
primary location is first, then the additional locations top-to-bottom — matching the C# `AllAsync()`
+ `foreach` + `addresses.Add`.

## golden.json provenance
Derived by **hand-executing the C# reference** (`LJCMGClient.cs:236-264`) over this DOM, then
cross-checked by running the fragment payload through the real interpreter over the fake backend
(byte-identical). Real-Chromium parity is the Phase 4 re-gate.
