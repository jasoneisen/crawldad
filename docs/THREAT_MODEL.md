# Crawldad — Threat model & security boundary

Crawldad is a hosted, multi-tenant service that drives customer-supplied browsers against arbitrary sites and records the result. The assets it must protect are: **provider/backend credentials**, **form-fill secrets**, **tenant API keys**, **extracted PII and screenshots**, and **cross-tenant isolation**. This document states the boundary as built and verified against the code; it is terse and factual. It cross-references the engine contract ([`SPEC.md`](SPEC.md)), the HTTP surface ([`API.md`](API.md)), and the payload language ([`PAYLOAD_SPEC.md`](PAYLOAD_SPEC.md)).

---

## Credentials by reference

Payloads and events never carry a raw secret. A backend binding carries a **`credentialRef`** — a name resolved to a live secret **only at connect time**, tenant-scoped, living solely in the interpreter's memory for that session. A tenant registers its browser connect credentials through the API (`PUT /browsers/{name}`, `Features/Browsers/`); the registered name is the `credentialRef`, and the secret is **encrypted at rest** (ASP.NET Data Protection, purpose-bound) in a tenant-partitioned Marten document — never returned by any endpoint, and never written to an event or log. `apiKey` mode registers a provider api key; `connectUrl` mode registers the whole connect URL, treated as a one-time secret (still scrubbed). (The Data-Protection key ring must be persisted in production — e.g. blob storage protected by a key vault — or stored credentials become undecryptable after a redeploy.)

Connect resolution (`IConnectCredentialResolver`) is **tenant-scoped by construction**: a `credentialRef` resolves against (a) the tenant's registered browsers first, then (b) a tenant-namespaced config fallback (`Secrets:{tenant}:{ref}`, an operator-seeded credential under the same tenant) — mirroring the form-fill path. There is **no** flat, process-global `Secrets:{ref}` lookup for connect credentials: a tenant can only ever resolve its own references, and a ref that belongs to another tenant (or to nobody) yields the same classified `secret-not-found` miss with no existence oracle — closing the multi-tenancy gap (#61). The form-fill vault remains a **keyed-adapter registry** (`ISecretStoreRegistry`, the same pattern as browser backends and storage sinks): `config` ships today; a real `azure-keyvault`/`aws-secretsmanager`/`hashicorp-vault`/customer-HTTP adapter is one registration line with no change to the interpreter.

## Provider connect strings are not safe to leak

Both managed backends' connect strings are live-session or account credentials, and both are scrubbed:

- **Browserbase.** The account `apiKey` (`bb_live_…`) travels only in the `X-BB-API-Key` header of the session-create call, never in a URL. The returned `connectUrl` is `wss://connect.<region>.browserbase.com/?signingKey=<JWT>` — a **per-session** JWT, not the account key. Leaking it compromises that one session (until expiry), not the account; it is still not safe to leak.
- **Browserless.** The connect URL is `wss://production-<region>.browserless.io/chromium/playwright?token=<token>`; the `token` is **account-scoped** — a leaked token drains the account's balance.

## The scrubbing primitive

`Infrastructure/Security/CredentialScrubber.cs` is the single scrubber every sink funnels outbound text through. It is idempotent and a no-op on ordinary text (the word "token" without `=value` is untouched, so credential-free output is byte-identical to its golden). Two rules:

1. **Exact secret** — any credential resolved for the current run (registered into the per-run scope by the connecting adapter or by a `fill.secret`) is replaced wherever it appears, catching free-form text no param rule would recognise (a `log` echoing an input, an exception, a scraped page, a form value read back after a fill). Connect credentials are scrubbed only at length ≥ 8; a form-fill secret — user-chosen and possibly short (a PIN) — is scrubbed at a lower floor of 4 (a 1–3 char "secret" stays inert, a documented over-redaction guard).
2. **Known credential params** — the values of `apiKey`, `token`, and `signingKey` (case-insensitive) are redacted wherever they appear as `name=value`, covering a `ws://`/`wss://` query and a JSON-embedded connect URL. The value runs to a query/prose delimiter and excludes brackets, so the marker itself is inert (double-scrub is stable). Surrounding text (scheme, host, path) is preserved so the redaction stays diagnostic.

The redaction marker is `[redacted]`. A third input feeds the exact-match rule: the configured **tenant API keys** are registered as always-on (process-wide) secrets, so a leaked key is redacted anywhere it might surface — defence in depth, not a substitute for never emitting it.

## The per-run secret registry

`IRunSecretScope` (`AmbientRunSecretScope`) is opened at the run's **execution** entry — inline in `POST /runs` for a synchronous run, and by the background executor for an async run, where it spans the whole execution including retries and a checkpoint resume (a fresh `ConnectAsync` re-registers the resolved credential naturally, so a resumed run rebuilds its scrub set with no persisted secret). The scope is ambient (`AsyncLocal`), so it flows down the run's async call chain, concurrent runs never see one another's secrets, and **no secret outlives its run** — disposing the scope at run end clears the registered secrets, and the scrubber holds only a reference to the seam, never a secret.

