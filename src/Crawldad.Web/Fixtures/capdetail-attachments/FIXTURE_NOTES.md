# capdetail-attachments fixture — provenance & fidelity notes

## What this is
A **synthesized** (not captured) fixture for the LJCMG CapDetail **attachments iframe** (frames,
`LJCMGClient.cs:531-621`): a `FrameLocator` bound to an `<iframe>`, an in-frame paginated grid, and
per-row downloads triggered by **in-frame** clicks. It exercises the Phase 3 seam additions
(`IPageHandle.FrameLocator` → `IFrameHandle.Locator`, `in:` on nodes/Sels) and the fake's frame
support. No Chromium, no live traffic.

- `page.html` — the outer CapDetail shell. The attachment grid is **not** in this DOM; it is the
  iframe's document (below). A page-level `#expandAttachments` tab (`title="Attachments"`) is present
  so a page click and an in-frame click can be told apart.
- `frame-p1.html` / `frame-p2.html` — the grid served **inside** the iframe on states `att1` / `att2`.
  Page 1: one file-link row + a `Next` pagination anchor + an `#frameInput`/`#frameBtn` for
  fill/clear/click-with-`in:`. Page 2: a `No records found.` row and no next link (the do-while stops).
- `sample.bin` — the download body (30 bytes; same content vector as `download-sample`, so `dl.stored`
  and the composed `internalFilename` are assertable).

## manifest schema extension (Phase 3 — frames)
Two optional keys were added; both default to "absent" so every existing manifest keeps working.

**1. Per-state frame content** — `states.<name>.frames` maps an iframe element's CSS selector to the
HTML file served as that frame's document. Because it is per-state, an in-frame pagination transition
to a new state swaps the grid the frame serves — reproducing the attachments postback re-render.
```jsonc
"states": {
  "att1": {
    "url": "…/CapDetail.aspx?…",
    "html": "page.html",
    "frames": {
      "#ctl00_PlaceHolderMain_attachmentEdit_iframeAttachmentList": "frame-p1.html"
    }
  }
}
```
A frame selector not present in the current state serves an **empty document** (locators find nothing,
`count` 0) — Playwright's "frame absent" resolving to an empty match set.

**2. In-frame click scope** — `transitions[].on.in` names the iframe a click happens inside; the click's
frame must match the transition's `in` for it to fire (an in-frame click never triggers a page-level
transition and vice versa). `on.in` absent = a page-level click (unchanged behaviour). The click
selector is resolved against the named frame's document.
```jsonc
{
  "from": "att1",
  "on": {
    "click": "#attachmentList_gdvAttachmentList > … > td:last-child > a",  // the "Next" anchor
    "in": "#ctl00_PlaceHolderMain_attachmentEdit_iframeAttachmentList"     // clicked inside this iframe
  },
  "to": "att2"                                                             // the frame re-renders to page 2
}
```
A download transition may also be in-frame: `on.in` + `download` — the per-row file link is clicked
inside the iframe, but the download is still captured at the page (`page.WaitForDownloadAsync` +
`fileLink.ClickAsync`, `:560-562`).

## Full schema (as of Phase 3)
- `states.<name>`: `{ gotoUrl?, url, html, frames?: { "<iframeSelector>": "<htmlFile>" } }`
- `transitions[]`: `{ from, on: { click, in? }, to, emit?, inject?, download? }`

## Laziness (why this reproduces the reference)
`FrameLocator` and every locator it yields are **lazy** (Playwright semantics): they re-query the
frame's *current* document on each terminal call. So the row/pagination handles bound on page 1
resolve page 2's freshly rendered grid after the `Next` click, with no rebind — exactly what the
reference's `do…while` relies on (`:538-621`).
