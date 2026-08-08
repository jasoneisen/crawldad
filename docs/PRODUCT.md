# Crawldad — Pricing, Azure Architecture, and the Constraints That Drive Both

> Product-architecture report, 2026-08-07. Crosses (A) plan tiers + metering, (B) Azure hosting
> architecture, (C) system constraints — with (C) driving (A) and (B). Grounded in
> `CRAWLDAD_DESIGN.md` (§8, §9, §11–14), `BACKLOG.md` (CD-1..CD-14), `SECURITY.md`.
> Prices in USD. Anything not verified against a primary source is marked **(verify)**.

---

## 0. TL;DR — the three decisions everything else hangs on

1. **The 240-second wall is real and universal.** Azure Front Door caps origin response timeout at
   **240 s** (Standard/Premium; default 30 s). Container Apps ingress (Envoy) cancels requests at
   **240 s** (Premium ingress stretches to ~2400 s, awkwardly). App Service front-ends kill requests
   at **~230 s, not configurable** (verify). Crawldad's default *synchronous* POST /runs with a
   30-minute deadline is architecturally dead on Azure. **Therefore: async is the product default;
   sync becomes a convenience feature hard-capped at 120 s.** This one decision fixes the ingress
   problem, halves the SNAT problem, and becomes a *pricing lever* (sync-for-short-runs is a DX
   perk, not the workhorse).
2. **Browser compute is cheap; concurrency is scarce.** Browserbase is ~**$0.10–0.12 per browser-
   hour** (≈ $0.002/minute) — but concurrent-session caps are **account-level** (100 concurrent on
   their $99 Startup plan, 250+ on Scale). Our COGS story is not minutes, it's *slots*. **Meter
   browser-minutes (fat margin, feels fair), but ration concurrency via per-tenant slot caps — the
   CD-3 `maxConcurrentRunsPerTenant` limit is literally the plan lever.**
3. **BYO backend is a margin weapon, not a compromise.** The design's founding boundary ("customers
   bring their own browser compute") means a BYO run's marginal cost to us is *cents of Postgres and
   blob* — near-zero. Bundle managed browser minutes on Free/Pro for zero-friction onboarding
   (Hormozi: remove effort & sacrifice), and let BYO-backend runs consume only slots. Pricing then
   attaches to what the design always said it should: **managed payloads, observability, drift** —
   the stuff with no marginal cost and all the perceived value.

---

## 1. Constraint & bottleneck inventory

Legend: **[I]** per-instance (scale-out fixes it) · **[G]** global/architectural (scale-out does NOT fix it).

### 1.1 Ingress / request-lifetime ceilings — the sync-run killer **[G]**

| Hop | Ceiling | Consequence for a 30-min sync run |
|---|---|---|
| Azure Front Door Std/Premium | origin response timeout **16–240 s max** (default 30 s) | 504 OriginTimeout at ≤4 min. Dead. |
| Container Apps ingress (Envoy) | **240 s** request timeout; Premium ingress up to ~**2400 s / 40 min** but CLI support is immature (verify) | Dead on standard ingress even with no Front Door. |
| App Service front-end | **~230 s**, **not configurable** (verify) | Dead. Also disqualifies App Service generally. |
| Application Gateway v2 | backend request timeout configurable **1–86,400 s** | The only front door that tolerates 30-min requests — but you give up global anycast/WAF-at-edge simplicity and still hold every intermediate connection. |
| SSE (server→client) | Same idle rules, but a **15–20 s heartbeat comment frame** resets idle timers at every hop | SSE survives everywhere with heartbeats. This is why async+SSE is the shippable long-run UX. |

Verdict: you *could* build a 30-min-sync path with App Gateway v2 and raw connection patience. You
shouldn't. Every element of the chain (client library defaults, corporate proxies, LB idle timeouts
~4 min default) fights you. The design already has the right escape hatch built (async 202 + poll +
SSE + checkpoints, §11); make it the default posture.

### 1.2 Outbound SNAT — the wss:// tax **[I → G fix]**

- Default outbound (App Service, ACA without NAT Gateway, LB default allocation): **~128
  preallocated SNAT ports per instance**. Each active run holds ≥1 long-lived outbound connection
  (the wss:// CDP to Browserbase) plus intermittent HTTPS to blob storage and Postgres (pooled,
  usually via private endpoint so not SNAT-relevant if VNet-integrated). Realistic ceiling without
  mitigation: **~50–80 concurrent runs per instance** before intermittent connect failures — the
  nastiest failure mode because it's probabilistic.
- **NAT Gateway: 64,512 SNAT ports per public IP** (up to 16 IPs ≈ 1M ports), dynamically allocated
  across the subnet. One NAT Gateway on the ACA subnet removes SNAT from the risk register
  entirely at our scale. ~$32/mo + $0.045/GB processed — trivial. **Non-optional in the reference
  architecture.**
- Note the CDP connection is *one* socket per run regardless of how many pages the target site
  loads — the browser's own traffic egresses from **Browserbase's IPs, not ours**. Our egress
  profile is modest; the risk was concurrency of long-lived sockets, and NAT Gateway ends it.

### 1.3 Postgres Flexible Server — connections and event volume **[G]**

- **max_connections** scales with memory: D2ds_v5 (8 GiB) ≈ 859; **D4ds_v5 (16 GiB) ≈ 1,717**
  (verify); capped ~5,000. Minus 15 reserved.
- Consumers per app instance: Npgsql pool (default max 100), Marten async projection daemon
  (session-pinned, advisory locks), Wolverine durable queue polling, and the SSE endpoints — each
  SSE client tails the run stream by **polling the event table** (the design reads the stream, not
  the projection). 3 instances × 100 pool = 300 connections: fine on D4ds. 20 instances is not.
  **Mitigation:** cap Npgsql `Maximum Pool Size` at ~50/instance; enable the Flexible Server
  **built-in PgBouncer** (port 6432, transaction mode) for HTTP-request-scoped Marten sessions —
  but keep the projection daemon and Wolverine on **direct** connections (advisory locks +
  LISTEN-style behavior are session-pinned and break under transaction pooling).
- **Event volume is the sleeper cost.** A LJCMG-scale run emits hundreds of semantic step events
  (~0.5–1 MB/run with metadata-only discipline). 100k runs/mo ≈ **50–100 GB/mo of event-store
  growth** plus timeline projections. CD-3's **max event count per run** is not just a safety
  limit, it is COGS control; add archival (completed-run streams → blob after retention window) to
  the roadmap. SSE tail-polling of hot streams also makes the events table the top query target —
  index by `(stream_id, version)` is native to Marten; the load is fine, the *storage* is the issue.

