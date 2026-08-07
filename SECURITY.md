# Crawldad — Security notes

> Companion to `CRAWLDAD_DESIGN.md` §12 (Security). This file records the credential-handling boundary
> as built in Phase 4 (WP1 credential-by-reference + connect scrubbing; WP3 the scrubbing boundary at
> every sink and its leak tests) and extended in Phase 5 (the background executor opens the run's secret
> scope; the SSE / `RunTimeline` / screenshot sinks are now real and leak-tested; the durable at-rest
> surfaces are documented below). Terse and factual by design.

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

`Infrastructure/Security/IRunSecretScope.cs` (`AmbientRunSecretScope`). A scope is opened at the run's
**execution** entry — inline in `POST /runs` for a synchronous run, and by the **background executor**
(`RunExecutor.DriveAsync`, WP2) for an async run, where it spans the whole execution including retries
and a checkpoint resume (a fresh `ConnectAsync` re-registers the resolved credential naturally, so a
resumed run rebuilds its scrub set with no persisted secret). The connecting adapter **registers the
resolved secret** (a token, an apiKey, and the apiKey-embedding `connectUrl`) into it; every sink's
scrubber consults it. The current scope is ambient (`AsyncLocal`), so it flows down the run's async call
chain, concurrent runs never see one another's secrets, and **no secret outlives its run**: disposing the
scope at run end clears the registered secrets, and the scrubber itself holds only a reference to the
seam — never a secret. A short length floor guards the exact-match rule.

## What is scrubbed, and where (one chokepoint per sink)

| Sink | Chokepoint | Note |
|---|---|---|
| **Connect exceptions** | the adapters (`Real/Browserless…`, `Real/Browserbase…`) | any provider fault becomes a hand-written, secret-free `BrowserConnectException` (WP1); the raw `wss://…?token=` URL is never wrapped in. |
| **Marten events** | `Features/Runs/RunEventScrubber`, at both append paths: the `POST /runs` append (`StartRunEndpoint`, sync) and the executor's own-session append (`RunExecutor.ExecutorObserver`, async, WP2) | Every event is scrubbed before append: `RunStarted` (payload name + input key names), `LogEmitted`, `RunFailed`, and the WP3 step trace (`Navigated`/`Clicked`/`Extracted`/`Downloaded`/`StepFailed`/`RunSessionOpened`). Input *values* are never persisted; `Extracted` carries a shape ref, `Downloaded` a blob ref, `StepFailed` a screenshot ref — metadata only (§12). |
| **Payload events** | `Features/Payloads/PayloadScript.Scrub` at save (`DraftPayloadEndpoint`/`RevisePayloadEndpoint`) | a payload's script *is* the stored artifact (§14.1), so it is scrubbed **before** the `PayloadDrafted`/`PayloadRevised` event is built and hashed — the immutable event store can never receive a credential. A well-formed payload (credentials are by reference) scrubs to a no-op, so the stored bytes stay executable and golden-identical. |
| **Projections** | — (by construction) | the `Run` snapshot, the `RunTimeline` read model (§13), and the `PayloadSummary` list derive purely from already-scrubbed events, so they inherit the guarantee; they add no un-scrubbed field (`PayloadSummary` carries no script body at all). |
| **Logs** | `Infrastructure/Security/ScrubbingLoggerFactory` (decorates `ILoggerFactory` in `HostConfiguration`) | every category's logger — application, Wolverine, Marten, ASP.NET — scrubs its rendered message before any sink writes it. Central, not per-call-site. |
| **HTTP response** | `StartRunEndpoint` (sync), `GetRunEndpoint`/`RunProgress` (async poll) | the failure message (same scrubbed value as the event) and the shaped `result`/`partial` (`ScrubJson`) — caller data a scraped page could echo. Credential-free results are byte-identical to their golden. |
| **SSE frames** | `Features/Runs/RunEventsEndpoint` (`RunEventFrames.Format`) | each frame's `data` is the already-scrubbed persisted event re-serialised; the **durable stream** (not an in-memory buffer) is the frame source, so a frame can carry nothing the event did not. |
| **`RunTimeline` + `GET /runs/{id}/timeline`** | — (by construction) | folds the already-scrubbed step trace; surfaces extracted-value refs, blob refs, region, and the screenshot ref — never a raw value or the image. |
| **Screenshot blobs** | `Infrastructure/Storage/IScreenshotStore` (content-addressed ref) | the failing page's image lives in a **deletable** blob store; the immutable `StepFailed` event holds only the `screenshots/{sha256}.png` ref (§12 PII). |

## Durable state at rest (Phase 5) — inputs by reference, not extracted PII

The background executor (§11/§14.2) leaves run state in three durable stores beyond the immutable trace. None is a
credential sink — credentials are **by reference** (`credentialRef`), so only the ref travels — but this is what sits
where, and for how long, stated honestly:

