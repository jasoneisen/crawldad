# capture-sample

Synthetic (not recorded) fixture for the `capture` node and `config.captureOnFailure`.

- **`page.html`** — a minimal parcel-detail document: a `<!DOCTYPE html>` + `<html>` wrapper, a `#content`
  subtree (heading, owner name, detail link), and a `<footer>` outside `#content`. Two things the tests turn on:
  - **Full vs. subtree.** A full-document `capture` serialises the whole document — doctype **and** the `<html>`
    element itself — not `innerHtml('html')`. A `capture` with `selector: "#content"` serialises that element's
    `outerHTML` subtree only (no `<html>`, `<head>`, or `<footer>`).
  - **Scrubber bypass (#70).** The detail link's `href` carries a credential-**shaped** `token=abc123SECRETtoken`
    query param. It is the customer's own scraped content going to the customer's own storage, so a capture streams
    it **verbatim** — the `CredentialScrubber` param regex (which would rewrite `token=…` to `[redacted]` in a
    result-borne `innerHtml`) never runs against captured bytes.

- **`manifest.json`** — a single state, no transitions. `capture` reads the current document; a run that `fail`s
  after navigating here exercises `captureOnFailure`.

No Chromium, no live traffic — AngleSharp parses and serialises `page.html` in-process.