### 1.4 Compute / Kestrel **[I]**

- Kestrel has no meaningful default connection cap; the real per-instance ceilings are memory per
  held connection (sync run: caller HTTP + CDP socket + interpreter state; SSE: one response
  stream + poll loop) and ThreadPool health. Budget ~1–3 MB per active run/SSE client → a 4 GiB
  container comfortably holds **~500–1,000 concurrent SSE clients + ~100–200 active run
  interpreters**. Scale-out fixes this — it's the healthy kind of bottleneck.
- Wolverine durable local queues (Postgres-backed) handle orders of magnitude more messages/sec
  than run orchestration will ever produce (runs are minutes long; message rate is per-step at
  most). Not a bottleneck before Postgres itself is.

### 1.5 Browserbase economics **[G — account-level]**

- Current pricing (verified via aggregators, re-verify against browserbase.com before contracts):
  Free $0 (3 concurrent, 1 browser-hour, **15-min max session**); Developer $20/mo (25 concurrent,
  100 hrs incl., $0.12/hr overage); **Startup $99/mo (100 concurrent, 500 hrs incl., $0.10/hr
  overage, 6-hr max session)**; Scale: custom, 250+ concurrent.
- **COGS ≈ $0.0017–0.002 per browser-minute.** A 10-minute run costs us ~2¢ of browser. Proxy
  bandwidth is the hidden multiplier ($10–12/GB) — do NOT bundle proxies in low tiers.
- **The binding constraint is the concurrency cap, and it is OUR account's, shared across all
  tenants on the managed backend.** Selling 5 slots each to 40 Pro customers = 200 potential
  concurrent vs our 100-cap. Mitigations: admission-control queue (a run waits for a slot rather
  than failing), Browserbase Scale contract as we grow, and pushing heavy users to BYO backend
  (which moves them onto *their* Browserbase account and *their* concurrency cap — margin and
  constraint relief in one move).

### 1.6 Blob storage **[G — cost, not capacity]**

Downloads + screenshot-on-failure (default ON) + archived traces. Azure Blob LRS ~$0.018/GB-mo hot,
~$0.01 cool; **egress to internet ~$0.05–0.087/GB after free 100 GB** (verify). Screenshots are
PII-bearing (§12) → lifecycle deletion is a security requirement AND a cost control; CD-2's
retention policies are the mechanism. Egress is the abuse vector: a payload that downloads 10 GB
and the customer re-downloads it repeatedly. CD-3 `maxDownloadedBytes` + presigned-URL Targets
(bytes go to *the customer's* storage, skipping our egress entirely) are the levers.

### 1.7 Summary table

| Constraint | Scope | Fix |
|---|---|---|
| 240 s ingress ceilings | **[G]** | Async-by-default; sync ≤120 s hard cap |
| SNAT 128 ports/instance | [I] | NAT Gateway (64,512/IP) — permanent fix |
| Postgres max_connections | **[G]** | Pool caps + built-in PgBouncer; scale SKU |
| Event-store growth | **[G]** | CD-3 max-event-count; retention + archival |
| Per-instance memory (runs, SSE) | [I] | Scale out (KEDA on active-run count) |
| Browserbase concurrency cap | **[G]** (account) | Slot rationing (CD-3), queueing, BYO steering |
| Blob egress / storage | **[G]** (cost) | CD-2 lifecycle, `maxDownloadedBytes`, presigned Targets |

---

## 2. Azure reference architecture

### 2.1 Compute: **Azure Container Apps, Dedicated workload profile** — not App Service, not AKS

- **App Service is disqualified** by the fixed ~230 s request timeout (kills even generous sync
  windows and complicates SSE) and weaker outbound story. **AKS is deferred**: a solo-maintainer
  .NET monolith with Wolverine in-process does not need cluster operations; revisit at Enterprise
  single-tenant deployments (an ACA→AKS move is easy because everything is one container).
- ACA gives: scale-to-N with KEDA (custom scaler on *active runs + SSE connections* — NOT
  CPU: run interpreters are I/O-idle most of the time), VNet integration, easy revisions,
  sticky-enough sessions for SSE. Configure **min replicas 2** (Wolverine durable queues make
  restarts safe; checkpoints make run resumption real — §11 is what makes aggressive
  autoscaling *safe*).
- Sizing: 2× (2 vCPU / 4 GiB) Dedicated D4 profile to start ≈ **$250–300/mo**; each replica
  comfortably drives ~100–200 concurrent run interpreters.

### 2.2 Front door: **Azure Front Door Standard** + the 120-second sync rule

- Front Door Standard (~$35/mo base + traffic) for TLS, WAF, anycast. Its 240 s cap is *fine*
  because **we cap sync at 120 s ourselves**: `POST /runs` without `"async": true` is accepted only
  if the effective deadline ≤ 120 s; otherwise **406/409 with a machine-readable steer**
  (`use_async: true`) — or, better DX, auto-upgrade with a `202 + runId` and a `Prefer:
  respond-async` note. Sync stays as the frictionless demo/short-job path (Hormozi: time-to-first-
  value in one curl), async is the workhorse.
- **SSE**: heartbeat comment frame every 15 s (resets idle timers at Front Door, Envoy, and client
  proxies). Document a client reconnect-with-Last-Event-ID pattern — the design's
  backfill-from-stream (§11) already supports it perfectly.
- Skip Application Gateway entirely unless an Enterprise customer demands >120 s sync; then it's a
  dedicated App Gateway v2 path (timeout up to 86,400 s) inside their private deployment — an
  Enterprise SKU feature, not shared infrastructure.

### 2.3 Outbound: **NAT Gateway on the ACA subnet** — day one, non-negotiable

One NAT Gateway + 1 public IP = 64,512 SNAT ports, dynamically shared. ~$32/mo + data. Also gives a
**stable egress IP** to publish for customers' firewall allowlists (relevant for the self-hosted
tunnel backend). Enterprise "private egress" = per-tenant NAT Gateway or VNet peering — a clean
premium feature.

### 2.4 Data: Postgres Flexible Server + Blob + Key Vault

- **Postgres Flexible Server D4ds_v5 (4 vCore/16 GiB, ~1,717 max_connections (verify)), zone-
  redundant HA off at launch, on at Pro-tier GA** ≈ $250–350/mo. Built-in **PgBouncer** enabled for
  request-scoped sessions; projection daemon + Wolverine on direct connections (session-pinned
  advisory locks). Npgsql `Maximum Pool Size=50` per instance.