## What is scrubbed, and where — one chokepoint per sink

| Sink | Chokepoint | Note |
|---|---|---|
| **Connect exceptions** | the real adapters | any provider fault becomes a hand-written, secret-free `BrowserConnectException`; the raw `wss://…?token=` URL is never wrapped in. |
| **Marten events** | `RunEventScrubber`, at both append paths (the sync `POST /runs` append and the executor's own-session append) | every event is scrubbed before append; a `fill.secret` `Filled` event carries `secret:<refName>`, never the typed secret. |
| **Payload scripts** | `PayloadScript.Scrub` at save | a payload's script *is* the stored artifact, so it is scrubbed before the `PayloadDrafted`/`PayloadRevised` event is built and hashed — the immutable store can never receive a credential. |
| **Projections** | by construction | the `Run` snapshot, `RunTimeline`, and `PayloadSummary` derive purely from already-scrubbed events and add no un-scrubbed field. |
| **Logs** | `ScrubbingLoggerFactory` (decorates `ILoggerFactory` in host configuration) | every category — application, Wolverine, Marten, ASP.NET — scrubs its rendered message before any sink writes it. Central, not per-call-site. |
| **HTTP response** | `StartRunEndpoint` (sync) / `RunFinalization` (async — scrub-before-store into `RunProgress`) | the failure message and the shaped `result`/`partial` are scrubbed (`ScrubJson`) — caller data a scraped page could echo. The async poll (`GetRunEndpoint`) then serves the already-scrubbed `RunProgress`. |
| **SSE frames** | `RunEventsEndpoint` | each frame re-serialises the already-scrubbed persisted event; the durable stream (not an in-memory buffer) is the frame source. |
| **Timeline** | by construction | folds the already-scrubbed step trace; surfaces refs, never raw values. |
| **Screenshot blobs** | `IScreenshotStore` (content-addressed ref) | the immutable event holds only a `screenshots/{sha256}.png` ref; the image lives in a deletable, tenant-partitioned, TTL-governed store. |

**The one thing text-scrubbing cannot cover:** a screenshot captures the page as *pixels*, so a shot taken right after a `fill.secret` can show the typed value on-screen. Mitigations are structural, not textual: the ref-only immutable trace, the shorter 7-day screenshot TTL (auto-expired by the janitor), and per-tenant partitioning. Authors must not place a `screenshot` where a secret is rendered on-screen; the platform never routes the bytes anywhere but the deletable store.

## Durable state at rest

The background executor leaves run state in three durable stores beyond the immutable trace. The invariant that holds across all of them: **inputs travel by reference**, so a `credentialRef` — not a resolved secret — is what they contain.

| At-rest store | Holds | Scrubbed? | Retention |
|---|---|---|---|
| `RunProgress` | the pollable `result`/`partial`/`failure` body and the durable checkpoint (cursor + accumulated-var snapshot) | **yes** — scrubbed before store; extracted PII it holds lives here (deletable), never in an event | mutable, executor-owned document |
| `RunExecutorSaga` | the run's `script` + `inputs` — the resume source | inputs are caller config; credentials among them are refs | **reclaimed atomically at terminal**: the finaliser deletes the saga in the same transaction as the run's terminal disposition (`RunFinalization` → `session.Delete`), so a finished run's inputs+script are gone the instant it reaches terminal — no separate cleanup step to lose, no crash window. A non-finalised run keeps its saga (the resume source). |
| Wolverine envelopes | `StartRun` carries `script`+`inputs`; `ExecuteRun`/`RunDeadline` carry only the run id | inputs are refs | handled envelopes retained until Wolverine's sweep; a scheduled `RunDeadline` sits until its delay fires. |

The honest corollary: a raw secret passed as a **plain input value** would sit un-scrubbed in the `inputs` carried by `StartRun` and the saga (the scrubber redacts registered run secrets and credential-shaped params, not arbitrary values). That is exactly why form-fill credentials must be a `secretRef` type, kept out of the run's eval scope entirely, never a plain input.

## Durable blob storage, retention & PII erasure

Bulk extracted downloads and failure/explicit screenshots never enter the immutable event store — events carry metadata only (content id, hash, blob ref); the bytes live in **deletable blob storage**, config-selected via `Crawldad:Storage:Provider` (`filesystem` default, `azure`, or the in-memory `fake` for tests).

- **Tenant partitioning.** Every blob is stored under its tenant (`{Root}/{tenant}/…` on disk, a `{tenant}/…` prefix in the Azure container); the idempotency probe resolves a tenant-qualified location, so one tenant can neither read, overwrite, nor probe another's blob by content id. The content-id/screenshot ref handed back to the engine stays tenant-independent, so the wire result and trace are byte-identical across providers. The one attacker-influenceable path segment (the tenant, from the authenticated principal) is guarded against traversal; the content id (a GUID) and screenshot key (a hex digest) are intrinsically safe.
- **Retention.** A host-enforced policy (`Crawldad:Storage:Retention`), so it applies uniformly across filesystem and Azure. A scheduled `RetentionJanitor` background service sweeps every durable store on `SweepInterval` (default 1 hour, must be positive) and deletes blobs past their category TTL: **downloads 30 days, screenshots 7 days** (shorter, because a screenshot can show PII). A TTL of 0 retains that category indefinitely; `Retention:Enabled=false` disables the janitor. A non-positive sweep interval, or a durable provider missing its target, fails the host loudly at boot.
- **Erasure — what ships vs future work.** Deletion today is **TTL-based auto-expiry only**: the scheduled janitor is the *sole* caller of `IRetentionStore.DeleteAsync`, so a blob is removed when it ages past its category TTL, and no endpoint or handler performs on-demand deletion (the API exposes no erasure route). `DeleteAsync` is the hard-delete primitive a future **on-demand right-to-erasure** path would build on — delete a subject's matching blobs on request, the immutable trace retaining only the credential-free ref — but that path is **not built**. Optional **crypto-shredding** (encrypt each blob under a per-run/subject key; discard the key to render it unrecoverable) is likewise **designed, not built**. Both are future work; the shipped guarantees are TTL expiry, per-tenant partitioning, and the ref-only trace.

## Authentication & tenant isolation

Every route requires an authenticated tenant except four deliberately anonymous, tenant-independent artifacts — `GET /health`, `GET /schema/crawldad-1.schema.json`, `GET /llms.txt`, `GET /openapi.json`. An endpoint-enumeration test discovers the live route table and asserts every other route rejects an unauthenticated request, so a route added later without auth fails the test by default.

- **Mechanism.** Per-tenant API keys presented as `Authorization: Bearer <key>` (checked first) or `X-Api-Key: <key>`, validated by a custom ASP.NET authentication handler against a config-bound tenant directory (`Crawldad:Tenants` → `{id, apiKey, actor}`). A missing or unknown key is `401`, and the key is never echoed or logged.
- **Keys are hash-compared.** `TenantRegistry` keeps only a SHA-256 of each configured key (never plaintext) and compares with `CryptographicOperations.FixedTimeEquals`, probing the **whole** set with no early return — so neither the match position nor a near-miss leaks through timing. Boot-time guards reject a missing id/actor, an API key shorter than 16 characters, a duplicate id, a reused key, and a `:` in a tenant id (which would make the per-tenant vault-key prefix `Secrets:{tenant}:{ref}` ambiguous).
- **Actor from the principal, never the body.** Payload mutation events carry a `by` actor stamped from the authenticated tenant's configured identity; the request contracts have no `by` field to spoof.
- **Tenancy model.** Marten conjoined multi-tenancy: one shared schema, every stream and document row qualified by `tenant_id`, every session opened for a tenant. A cross-tenant read returns nothing → **`404` (not `403`)**, so a tenant cannot even confirm another's resource exists. The tenant flows into **every** session, including the out-of-request ones: the background executor reads `Envelope.TenantId` (a run with no tenant fails closed), checkpoint resume and SSE backfill open tenant-scoped sessions, startup recovery fans out over every configured tenant, and the async projection daemon projects each event under its own tenant. Backend **connect credentials** resolve tenant-scoped (a tenant's registered browsers, then `Secrets:{tenant}:{ref}`) — no process-global credential namespace (#61); backend sessions are per-run and never shared across tenants; storage is partitioned per tenant.
- **Upgrade path.** Conjoined tenancy is one database with no per-tenant provisioning; a stronger schema-per-tenant or database-per-tenant posture is available later via Marten with no change to the endpoints or executor — only the store wiring.

## Payload safety & resource limits

The payload language is the first line of defence: it cannot `eval`, cannot reach the filesystem/network, and cannot loop unbounded (every loop carries a mandatory cap), so a schema-valid, save-validated payload has a **bounded, inspectable effect surface** ([`PAYLOAD_SPEC.md`](PAYLOAD_SPEC.md)). Backing that, five server-side resource caps a payload can never raise bound a run's cost and blast radius — max steps, max downloaded bytes, max events, an expression fuel budget, and per-tenant concurrent runs (with a durable admission queue rather than rejection) — specified with their defaults in [`SPEC.md`](SPEC.md#resource-limits) and [`API.md` §12.4](API.md).

An unbounded synchronous connection is itself a connection-holding DoS vector; the 120 s sync cap with auto-upgrade (see [`ARCHITECTURE.md`](ARCHITECTURE.md#a5-run-lifecycle)) closes it while keeping the credential-scrubbing boundary intact across the request→background handoff — the run's ambient secret scope stays open until background finalisation, so an upgraded run's events and result are scrubbed exactly as an inline run's.
