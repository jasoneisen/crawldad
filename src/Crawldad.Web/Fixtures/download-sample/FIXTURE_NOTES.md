# download-sample fixture — provenance & fidelity notes

## What this is
A **synthesized** (not captured) single-page fixture for the `download` action, the
tension (b) reshaping: downloads stream to a caller `Target`, engine-hashed and idempotent,
reproducing `LJCMGClient.cs:559-594` (`WaitForDownloadAsync` → `AttachmentHashing` →
`handleDownload`) **without** the iframe/pagination machinery (that is Phase 3). No Chromium, no
live traffic.

- `manifest.json` — one state `page`; a self-loop transition whose click yields `sample.bin`'s bytes
  as a browser download.
- `page.html` — an attachment row with a file link; the first cell's text is the **scraped** filename.
- `sample.bin` — the download body (30 bytes of deterministic ASCII: `Crawldad sample attachment v1\n`).

## manifest schema extension (Phase 2 Deliverable 2)
A transition may now carry a `download` block and may omit `emit`:
```jsonc
{
  "from": "page",
  "on": { "click": "<css>" },      // the file link
  "to": "page",                     // a download link is a self-loop; state does not change
  "download": {
    "file": "sample.bin",           // fixture file whose bytes are the download body
    "suggestedFilename": "report.pdf" // the download's HTTP-suggested name (→ engine storedAs extension)
  }
  // no "emit": a pure download click fires no navigation postback
}
```
`RunAndWaitForDownloadAsync` arms before the trigger click (Playwright semantics); the click sets the
page's pending download, which the wait returns. A trigger that starts no download is a retryable
`BrowserTimeoutException` (the reference's 180 s wait).

## Pinned content identity (the golden vector)
The engine computes the identity natively, byte-for-byte as `AttachmentHashing`:
`contentId = new Guid(SHA256(bytes)[0..16])` (mixed-endian per the `Guid(ReadOnlySpan<byte>)`
constructor), and the stored-blob name `= BuildInternalFilename` on the **suggested** filename.

For `sample.bin` (`Crawldad sample attachment v1\n`, 30 bytes):

| field | value |
|---|---|
| SHA-256 | `e22edc18626ec6f58ec1648aa28b2f48fc168b6ce9defa3b40344b1eb22f789e` |
| `contentId` (first 16 bytes → Guid) | `18dc2ee2-6e62-f5c6-8ec1-648aa28b2f48` |
| `sizeBytes` | `30` |
| `storedAs` (engine, from suggested `report.pdf`) | `18dc2ee2-6e62-f5c6-8ec1-648aa28b2f48.pdf` |
| `internalFilename` (payload, from scraped `Site Photo.jpg`) | `18dc2ee2-6e62-f5c6-8ec1-648aa28b2f48.jpg` |

**GUID byte order (why `18dc2ee2…` and not `e22edc18…`):** `new Guid(byte[16])` reads the first
three fields little-endian and the last eight bytes in order. With hash bytes
`e2 2e dc 18 | 62 6e | c6 f5 | 8e c1 | 64 8a a2 8b 2f 48` that yields
`18dc2ee2-6e62-f5c6-8ec1-648aa28b2f48`. Pinned in `AttachmentContentIdTests`.

## The storedAs / internalFilename split
The download's suggested name (`report.pdf`) and the scraped filename cell (`Site Photo.jpg`) carry
**different extensions on purpose**. The engine's `storedAs` uses the suggested name (`.pdf`); the
payload composes `internalFilename` from the scraped cell (`.jpg`), exactly as the reference builds
`InternalFilename` from the scraped `filename`, not the download's own name. Same `contentId`, two
different filenames — the distinction calls out.

## Idempotency
Clicking the same link twice yields identical bytes → identical `contentId`. The first download finds
the sink empty (`exists`→false) and uploads; the second finds it present (`exists`→true) and
**short-circuits to `stored:true` with no re-upload**. Asserted via `FakeDownloadSink.StoreCalls`
(stays at 1 across two downloads) in `DownloadNodeTests`.