- **Blob Storage (CD-2):** containers per purpose — `downloads/`, `screenshots/`, `trace-archive/`
  — per-tenant prefixing now, per-tenant containers at CD-1. **Lifecycle policies ARE the tier
  ladder:** hot→cool at 7 d, delete at tier retention (Free 3 d, Pro 30 d, Scale 90 d, Enterprise
  custom/immutable). Crypto-shredding (per-run key in Key Vault, discard to erase) implements §12
  erasure and becomes a compliance selling point.
- **Key Vault + Managed Identity** for our own secrets and the default `ISecretStore` backing;
  CD-6's `azure-keyvault` adapter then targets the *customer's* vault via their identity —
  Enterprise BYO-vault maps 1:1 onto the designed keyed-registry seam.
- **Monitoring:** Azure Monitor + Log Analytics; OTel from .NET. The must-have custom metrics are
  the constraint dashboard: active runs by tenant, slot-queue depth, Browserbase concurrent
  sessions vs account cap, SNAT allocation, Postgres connections, event-table growth/day, SSE
  connection count. Every **[G]** row in §1.7 gets an alert.

### 2.5 Reference bill (steady-state, pre-Enterprise)

| Component | ~$/mo |
|---|---|
| ACA 2× D4 replicas | 280 |
| Postgres D4ds_v5 + 128 GB | 320 |
| Front Door Standard | 40 |
| NAT Gateway | 35 |
| Blob (500 GB mixed) | 15 |
| Key Vault, Monitor, misc | 60 |
| Browserbase Startup | 99 |
| **Total infra floor** | **~$850/mo** |

Breakeven at ~12 Pro seats or 3 Scale seats. The platform is structurally cheap because browsers —
the expensive part — are Browserbase's problem (metered through) or the customer's (BYO).

---

## 3. Metering model — what is metered vs what is a cap

> **SUPERSEDED by "Pivot: the transparent BYO model" (end of report)** — the managed-browser-markup
> model was rejected; browser-minute metering no longer applies. Kept for the record.

Candidates evaluated against (a) what costs US money, (b) whether the customer can predict it,
(c) whether it maps to a CD-3 enforcement knob:

| Unit | Our cost driver | Verdict |
|---|---|---|
| **Browser-minutes (managed backend)** | Direct Browserbase COGS (~$0.002/min) | **METER #1.** Passthrough-with-margin; customers already understand it (Browserbase/Browserless priced the anchor for us). Enforced by run wall-clock — already built. |
| **Storage GB-months (artifacts)** | Blob + event-store growth | **METER #2.** Downloads, screenshots, archived traces. Enforced by CD-2 lifecycle + CD-3 maxDownloadedBytes. Presigned-URL Targets bypass it (customer's storage) — good, that steers cost away from us. |
| **Concurrent run slots** | Browserbase account cap + instance memory | **CAP, sold in packs at Scale+.** The single most important lever: protects the one truly global scarce resource. CD-3 `maxConcurrentRunsPerTenant` IS this. Over-cap runs queue, never fail. |
| Runs/month | Postgres events per run | Tier cap (abuse brake), not a meter — punishing per-run pricing fights the loops/long-runs differentiator. |
| Steps per run | Event volume, runtime | CD-3 `maxSteps` as a tier cap (Free 200 / Pro 2,000 / Scale 10,000). Never metered — nobody can predict step counts. |
| SSE/webhook events | Negligible | Free. Observability is the product's charm; metering it poisons the value story. |
| Managed payloads + revisions | ~zero | **Tier cap and the value ladder** (the founding thesis: pricing attaches to managed payloads). Free 3 / Pro 25 / Scale unlimited. |
| BYO-backend run minutes | ~zero (Postgres cents) | Unmetered; consume slots only. This makes BYO the natural relief valve for heavy users — high perceived generosity, near-zero cost. |

**Recommendation: meter exactly two things — managed browser-minutes and artifact storage
GB-months. Everything else is a tier cap keyed to a CD-3 limit.** Two meters keep the bill
predictable (Hormozi: uncertainty is a perceived-likelihood killer); the caps do the margin
protection.

Pricing the meter: **$0.012/browser-minute overage (Pro), $0.008 (Scale)** — a 4–6× markup on COGS
that still reads as "72¢/browser-hour with full trace, replay, screenshots, drift detection"
against Browserbase's raw $0.10/hr — the delta is transparently the managed layer. Storage:
$0.10/GB-month past included, cost ~$0.02 — fine.

**Risk-reversal rule baked into the meter:** runs that fail with an *engine-class* error (our bug,
infra fault — not payload `guard`/terminal or target-site failures) are **auto-credited**. The
error taxonomy (§8.3) already distinguishes these classes; this turns an internal taxonomy into a
trust feature: "you never pay for our failures."

---

## 4. Tier design (Hormozi framing)

> **SUPERSEDED by "Pivot: the transparent BYO model" (end of report)** — tiers rebuilt around BYO
> browser/storage at every tier and a self-host free core. Kept for the record.

Value equation, applied: **dream outcome** = "browser automations that don't silently rot —
declarative, versioned, replayable, drift-detected." **Perceived likelihood** = watch a live run in
SSE, see the failure screenshot, replay the exact pinned revision. **Time delay** = one curl to
first run (bundled browser, no Browserbase signup, importer from Chrome Recorder later per CD-13).
**Effort & sacrifice** = JSON payload instead of a Playwright codebase + no infra to run. Every cap
below names the constraint it protects.

| | **Free — "Hatchling"** | **Pro — $49/mo** | **Scale — $299/mo** | **Enterprise — custom ($2k+/mo)** |
|---|---|---|---|---|
| Managed browser-minutes incl. | 60/mo | 2,000/mo (o/a $0.012) | 15,000/mo (o/a $0.008) | committed volume |
| Concurrent run slots | 1 | 5 | 25 (+$10/slot packs) | custom, dedicated pool |
| BYO backend (their Browserbase/Browserless/tunnel) | ✔ (the dev hook) | ✔ | ✔ | ✔ + self-hosted tunnel SLA |
| Sync runs (≤120 s hard cap) | ✔ | ✔ | ✔ | ✔ (private App-Gateway path if truly needed) |
| Async run deadline | 5 min | 30 min | 2 h + checkpoint-resume guidance (CD-14) | custom |
| Max steps / run | 200 | 2,000 | 10,000 | custom |
| Managed payloads | 3 | 25 | unlimited | unlimited |
| Trace + screenshot retention | 3 days | 30 days | 90 days | custom / immutable |
| Artifact storage incl. | 100 MB | 5 GB | 50 GB | custom |
| Webhooks (CD-12) | — | ✔ | ✔ | ✔ + signed |
| Drift alerting / scheduled canaries | — | — | ✔ | ✔ |
| BYO vault (CD-6), VNet/private egress, SSO, 99.9 % SLA, tenant DB isolation | — | — | — | ✔ |
| Support | community | email | priority | named, Slack, onboarding |

