# Crawldad — Security notes

> Companion to `CRAWLDAD_DESIGN.md` §12 (Security). This file records the credential-handling boundary
> as built in Phase 4 (WP1 credential-by-reference + connect scrubbing; WP3 the scrubbing boundary at
> every sink and its leak tests). Terse and factual by design.

## Credentials by reference

Payloads and events never carry a raw secret. A backend binding carries a **credential reference**
(`credentialRef`) — an id into an `ISecretStore` (`Infrastructure/Security/ISecretStore.cs`). The
secret is resolved **only at connect time**, by the adapter, and lives solely in the interpreter's
memory for the session. `apiKey` mode stores a vault ref; `connectUrl` mode treats the whole URL as a
one-time secret (still scrubbed — see below). The store's default backing is configuration
(`Secrets:{ref}`), so any provider (user-secrets, env, a mounted vault file) supplies secrets without
a code change.

## The Browserbase `connectUrl` is NOT safe to leak (re-verified)

Verified shape: `wss://connect.browserbase.com?apiKey=bb_live_…&sessionId=ses_…`. The default CDP
`connectUrl` **embeds the account `apiKey` in its query string**. It is ephemeral in *session
lifetime* but **not credential-isolated**: a holder can read the key out and mint more sessions.
`connectUrl` mode therefore reduces credential *storage* (no long-lived key in our vault) but not
*blast radius on leak*. It is scrubbed **exactly like an `apiKey`**; the session-create response's
separate `signingKey` is likewise secret. Do not describe `connectUrl` as "safe to leak" anywhere.
The Browserless `token` is account-scoped (a leaked token drains the account's unit balance) — same
rules.

## The scrubbing primitive

`Infrastructure/Security/CredentialScrubber.cs` is the single scrubber. Two rules, idempotent, and a
no-op on ordinary text:

1. **Exact secret** — any credential resolved for the *current run* (registered by the connecting
   adapter, below) is replaced wherever it appears, catching free-form text no param rule would
   recognise (a `log` message echoing an input, an exception, a scraped page that echoes the value).
2. **Known credential params** — the values of `apiKey`, `token`, `signingKey` (case-insensitive) are
   redacted anywhere they appear as `name=value`, which covers a `ws://`/`wss://` connect URL's query
   and a JSON-embedded connect URL. Surrounding text (scheme, host, path, the non-secret `sessionId`)
   is preserved so the redaction stays diagnostic.

Redaction marker: `[redacted]`. The word "token" without `=value` is never touched (so the acceptance
goldens are byte-identical), and a value below a short length floor is not exact-scrubbed (so a
pathologically short "secret" can't mangle common substrings — the param rule still redacts it).

## The per-run secret registry (exact-match lifetime)

`Infrastructure/Security/IRunSecretScope.cs` (`AmbientRunSecretScope`). The run's entry point
(`POST /runs`) opens a scope for the whole run; the connecting adapter **registers the resolved
secret** (a token, an apiKey, and the apiKey-embedding `connectUrl`) into it; every sink's scrubber
consults it. The current scope is ambient (`AsyncLocal`), so it flows down the run's async call chain,
concurrent runs never see one another's secrets, and **no secret outlives its run**: disposing the
scope at run end clears the registered secrets, and the scrubber itself holds only a reference to the
seam — never a secret. A short length floor guards the exact-match rule.

## What is scrubbed, and where (one chokepoint per sink)

| Sink | Chokepoint | Note |
|---|---|---|
| **Connect exceptions** | the adapters (`Real/Browserless…`, `Real/Browserbase…`) | any provider fault becomes a hand-written, secret-free `BrowserConnectException` (WP1); the raw `wss://…?token=` URL is never wrapped in. |
| **Marten events** | `Features/Runs/RunEventScrubber` at the `POST /runs` append (`StartRunEndpoint`) | `RunStarted` (payload name + input key names), `LogEmitted` (message), `RunFailed` (failure message) are scrubbed before append. Input *values* are never persisted (metadata only, §12). |
| **Projections** | — (by construction) | the `Run` snapshot and the future `RunTimeline`/summary read models derive purely from already-scrubbed events, so they inherit the guarantee; they add no un-scrubbed field. |
| **Logs** | `Infrastructure/Security/ScrubbingLoggerFactory` (decorates `ILoggerFactory` in `HostConfiguration`) | every category's logger — application, Wolverine, Marten, ASP.NET — scrubs its rendered message before any sink writes it. Central, not per-call-site. |
| **HTTP response** | `StartRunEndpoint` | the failure message (same scrubbed value as the event) and the shaped `result` (`ScrubJson`) — `result` is caller data a scraped page could echo. Credential-free results are byte-identical to their golden. |
| **SSE / trace artifacts** | — (by construction, Phase 5) | neither exists yet; both render from events/projections that are already scrubbed, so they inherit scrubbing without a speculative hook. |

## Leak test (the phase gate)

`tests/Crawldad.Tests/Integration/CredentialLeakTests.cs` runs a payload through `POST /runs` with a
distinctive sentinel as the resolved credential, driving the real WP1 `browserless` adapter against a
loopback Playwright `run-server` (token in the ws URL) and the `browserbase` adapter against a
loopback CDP endpoint + session-create stub — **zero live third-party traffic**. The payload
adversarially interpolates the sentinel into a `log` message and the shaped `result`. It then asserts
the sentinel appears in **no** event (all events dumped, plus raw `data::text` from the Marten
tables), projection/document row, captured log line (framework categories included), or response
body. A failure-path variant fails the connect with the sentinel embedded in a bad-port `connectUrl`
and asserts the same.

## Not in scope here

Auth/authz (the tenant boundary is designed, §12, not yet built), PII crypto-shredding, and resource
limits are tracked in `CRAWLDAD_DESIGN.md` §12 and are not part of the WP3 scrubbing boundary.
