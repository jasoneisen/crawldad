# record-09-guard-redirect — provenance & fidelity notes

## Variety focus (TERMINAL — no result golden)
The **CapDetail-guard redirect** terminal case (`LJCMGClient.cs:199-206`): the record is not accessible,
so the guard fails, the run **fails terminally**, and it is **not retried**.

## Failure derivation (hand-executed from the reference)
`goto(input.link)` targets the record's `CapDetail.aspx` URL, but the manifest state's reported `url` is
`https://aca-prod.accela.com/LJCMG/Error.aspx?msg=RecordNotFound` (an invalid/broken-record redirect,
`:199`). The C# guard is `if (!page.Url.Contains("CapDetail.aspx", OrdinalIgnoreCase)) throw …`; B.2
expresses it as `guard { cond: contains(lower(pageUrl()), 'capdetail.aspx') }`. Since the Error URL has
**no** `capdetail.aspx` substring, the guard fails and raises:

- `status = "failed"`, `failure.class = "terminal"`, `failure.code = "record_not_accessible"`,
- `failure.message = "Record not accessible (redirected to /LJCMG/Error.aspx): {link}"`
  (`urlPath(pageUrl())` = the Error URL's absolute path).

A `terminal` `CrawldadFailureException` is excluded from `retryOn` (`timeout`/`pageCrashed` only), so the
run is **not** retried: the event stream is exactly `[RunStarted, RunFailed]` (no `RunAttemptFailed`) —
attempt count 1. Asserted by `ScrapeRecordAcceptanceTests.Guard_redirect_is_terminal_and_not_retried`.

## Documented nuance — the login-redirect variant
The reference's guard is a plain substring check. A **Login.aspx** redirect whose `?ReturnUrl=` echoes the
original `CapDetail.aspx` path would *contain* `CapDetail.aspx`, so it slips **past** this guard and
instead fails terminally at the record-number guard (`missing_record_number`, `:273`) — also a terminal,
non-retried failure. This fixture uses the clean `Error.aspx` case so it exercises the `:203` guard
itself; the login variant reaches the same terminal-not-retried outcome by the second guard.

## Provenance
Failure shape hand-derived from `LJCMGClient.cs:203-206`; verified against the interpreter over the fake.