**Which constraint each cap protects:** slots → Browserbase account concurrency **[G]**; deadline →
ingress ceilings + slot occupancy; max steps → event-store growth **[G]**; retention/storage →
blob + Postgres cost **[G]**; minutes → COGS; payload count → nothing (pure value ladder — the
margin is here).

**Free tier is structurally near-zero cost by construction:** 1 slot means a free tenant can burn
at most 1 browser × 5 min at a time; 60 min/mo ≈ **$0.12 browser COGS/tenant/mo worst case**;
3-day retention keeps their Postgres+blob footprint at megabytes; 200-step cap bounds event volume;
no proxies, no webhooks. Yet it's genuinely useful: real runs, real SSE, real failure screenshots,
3 managed payloads with revision history — a working monitor for one scraper. The lead-magnet job:
*experience drift detection once* and the upgrade sells itself.

**Upgrade triggers (each is a felt moment, with a fair, machine-readable error):**
- Free→Pro: 2nd concurrent run queues ("slot_exhausted — your run is queued; Pro runs 5 at once");
  trace expired at day 3 exactly when they're debugging; 4th payload.
- Pro→Scale: slot queue wait time surfaces in the dashboard; 30-min deadline hits on a big crawl;
  drift alerting is the pull ("you found out it broke from your customer; Scale tells you first").
- Scale→Enterprise: the first login-gated target (needs CD-6 secretRef — deliberately
  Enterprise-only, and the security story writes itself from SECURITY.md), procurement/SLA, VNet.

**Grand-slam offer (Scale is the flagship):** "Your scrapers, guaranteed watched: every run
recorded, every failure screenshotted, every payload change diffed, drift caught by nightly canary
— or the minutes are on us." Risk reversal stack: engine-failure auto-credit (above), 30-day full
refund, no annual lock-in below Enterprise, and export-everything (payloads are your JSON; traces
exportable) — kills switching-cost fear, which *increases* willingness to commit.

---

## 5. Monetization-critical backlog mapping

**Phase M0 — prerequisites to charge anyone (in order):**
1. **CD-1 auth/tenancy** — no tenant identity, no billing subject, no per-tenant caps. First.
2. **CD-3 resource limits** — the caps ARE the plans: `maxConcurrentRunsPerTenant` (slots),
   `maxSteps`, `maxDownloadedBytes`, `maxEventCount`, expression budget. Metering reads what
   enforcement counts — build the counters once.
3. **CD-2 real blob + retention/lifecycle** — storage meter + retention tiers + the PII/security
   story; FakeDownloadSink cannot be production.
4. **CD-5 saga completion cleanup** — unbounded `mt_doc_runexecutorsaga` growth is a COGS leak and
   a data-retention violation the moment retention is a sold feature.
5. **CD-4 connectUrl live re-check** — blocks the GA security copy that Pro/Enterprise marketing
   leans on.
6. **The 120-s sync cap + async steering** — not a ticket yet; file it. Cheap (StartRun validation
   + response contract) and it de-risks the entire ingress layer.

