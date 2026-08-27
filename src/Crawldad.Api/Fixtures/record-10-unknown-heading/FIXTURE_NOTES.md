# record-10-unknown-heading — provenance & fidelity notes

## Variety focus (TERMINAL — no result golden)
The **unknown-heading** terminal case (`LJCMGClient.cs:350-353`): an owner-details section carries a
heading the switch does not recognise, so the payload's `switch` **default** fails terminally and the run
is **not retried**.

## Failure derivation (hand-executed from the reference)
The LOCATION region (3-branch `42 UNKNOWN RD / … / LOUISVILLE, KY 40218`) and RECORD DETAILS parse cleanly
(both record-number and record-type guards pass, `:273/:276`). Then `ownerDetails` matches one `<td>`
whose `<h1>` is `Contact Information`. In the C#, `heading` starts with neither `description`,
`licensed professional`, nor `owner`, so the final `else throw new Exception($"UNKNOWN HEADING: {heading}
AT {link.Id}")` (`:350-353`) fires. B.2 reproduces this as the owner `switch`'s `default`:
`fail { class: "terminal", code: "unknown_heading", message: "UNKNOWN HEADING: ${heading} AT ${input.link}" }`.

Result: `status = "failed"`, `failure.class = "terminal"`, `failure.code = "unknown_heading"`,
`failure.message = "UNKNOWN HEADING: Contact Information AT {link}"`. A terminal `fail` is excluded from
`retryOn`, so the event stream is exactly `[RunStarted, RunFailed]` (attempt count 1). Asserted by
`ScrapeRecordAcceptanceTests.Unknown_owner_heading_is_terminal_and_not_retried`.

## Provenance
Failure shape hand-derived from `LJCMGClient.cs:350-353`; verified against the interpreter over the fake.
