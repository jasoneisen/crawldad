# capdetail-violations fixture — provenance & fidelity notes

## What this is
A **synthesized** (not captured) record/replay fixture for the LJCMG CapDetail **APPLICATION
INFORMATION TABLE** region (`LJCMGClient.cs:359-425`) — the nested `k*2+1`/`k*2+2` violations ladder.
Single-state manifest; read by `violations-fragment.json`. Phase 2 WP3 acceptance fixture.

## Structure
`#trASITList > td` holds **three** tables (`table:nth-child(1..3)`):
1. `VIOLATIONS` — heading row + **2 violation rows** (`tbody > tr` count 3, loop `j = 2..3`).
2. `CODE ENFORCEMENT BOARD` — ignored by the reference (`:414-417`), no-op.
3. `CONTACTS` — an **unknown heading** → `LogEmitted` warning `Unknown heading in application
   information table` (`:420`). The integration test asserts exactly this one warning.

Each violation row's container `<div>` holds **10 alternating** `MoreDetail_ItemCol1` (key) /
`MoreDetail_ItemCol2` (value) `<div>`s: the nine reference key ladders plus a 10th **unmatched**
`Notes` pair that is silently ignored (the C# `if/else-if` chain and the payload `switch` both have
**no default** — verified).

## Off-by-one hardening (the point of this fixture)
`count(div.MoreDetail_ItemCol1)` gives the pair count `k`; the key is `div:nth-child(k*2+1)` and the
value is `div:nth-child(k*2+2)`, **1-based**. Every value is **distinct per position**, so any
off-by-one in the arithmetic (or a 0-based/1-based slip) would land a value where a key is expected,
match no ladder, and blank the field — the golden would change and the test would fail. The two rows
carry the same keys with different values, so a row-index slip is caught too.

`:nth-child()` counts element siblings and ignores text nodes, so this markup is formatted for
readability; the container `<div>` holds **only** the alternating `<div>`s (no other element children)
so the position arithmetic is exact. Field values are `.Trim()`-ed, so intra-cell whitespace is safe.

## Key-ladder matching
Keys are lowercased then prefix-matched (first-true-wins), reproducing the C# `StartsWith(…, IgnoreCase)`
ladder: `violation/status/code/due date/inspector co/referral typ/referral dat/work order t/referral res`.
The chosen keys (`Inspector Comments`, `Referral Type`, `Referral Date`, `Work Order Type`,
`Referral Result`) each match exactly one prefix and nothing earlier.

## Ordering guarantee
`violations` follows table order then row order (`i` outer over tables, `j` inner over `tr:nth-child`),
matching the C# nested loops.

## golden.json provenance
Hand-derived from `LJCMGClient.cs:372-411`, cross-checked against the interpreter over the fake backend
(byte-identical). Phase 4 re-gates against real Chromium.