**Phase M1 — tier-differentiating:** CD-12 webhooks (Pro), CD-7 nightly canary → productized drift
alerting (Scale's headline), CD-8 explicit screenshot node (Pro polish), CD-14 checkpoint authoring
guidance (Scale's 2-h runs).

**Phase M2 — expansion:** CD-6 BYO vault + secretRef (the Enterprise unlock — first login-gated
target = first enterprise deal), CD-13 @puppeteer/replay importer (top-of-funnel: Chrome Recorder →
Free tier in one upload; this is a growth ticket wearing an engineering coat).

**Never monetization-blocking:** CD-9, CD-10, CD-11.

---

## 6. Named risks (top 5) and mitigations

1. **Sync-mode connection abuse / accidental DoS.** Long sync requests pin memory, slots, and
   ingress at every hop; retrying clients double-execute non-idempotent runs. *Mitigation:* the
   120-s hard cap; per-tenant slot admission even for sync; idempotency keys on StartRun; the
   Prefer-async auto-upgrade so well-behaved clients never notice.
2. **Browserbase margin squeeze / single-vendor dependency.** They raise prices, cut our account
   concurrency, or launch a competing orchestration layer (they're adjacent to it). *Mitigation:*
   the meter is "browser-minutes," never vendor-named; the `IBrowserBackend` seam already supports
   Browserless (cheaper, unit-priced) and self-hosted — dual-source the managed pool; BYO steering
   moves the heaviest usage off our account entirely; renegotiate to Browserbase Scale with
   committed volume once >60 % of the 100-session cap is hit at p95.
3. **Free-tier abuse (scraping farms, credential stuffing, card testing).** Our brand is on the
   run records even though egress IPs are Browserbase's; their abuse team will attribute to our
   account. *Mitigation:* 1 slot + 60 min/mo makes farming uneconomic; email+payment-card identity
   for anything above Free; target-domain blocklist at payload validation (banks, login pages of
   major providers on Free); no proxy passthrough below Scale; the declarative-only DSL is itself
   a mitigation (no eval, inspectable effect surface — §12) and worth marketing to that effect.
4. **Event-store bloat outruns revenue.** Chatty payloads (10k-step crawls) write ~MBs of events
   per run; Postgres storage + projection lag degrade everyone (**[G]**). *Mitigation:* CD-3
   `maxEventCount` per tier; trace archival to blob after retention; per-tenant event-volume
   metric with soft alerts before hard caps; Scale pricing already assumes heavy runs.
5. **Oversold concurrency (slot arithmetic vs account cap).** Sum of sold slots exceeds our
   Browserbase concurrency; a burst turns into queue-time SLA violations on the tier we market as
   "runs 25 at once." *Mitigation:* track sold-slots:account-cap ratio (alert at 2:1 given
   observed ~20 % simultaneous utilization — measure and tune); queue-not-fail semantics with
   honest queue-position in SSE; burst headroom via a second backend account (Browserless) for
   overflow; contractual "concurrent" language = "up to, subject to fair queuing" below Enterprise,
   dedicated pools at Enterprise.

---

## 7. Sources

- [Azure Front Door origin response timeout 16–240 s](https://learn.microsoft.com/en-us/azure/frontdoor/how-to-configure-origin) · [troubleshooting](https://learn.microsoft.com/en-us/troubleshoot/azure/front-door/troubleshoot-issues)
- [Container Apps ingress 240 s / premium ingress](https://github.com/MicrosoftDocs/azure-docs/blob/main/articles/container-apps/ingress-overview.md) · [timeout issue #597](https://github.com/microsoft/azure-container-apps/issues/597) · [premium ingress Q&A](https://learn.microsoft.com/en-us/answers/questions/2280978/unable-to-use-premium-ingress-mode-to-extend-http)
- [SNAT defaults & NAT Gateway 64,512 ports](https://learn.microsoft.com/en-us/azure/load-balancer/load-balancer-outbound-connections) · [App Service SNAT 128](https://learn.microsoft.com/en-us/azure/app-service/troubleshoot-intermittent-outbound-connection-errors)
- [Postgres Flexible Server limits / max_connections](https://learn.microsoft.com/en-us/azure/postgresql/configure-maintain/concepts-limits)
- [Browserbase pricing (aggregator, re-verify)](https://scrapegraphai.com/blog/browserbase-pricing) · [alt](https://agentsindex.ai/pricing/browserbase)

---

## POC floor: consumption-based architecture

> Follow-up: the ~$850/mo floor is a *production* floor. For a POC, consumption-based options get
> the bill to **~$30/mo idle, ~$50/mo light use** (or ~$15/mo with an Azure free account) — with
> one honest architectural caveat about scale-to-zero. Evaluated against the system's real shape:
> a monolithic ASP.NET host with background workers (Wolverine durable queues + scheduled
> `RunDeadline` timeout messages, Marten projection daemon), long-lived outbound wss:// CDP
> sockets, and SSE. This is **not** a request/response function.

### P.1 Azure Functions Consumption/Flex — disqualified, quickly

- **Consumption plan:** function execution timeout default 5 min, **hard max 10 min** — under the
  30-min run deadline before we even discuss shape. No always-on host: Wolverine's polling
  agents, the Marten projection daemon, and scheduled `RunDeadline` delivery all assume a
  persistent `IHostedService` process, which the Functions programming model does not host.
- **Flex Consumption:** removes the hard execution cap (verify) and scales per-instance, but the
  model is still trigger→invocation, not "run a Wolverine node." Holding a wss:// CDP socket for
  30 minutes inside an invocation fights the scale controller; SSE out is unsupported-to-hostile;
  durable background messaging would mean rewriting orchestration onto Durable Functions —
  i.e., **throwing away the Critter Stack saga/checkpoint design that already works**. A rewrite
  to save ~$20/mo is a category error. Skip.

### P.2 Container Apps **Consumption plan** — the serious candidate

Same container, same code, consumption billing. Verified rates (East US): **active
$0.000024/vCPU-s + $0.000003/GiB-s; idle vCPU ~$0.000003/s** (memory billed at one rate); free
grant per subscription per month: **180,000 vCPU-s + 360,000 GiB-s + 2M requests**.

**One always-on small replica (min-replicas=1, 0.5 vCPU / 1 GiB), 30-day month (2.592M s):**
- vCPU: 1.296M vCPU-s − 180k free ≈ 1.116M → **~$3.30/mo if billed idle**, ~$26.80 if fully active.
- Memory: 2.592M GiB-s − 360k free ≈ 2.232M × $0.000003 ≈ **$6.70/mo**.
- **Realistic: ~$10–15/mo** (mostly idle; Wolverine's low-CPU Postgres polling should stay under
  the idle threshold, but ACA's idle-rate qualification is CPU/request-based — budget the ~$33
  fully-active number as the ceiling and treat idle billing as upside **(verify)**).

**The scale-to-zero trap, stated honestly:** at 0 replicas there is no process. Nobody polls
Wolverine's queues, so a scheduled `RunDeadline` sits in `wolverine_incoming_envelopes` past its
due time; the durability recovery scan doesn't run; SSE clients can't connect; and scaling to zero
*mid-run* kills the interpreter (checkpoint resume recovers — but only when something wakes the
app, and the default HTTP scaler wakes only on an inbound request). Options, quantified:

| Option | Cost | Verdict |
|---|---|---|
| **min-replicas=1** | ~$10–15/mo | **Recommended.** Zero correctness caveats; the whole design (sagas, deadlines, projections, SSE) works unmodified. This is what "consumption" buys us: production shape at hobby price. |
| KEDA cron or postgres scaler (wake when due envelopes exist / on a schedule) | ~$3–8/mo saved | Works — KEDA runs platform-side, a postgres scaler querying due Wolverine envelopes wakes the app — but adds a moving part whose failure mode is "deadlines silently late." Not worth $10. |
| Accept-late-deadlines, scale to zero | ~$10 saved | Only if the POC is demo-on-demand: deadlines/cleanup fire on next wake, in-flight runs die at scale-in. Acceptable for a screen-share demo, nothing else. |

### P.3 Cheap Postgres: **Flexible Server B1ms burstable**

- **B1ms (1 vCore, 2 GiB): ~$12.41/mo** pay-as-you-go + storage (~$0.115/GiB-mo → 32 GiB ≈
  $3.70). **Azure free account: 750 hrs/mo of B1ms + 32 GB storage + 32 GB backup free for 12
  months** — a POC database for $0 if the subscription qualifies.
- **Skip PgBouncer — but respect the ceiling:** 2 GiB → max_connections ≈ 50 total, **~35
  usable** (verify). One app replica with Npgsql `Maximum Pool Size=15` + projection daemon +
  Wolverine polling fits comfortably; two replicas start to squeeze. This ceiling, not CPU, is
  the first thing that forces the Postgres upgrade.

### P.4 What to drop for POC

- **Front Door** → ACA built-in ingress (Envoy, free, automatic TLS on `*.azurecontainerapps.io`,
  free managed cert on a custom domain). The 240 s cap is identical to production, so the 120-s
  sync rule is unchanged — the POC exercises the same contract.
- **NAT Gateway** → default platform SNAT (~**128 ports/instance** conservative figure) is ample
  at 1–2 concurrent runs (~2–3 long-lived outbound sockets per run).
- **Zone redundancy, HA Postgres, min 2 replicas** → gone. Single replica, single zone. Revision
  restarts drop SSE clients (they reconnect + backfill by design, §11) and interrupt in-flight
  runs (checkpoint resume recovers the two LJCMG loop shapes).
- **Browserbase Startup** → their **Free plan** ($0: 3 concurrent, 1 browser-hr, **15-min max
  session** — fine for smoke tests) or Developer $20/mo when the 15-min cap pinches.

### P.5 POC bill of materials

| Component | Idle $/mo | Light use $/mo |
|---|---|---|
| ACA Consumption, 1× 0.5 vCPU/1 GiB, min-replicas=1 | ~10 | ~18 |
| Postgres Flexible B1ms + 32 GB | 16 (**0** w/ 12-mo free account) | 16 |
| Blob Storage (a few GB) | <1 | 2 |
| Log Analytics (cap daily ingestion; sample) | ~2 | ~5 |
| Browserbase Free → Developer | 0 | 20 |
| Front Door / NAT GW / HA | 0 | 0 |
| **Total** | **~$29 (~$13 on free account)** | **~$55–60** |

**Accepted risks vs the $850 production floor:** no HA anywhere (a zone or revision event kills
in-flight runs — checkpoints make this "resume," not "lose"); ~35 usable Postgres connections; no
WAF/edge; SNAT ceiling ≈ a few dozen concurrent runs; consumption CPU is shared/burstable
(interpreter latency jitter); and *nothing about the ingress contract regresses* — the 240-s wall
and the 120-s sync cap are identical, which is exactly why this POC validates the production
architecture rather than a toy variant of it.

**Graduation triggers (each is a metric, not a vibe):**
- Sustained **>5–10 concurrent runs** or first SNAT connect failures → VNet + **NAT Gateway**.
- **First paying customer / CD-1 auth ships** → min-replicas=2, Postgres **D2ds_v5 + PgBouncer**,
  HA on; this is the moment "restart = resumed run" must become "restart = unnoticed."
- Postgres connections **>70 % of 35** or storage >20 GB → SKU bump (connection ceiling ∝ memory).
- CPU throttling / scale-churn killing interpreters on consumption → **Dedicated workload
  profile** (the $280/mo line item returns).
- Public launch traffic, WAF need, or custom-domain global latency → **Front Door Standard**.

POC sources: [ACA pricing + free grant](https://azure.microsoft.com/en-us/pricing/details/container-apps/) ·
[ACA billing (idle vs active rates)](https://learn.microsoft.com/en-us/azure/container-apps/billing) ·
[Postgres B1ms pricing](https://www.bytebase.com/dbcost/azure-flexible/instance/B1ms/) ·
[Azure free account: 750 hrs B1ms/mo, 12 months](https://learn.microsoft.com/en-ie/azure/postgresql/flexible-server/how-to-deploy-on-azure-free-account) ·
[Functions timeout limits](https://learn.microsoft.com/en-us/azure/azure-functions/functions-scale)

---

## Pivot: the transparent BYO model

> Decision (user, 2026-08): **no markup on Browserbase, no white-labeled infra.** "The API is just
> a translation layer to your existing infra. A solo dev can plug it into their own dev tools
> running on their own machine. A crawling farm can plug it into their data center(s). A crawling
> team in the middle can plug it into their browserbase/less." BYO browser, BYO storage, no VPN
> fussing — at **every** tier. Crawldad is **closed-source and hosted-only**: transparency is
> delivered through the *product* — payloads are declarative JSON the customer authors, owns, and
> can export; every run is fully observable (timeline, replay, SSE); and BYO browser/storage/vault
> means their data and infra stay theirs — not through source availability. This supersedes §3
> (metering) and §4 (tiers) above; §1 (constraints) and §2 (architecture) survive with the deltas
> in Pv.4. Notably, this pivot lands exactly on the design's founding boundary (§1 of
> CRAWLDAD_DESIGN.md: "customers bring their own browser compute; pricing attaches to *managed
> payloads*") — the markup model was the deviation, not this.

### Pv.1 What's left to sell — and the Hormozi reframe

With browser and storage BYO, COGS collapses to **control-plane compute + the Postgres event
store**. What remains is the entire reason the product exists:

- the **safe declarative payload language** (loops + content-aware conditions, no eval — the thing
  no competitor has) and its interpreter;
- **durability**: sagas, checkpoints, resume-across-restarts, deadline enforcement;
- **observability**: SSE live progress, timeline, replay, screenshot-on-failure (to *their* storage);
- **payload management**: versioning, pinning, diff, drift detection, canaries;
- **team workflow**: shared payload registry, revision review, actor audit, webhooks.

**Reframed dream outcome:** *"reliable, maintainable browser automation without owning a
browser-orchestration codebase."* The customer already owns infra (that's the thesis); what they
don't want to own is the 20k lines of interpreter, retry taxonomy, checkpoint saga, and drift
tooling that rot on a shelf. Perceived likelihood: watch your own Browserbase session driven live
over SSE, replay the exact pinned revision. Time delay: point a payload at your existing
`connectUrl` — no new infra, no VPN, first run in minutes. Effort: JSON, not a Playwright codebase.
**We price the reliability and the leverage, never the electrons.** Transparency is itself the
offer's risk-reversal: nothing is resold (no bill can surprise you), your artifacts land in your
storage, and your payloads are your JSON — exportable any day. Hosted-only is also what keeps the
§13 telemetry moat intact: the cross-customer drift signal only accretes because execution runs
through one hosted plane.

**Honest value metrics now (evaluated):**

| Metric | Scales with customer value? | Scales with our cost? | Verdict |
|---|---|---|---|
| **Concurrent run slots** | yes — parallelism is throughput is value; buyers already think in this unit (BrowserStack/Sauce parallels, CI workers, Browserbase's own session caps) | **yes — our costs are peak-capacity shaped** (see below) | **THE PRICED AXIS** |
| Runs/month | yes, but noisy (run length varies 100×) | yes (events), but slots already bound the peak | **fair-use guardrail cap**, not a meter |
| Events per run | no (an implementation detail to the customer) | yes (Postgres) | guardrail cap (CD-3 `maxEventCount`) |
| Seats | yes (team workflow) | ~zero | **folded into tiers** (see below) |
| Active managed payloads | yes (automations under management) | ~zero | tier cap / ladder |
| Timeline retention window | yes (debugging + audit) | yes (Postgres GB) | tier cap |
| Drift monitoring / canaries / webhooks | high | low | feature gates |

**Recommendation: price on concurrent run slots; ladder on payloads, retention, seats-by-tier,
and feature gates; demote runs/events to fair-use guardrails.** The case for slots as the axis:

- **Slots price exactly what we build for.** In the BYO model every cost we still carry is
  peak-capacity shaped: each in-flight run holds an outbound wss socket (SNAT), an executor loop,
  a live saga, an appending event stream, and Postgres connections — and replicas, NAT, and the
  database are provisioned for *peak concurrency*, not monthly totals. A tenant's slot count is a
  direct claim on provisioned capacity; charging for it is charging for the thing that costs us
  money, in the unit it costs us.
- **Legible, fair-feeling, hard to game, cheap to enforce.** No metering pipeline, no
  surprise bills, no end-of-month reconciliation — an admission gate at StartRun is the entire
  billing enforcement surface, and it's the CD-3 `maxConcurrentRunsPerTenant` limit we were
  building anyway.
- **It self-selects the personas.** A tunneled laptop physically caps at 1–2 concurrent browsers;
  a Browserbase team wants 10–25 (congruent with their vendor's own session caps); a farm wants
  hundreds — which is exactly where our peak cost lives. The price axis and the persona ladder
  are the same axis.
- **The caveat, handled:** slots don't track Postgres event *volume* (the dominant marginal
  cost) — a tenant could spam thousands of tiny runs through 2 slots. So runs/month and
  events-per-run become **per-tier fair-use guardrails**: invisible to ~95 % of legitimate users,
  and for the pathological short-run-spam profile they are the upgrade conversation, not a bill.

**Seats fold into tiers** (Free 1 / Team 10 / Scale+ unlimited) rather than metering: in a BYO
model the shared payload registry is the adoption surface and the moat — per-seat pricing taxes
exactly the behavior we want to spread, collaboration value already correlates with slot count,
and dropping the second meter keeps the bill one line long.

### Pv.2 The topology question — every persona reaches us outbound-in-reverse

Crawldad dials **out** (wss:// CDP), so each persona's question is only "can our egress IP reach
your CDP endpoint?":

- **Mid team → Browserbase/Browserless:** trivial; this is the designed happy path (§9.1).
- **Crawling farm → their data centers:** the farm exposes a TLS-terminated CDP edge and
  allowlists our **stable NAT Gateway egress IP** (now a product feature, not just plumbing). For
  operators running fleets, this is a Tuesday.
- **Solo dev → their own machine:** the **documented, supported pattern is a dev tunnel they
  already know how to run** — `ngrok` or `cloudflared` exposing the local browser's CDP endpoint
  (`chrome --remote-debugging-port=9222`, then tunnel `localhost:9222`; the resulting public wss
  URL is the backend binding). Zero novelty: this is the same motion devs already use to receive
  webhooks locally. Honest caveats, all already engineered for:
  - **The tunnel URL is a credential-bearing secret** — anyone holding it owns the browser.
    Treat it *exactly* like a Browserbase `connectUrl`: pass it as a `credentialRef`, never a
    plain input; the existing `CredentialScrubber` + `IRunSecretScope` machinery (SECURITY.md)
    applies unchanged. Add ngrok/cloudflared URL shapes to the scrub test matrix (Pv.5).
  - **Latency:** CDP over a public tunnel is chatty; fine for authoring/debugging iteration
    (the persona's actual job), not a production topology — and that's the natural upsell to a
    real backend, not a limitation to hide.
  - **Tunnel drops mid-run** surface as a retryable connect failure; checkpoint resume (§11)
    already covers recovery. Document it; nothing to build.

  A hosted-side relay/agent (the §9.1 "self-hosted tunnel / thin connector") stays a deferred
  ticket for the enterprise case ("reach our on-prem browser without a public endpoint") — the
  solo dev doesn't need us to run tunnel infrastructure for them.

**Free/paid boundary, drawn accordingly: the free hosted tier is the entire top of funnel** — there
is no other on-ramp — so it must be sized as a real lead magnet (Pv.3). The paid boundary is where
collaboration and continuity live: seats, shared registry, retention, drift monitoring, webhooks,
SLA.

### Pv.3 Tiers, rebuilt for the three personas

BYO browser, BYO storage, BYO vault are **table stakes at every tier including free** — never an
upsell. No infra markup exists anywhere in the table. **Concurrent run slots are the headline
priced axis** (Pv.1); runs and events appear only as fair-use guardrails. Free is deliberately
generous because BYO browser makes a free run's marginal cost *Postgres events plus compute-cents*
(~$0.25–0.50/mo per active free tenant at the caps below) — and because it now carries the whole
funnel.

| | **Free — hosted** (solo dev, tunnel or their Browserbase key) | **Team — $99/mo** (mid team on Browserbase/less) | **Scale — $499/mo** (crawling farm, hosted plane) | **Enterprise — custom** |
|---|---|---|---|---|
| **Concurrent run slots (priced axis)** | **2** | **10 incl.** | **50 incl.** | **hundreds, committed** |
| Add-on slots | — | **$12/slot/mo** (up to 25 total) | **$8/slot/mo**; slots beyond 100 total at **$6** | **~$4–5/slot/mo** committed, dedicated pool |
| At-cap behavior | queue (depth 10) | queue (depth 100) | queue (depth 1,000) | custom admission policy |
| Seats (folded into tier) | 1 | 10 | unlimited | unlimited |
| Managed payloads | 5 | 50 | unlimited | unlimited |
| Timeline retention | 7 days | 30 days | 90 days | custom + archival to your storage |
| Fair-use guardrails (invisible to ~95 %): runs/mo · max steps/run · max events/run | 5,000 · 500 · 2,500 | 50,000 · 2,000 · 10,000 | 500,000 · 10,000 · 50,000 | custom |
| DSL, interpreter, checkpoints/resume, SSE, replay, versioning/pinning/diff | ✔ full | ✔ | ✔ | ✔ |
| BYO browser / storage / vault | ✔ | ✔ | ✔ | ✔ |
| Webhooks | — | ✔ | ✔ | ✔ signed |
| Drift monitoring + scheduled canaries | — | — | ✔ | ✔ |
| Stable egress IPs for your firewall allowlist | shared | ✔ | ✔ (dedicated pair) | dedicated / private peering |
| SSO/SAML, audit export, dedicated DB isolation, 99.9 % SLA, VNet-peered dedicated instance | — | — | — | ✔ |
| Support | community | email | priority | named + Slack |

**Slot economics, worked:** Team headline ≈ $10/slot ($99 / 10). A farm at 300 slots: $499 + 50 ×
$8 + 200 × $6 = **$2,099/mo** — an obvious volume curve ($12 → $8 → $6 → committed ~$4–5) with our
fully-loaded cost per provisioned slot around $1.50–2 (compute share + Postgres + NAT), so margin
survives the steps. Slot counts are also deliberately congruent with what the customer's own
browser vendor sells them (Browserbase Developer = 25 concurrent, Startup = 100): our slot ladder
never makes Crawldad the bottleneck of a stack the customer already paid for.

**Queue-don't-reject:** at the slot cap, run N+1 **queues for a free slot** — the durable
saga/async machinery makes this natural (the run is accepted, 202 + runId, queue position surfaced
in `GET /runs/{id}` and SSE) — because a 429 at your own capacity limit feels like a broken
product, while a visible queue feels like a full one. A 429 (`queue_depth_exceeded`) occurs only past the
per-tier queue depth; a queued run may carry `maxQueueWaitMs` after which it terminates with a
clean `queue_wait_exceeded`. Sustained queue wait IS the upgrade signal, and the dashboard says so:
"p95 queue wait this week: 4 m 12 s — add 5 slots?"

**Upgrade triggers:** solo dev's second concurrent run queues behind the first (2-slot wall) or a
teammate asks "which revision is prod running?" → Team (capacity and collaboration walls arrive
together). Team's p95 queue wait creeps up, or their scraper breaks silently on a Tuesday → more
slots à la carte, then Scale for the cheaper slot curve + drift monitoring ("you found out from
your customer; Scale tells you first"). Farm needs hundreds of committed slots, dedicated egress,
SSO, SLA, or a single-tenant instance inside their cloud perimeter → Enterprise. **Grand-slam
framing (Team/Scale):** *"every automation versioned, watched, and replayable — on your browsers,
into your storage, under your keys."* Risk-reversal stack (closed-source-compatible): payloads and
timelines are **exportable, always** (your JSON, your storage — no data hostage); engine-class
failures auto-credit (the §8.3 taxonomy as a trust feature — with slot pricing this credits
*queue-priority/goodwill credits*, not a metered bill); month-to-month below Enterprise; 30-day
refund.

### Pv.4 Hosting delta (hosted control plane)

- **Shrinks:** Browserbase line item — gone ($0; we hold a dev account for canaries/CI only). Blob —
  mostly gone: screenshots/downloads stream to **BYO storage targets** (presigned URLs or
  customer-credentialed adapters), so CD-2 stops meaning "our retention" and starts meaning "ship
  BYO adapters" (Pv.5); we keep a few GB for our own artifacts. Blob-egress abuse risk — gone
  (their storage, their egress).
- **Stays:** the **Postgres event store is now THE hot marginal cost** — events/run × runs/mo —
  bounded on the billing side by slot-gated peak concurrency plus the runs/events fair-use
  guardrails, and the peak-shaped costs (replicas, NAT, connections) are what the slot price
  provisions for. SSE fan-out unchanged. **SNAT/NAT Gateway unchanged and slightly more important**: all
  browser traffic is outbound wss to *arbitrary customer endpoints* (Browserbase, farm edges,
  ngrok/cloudflared tunnels), and the NAT Gateway's stable egress IP is now a *product feature*.
  The 240-s ingress wall and the 120-s sync cap are untouched.
- **Floors move modestly, not dramatically** (compute + Postgres always dominated): production
  ~$850 → **~$740/mo** (drop Browserbase $99, blob to ~$3); POC ~$29 → **~$27 idle / ~$35 light**
  (canaries run against Browserbase's free tier or a tunneled local Chrome in CI). The real
  economics shift is on the *revenue* side: hosted gross margin approaches SaaS-normal (~90 %+)
  because nothing is resold.

### Pv.5 Backlog re-prioritization under the BYO model

1. **CD-1 auth/tenancy — back on the launch critical path, unambiguously first.** Hosted-only
   means even the free tier (the entire funnel) cannot open without tenant identity, per-tenant
   caps, and seats.
2. **CD-2 — reframed and promoted:** from "our blob + retention" to **BYO storage adapters**
   (S3 / Azure Blob / GCS via customer credentials-by-reference, plus the already-designed
   presigned-URL Target). Every hosted artifact must land in customer storage; our retention story
   shrinks to the Postgres timeline. P0.
3. **CD-3 `maxConcurrentRunsPerTenant` — now THE billing-critical limit**: it *is* the priced
   axis, so it graduates from safety cap to revenue enforcement (per-tenant slot count read from
   the plan, enforced at StartRun admission). `maxEventCount` + max-steps + runs/mo demote to
   **guardrail enforcement** — fair-use ceilings with soft alerts before hard terminal codes.
   Sibling ticket unchanged: **timeline archival to customer storage** at retention expiry (they
   keep history forever; we keep the bill bounded).
3a. **NEW — slot admission queue semantics** (proposed ticket): at-cap StartRun accepts and
   enqueues rather than rejects — durable FIFO per tenant on the existing Wolverine machinery,
   queue position in `GET /runs/{id}` + SSE, per-tier `queueDepth` (429 `queue_depth_exceeded` only past
   it), optional `maxQueueWaitMs` → clean `queue_wait_exceeded`, p95-queue-wait metric surfaced
   per-tenant (it is the upgrade signal). Depends on CD-1 (tenant identity) + CD-3 (slot limit).
4. **CD-6 BYO vault — promoted from Enterprise to core (Team).** BYO-everything is the thesis;
   form-fill credentials and storage credentials both need the by-reference machinery. Enterprise
   keeps only the exotic backends (customer HTTP endpoint, private vault peering).
5. **NEW — tunnel-backend support** (small ticket): document the ngrok/cloudflared local-CDP
   pattern as the official solo-dev on-ramp; add tunnel URL shapes to the credential-scrub test
   matrix; tune connect retry/backoff for tunnel flakiness. Docs + tests, not engineering.
6. CD-12 webhooks (Team gate) and CD-7 canaries → productized drift monitoring (Scale gate) keep
   their M1 slots; the hosted-side relay/agent for on-prem browsers stays deferred until an
   Enterprise deal demands it.

**Top risk under this model:** free-tier funnel concentration — with no other on-ramp, a
mis-sized free tier (too stingy → no funnel; too generous → event-store bloat from short-run
spam through 2 slots) is a single point of failure. Mitigation: the slot counts and guardrail
numbers above are start points, instrumented per-tenant (slot occupancy, p95 queue wait,
events/run, runs/mo) and tuned quarterly; the runs/events guardrails + short retention keep
worst-case free-tenant cost under ~$0.50/mo.

### Pv.6 The open product question

**Where does the Enterprise deployment boundary sit?** Closed-source + hosted-only leaves no
answer for the buyer who says "this must run inside our perimeter" — large crawling farms,
regulated industries, government. The candidate answer is an **operator-managed dedicated
instance** (single-tenant Crawldad we deploy and operate in the customer's cloud subscription or a
VNet-peered enclave — the App-Gateway-for-long-sync variant from §2.2 lives there too); the
alternative is declaring those deals out of scope and staying purely multi-tenant. This decision
sizes the addressable Enterprise segment and the ops burden, and it should be made before the
first farm-scale negotiation — everything else in Pv.3 survives either answer.