| At-rest store | Holds | Credential-scrubbed? | Retention |
|---|---|---|---|
| **`RunProgress`** (`mt_doc_runprogress`) | the pollable `result`/`partial`/`failure` body and the durable checkpoint (cursor + accumulated-var snapshot) | **yes** — result/partial/checkpoint are scrubbed before store; the body is bulk data deliberately kept out of the immutable trace (§12), and any *extracted PII* it holds lives here (deletable), never in an event | deletable, mutable, executor-owned document |
| **`RunExecutorSaga`** (`mt_doc_runexecutorsaga`) | the run's `script` + `inputs` — the resume source (re-run + re-connect after a restart) | no (inputs are caller config; credentials among them are refs) | **lingers indefinitely** — the saga is never marked complete, so a finished run's inputs+script remain at rest until the schema is dropped |
| **Wolverine durable envelopes** (`wolverine_incoming/outgoing_envelopes`, `wolverine_dead_letters`) | `StartRun` carries `script` + `inputs`; `ExecuteRun` / `RunDeadline` carry only the run id | no | handled `StartRun`/`ExecuteRun` are retained until Wolverine's `keep_until` sweep; the scheduled `RunDeadline` sits until its wall-clock delay fires; a dead-lettered `ExecuteRun` persists until cleared |

The invariant that holds at rest: **inputs travel by reference**, so `credentialRef` — not the resolved secret — is what
these stores contain. `CredentialLeakTests.An_async_by_reference_run_keeps_the_resolved_secret_out_of_the_saga_and_wolverine_envelopes`
drives a by-reference run and asserts the resolved secret appears in the `RunExecutorSaga` document and every Wolverine
envelope body **nowhere**, while the `credentialRef` *is* present — proving the boundary at rest, not just at the sinks.
The honest corollary (see the final "Designed, not built" note): a raw secret passed as a plain **input value** would sit
un-scrubbed in the `inputs` carried by `StartRun` and the saga (and re-read on resume) — which is exactly why form-fill
credentials must become a `secretRef` type, never a plain input.

## Leak test (the phase gate)

`tests/Crawldad.Tests/Integration/CredentialLeakTests.cs` runs a payload through `POST /runs` with a
distinctive sentinel as the resolved credential, driving the real WP1 `browserless` adapter against a
loopback Playwright `run-server` (token in the ws URL) and the `browserbase` adapter against a
loopback CDP endpoint + session-create stub — **zero live third-party traffic**. The payload
adversarially interpolates the sentinel into a `log` message and the shaped `result`. It then asserts
the sentinel appears in **no** event (all events dumped, plus raw `data::text` from the Marten
tables), projection/document row, captured log line (framework categories included), response body,
**SSE frame, `RunTimeline` row, or screenshot key/byte** (the WP3 sinks). Variants cover the failure
path (a connect failed with the sentinel in a bad-port `connectUrl`), the **async/checkpoint** path
(the durable `RunProgress` checkpoint cursor + var snapshot swept), a **failing async run that captures
a real screenshot**, and the **durable at-rest surfaces** above (the `RunExecutorSaga` document +
Wolverine envelope bodies swept for a by-reference credential). Separately, the metadata-only trace
discipline for non-credential **bulk PII** is re-asserted by
`RunObservabilityTests.The_trace_stream_holds_no_raw_extracted_or_input_value_only_metadata_refs`: a
known extracted value reaches the result body but appears in **no** trace event, SSE frame, or timeline
row (the scrubber never touches it — its absence is the discipline itself, not a redaction).

## Not in scope here

Auth/authz (the tenant boundary is designed, §12, not yet built), PII crypto-shredding, and resource
limits are tracked in `CRAWLDAD_DESIGN.md` §12 and are not part of the WP3 scrubbing boundary.

## Designed, not built: BYO key vault + `secretRef` form-fill credentials (post-P5)

The first login-gated target needs a credential typed into a **form** (`fill`), not a backend
binding — and today the only by-reference machinery is `credentialRef` → `ISecretStore` at connect.
Passing a password as a plain run input is not acceptable: checkpoint resume (§11) keeps inputs
durably at rest — in the `StartRun` envelope and the `RunExecutorSaga` document ("Durable state at
rest" above) — invisible to the scrubber (which redacts registered run secrets and credential-shaped
params, not arbitrary values). The designed answer, agreed 2026-08-07:

- **BYO vault.** `ISecretStore` becomes a keyed-adapter registry (the same pattern as backends and
  storage targets): `config` (today's `Secrets:{ref}` backing), then `azure-keyvault` /
  `aws-secretsmanager` / `hashicorp-vault` / a customer HTTP endpoint. The customer's vault is the
  sole custodian; a vault binding is declared like a `storageTarget` (`{kind, name, options}`).
- **`secretRef` input type.** The input's value is the *reference string only*. Durable messages,
  events, projections, and payloads carry the ref, never the secret — a Crawldad operator compromise
  leaks references, not credentials.
- **Secrets stay out of the expression value space.** A dedicated secret-valued action field (e.g.
  `fill: { sel, secret: "input.loginPassword" }`), never `${…}` interpolation — otherwise a secret
  could be `push`ed, `log`ged, or shaped into a `result`, and no after-the-fact scrubber should be
  asked to catch that.
- **Resolve at action time; register in the run scope.** Resolution happens in interpreter memory at
  the `fill`, registering the value into `IRunSecretScope` exactly as connecting adapters do — every
  sink scrubs it for free, it never outlives the run, and checkpoint resume re-resolves it naturally
  (the same path backend credentials take at `ConnectAsync`).
