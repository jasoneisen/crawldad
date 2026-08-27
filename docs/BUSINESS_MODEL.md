# Crawldad — Business model

Crawldad is a **closed-source, hosted-only** declarative browser-automation API sold on a **transparent, bring-your-own-infrastructure** model: no markup on browser compute, no white-labeled infrastructure, at every tier including free. The customer brings their own browser (Browserbase, Browserless, a self-hosted CDP tunnel, or local Chromium), their own storage, and — where relevant — their own vault. Transparency is delivered through the **product**, not source availability: payloads are declarative JSON the customer authors, owns, and can export; every run is fully observable; and BYO browser/storage/vault means their data and infra stay theirs. This document covers what is sold, the priced axis, the tier design, and the cost context. The offer/positioning strategy is in [`MARKETING.md`](MARKETING.md) (which cross-links here for numbers rather than duplicating them).

---

## What's left to sell

With browser and storage BYO, our cost of goods collapses to **control-plane compute plus the Postgres event store**. What remains is the entire reason the product exists:

- the **safe declarative payload language** and its interpreter (loops + content-aware conditions, no `eval` — the thing point-in-time competitors structurally lack);
- **durability**: sagas, checkpoints, resume-across-restarts, deadline enforcement;
- **observability**: live SSE progress, timeline, replay, screenshot-on-failure;
- **payload management**: versioning, pinning, diff, drift detection, canaries;
- **team workflow**: shared payload registry, revision review, actor audit, webhooks.

The reframed dream outcome: *reliable, maintainable browser automation without owning a browser-orchestration codebase.* We price the reliability and the leverage, never the electrons.

## The priced axis: concurrent run slots

The single priced unit is the **concurrent run slot**. The case for it:

- **It prices exactly what we build for.** In the BYO model every cost we still carry is peak-capacity shaped: each in-flight run holds an outbound socket, an executor loop, a live saga, an appending event stream, and Postgres connections — and replicas, NAT, and the database are provisioned for *peak concurrency*, not monthly totals. A tenant's slot count is a direct claim on provisioned capacity.
- **It's legible, fair-feeling, hard to game, and cheap to enforce.** No metering pipeline, no surprise bills, no end-of-month reconciliation — the admission gate at `POST /runs` is the entire billing-enforcement surface.
- **It self-selects the personas.** A tunneled laptop physically caps at 1–2 concurrent browsers; a team on Browserbase wants 10–25; a farm wants hundreds — which is exactly where our peak cost lives. The price axis and the persona ladder are the same axis, and slot counts are kept congruent with what the customer's own browser vendor already sells them, so Crawldad is never the bottleneck of a stack they already paid for.

**The caveat, handled:** slots don't track Postgres event *volume* (the dominant marginal cost) — a tenant could spam thousands of tiny runs through a couple of slots. So runs/month and events-per-run become **fair-use guardrails**: invisible to ~95 % of legitimate users, and for the pathological short-run-spam profile they are the upgrade conversation, not a bill.

## Metering vs caps vs guardrails

Nothing is metered in the usage-billing sense. The model is one priced allowance plus tiered ladders and fair-use ceilings:

| Dimension | Role | Rationale |
|---|---|---|
| **Concurrent run slots** | **priced allowance** (enforced at admission) | the one truly peak-scarce, cost-shaped resource |
| Seats | folded into tiers | in a BYO model the shared payload registry is the adoption surface and moat; per-seat pricing would tax the behavior we want to spread |
| Active managed payloads | tier ladder | automations under management — near-zero marginal cost, the value ladder |
| Timeline retention window | tier ladder | Postgres GB — a real cost that scales with debugging/audit value |
| Runs/month, events/run | fair-use guardrails | noisy proxies for cost that slots already bound at the peak; soft alerts before hard ceilings |
| Drift monitoring, canaries, webhooks | feature gates | high value, low incremental cost |

## Tiers (BYO at every tier)

BYO browser, storage, and vault are **table stakes at every tier including free** — never an upsell — and no infra markup exists anywhere in the table. Concurrent slots are the headline priced axis; runs and events appear only as fair-use guardrails. Free is deliberately generous because BYO browser makes a free run's marginal cost only Postgres events plus compute-cents (~$0.25–0.50/mo per active free tenant at these caps), and because it carries the whole top of funnel.

| | **Free — hosted** | **Team — $99/mo** | **Scale — $499/mo** | **Enterprise — custom** |
|---|---|---|---|---|
| **Concurrent run slots (priced axis)** | **2** | **10 incl.** | **50 incl.** | hundreds, committed |
| Add-on slots | — | **$12/slot/mo** (up to 25 total) | **$8/slot/mo**; beyond 100 total at **$6** | ~$4–5/slot/mo, dedicated pool |
| At-cap behavior | queue (depth 10) | queue (depth 100) | queue (depth 1,000) | custom admission policy |
| Seats | 1 | 10 | unlimited | unlimited |
| Managed payloads | 5 | 50 | unlimited | unlimited |
| Timeline retention | 7 days | 30 days | 90 days | custom + archival to your storage |
| Fair-use guardrails (runs/mo · steps/run · events/run) | 5,000 · 500 · 2,500 | 50,000 · 2,000 · 10,000 | 500,000 · 10,000 · 50,000 | custom |
| DSL, interpreter, checkpoints/resume, SSE, replay, versioning/pinning/diff | ✔ | ✔ | ✔ | ✔ |
| BYO browser / storage / vault | ✔ | ✔ | ✔ | ✔ |
| Webhooks | — | ✔ | ✔ | ✔ signed |
| Drift monitoring + scheduled canaries | — | — | ✔ | ✔ |
| Stable egress IPs for your allowlist | shared | ✔ | ✔ (dedicated pair) | dedicated / private peering |
| SSO/SAML, audit export, dedicated DB isolation, 99.9 % SLA, VNet-peered instance | — | — | — | ✔ |
| Support | community | email | priority | named + Slack |

**Slot economics, worked.** Team headline ≈ $10/slot ($99 / 10). A farm at 300 slots pays `$499 + 50 × $8 + 200 × $6 = $2,099/mo` — an obvious volume curve ($12 → $8 → $6 → committed ~$4–5) against a fully-loaded cost per provisioned slot around **$1.50–2** (compute share + Postgres + NAT), so margin survives the steps.

**Queue, don't reject.** At the slot cap, run N+1 queues for a free slot (the durable saga/async machinery makes this natural — `202 + runId`, queue position surfaced in `GET /runs/{id}` and SSE), because a `429` at your own capacity limit feels like a broken product while a visible queue feels like a full one. A `429` occurs only past the per-tier queue depth. Sustained queue wait *is* the upgrade signal, and the dashboard says so ("p95 queue wait this week: 4 m 12 s — add 5 slots?").

### What's enforced today vs packaging intent

Accuracy matters here: the matrix above is the **pricing design**, and only part of it is wired as per-tenant enforcement today.

- **Enforced per tenant now:** the concurrent-slot allowance and the queue depth, applied as per-tenant overrides of the platform defaults (`MaxConcurrentRunsPerTenant`, `MaxQueueDepthPerTenant`). The slot allowance is now a durable, per-tenant value administered through the **store-backed tenant registry and its management API** (PR #122) — no longer only a static config override. Absent an override the platform defaults apply — **32 concurrent runs and a queue depth of 1,000** — which are the operative numbers until a tenant is placed on a tier. See [`SPEC.md`](SPEC.md#resource-limits) / [`API.md` §15.4](API.md).
- **Enforced globally, not yet per tier:** max steps, max downloaded bytes, max events, and the expression budget are single deployment-wide caps today (defaults 100,000 · 1 GiB · 100,000 · 1,000,000), and retention is a single host-wide policy (downloads 30 d / screenshots 7 d). The per-tier guardrail and retention numbers above are the intended per-tenant values.
- **Capability shipped; per-tier gating still packaging intent:** the feature-gated capabilities the tiers describe have largely shipped — **webhooks** (issue #12: signed HMAC-SHA256 delivery, retried, SSRF-guarded; [`API.md` §14](API.md)) and **on-read drift detection** (issue #47: per-run revision drift via `GET /runs/{id}/drift` and per-selector payload drift via `GET /payloads/{id}/drift-status`; [`API.md` §9](API.md)) are reachable by any authenticated tenant today. What is still *packaging intent, not yet mechanized* is the per-tenant **entitlement gating** of those features by tier (Free has no webhooks; drift is Scale+) and the metered ladders — seats, managed-payload counts, runs/month, per-tier retention windows.
- **Genuinely still unbuilt:** the **scheduled canary/monitor** that would re-run pinned revisions to feed the drift signal automatically (issue #7 — today drift is computed on read, never scheduled), and **timeline archival to customer storage** for the Scale/Enterprise retention promise (issue #46).

## Cost context & reference bills

The platform is structurally cheap because browsers — the expensive part — are the customer's. The infrastructure floor is detailed in [`ARCHITECTURE.md`](ARCHITECTURE.md#part-b--infrastructure-reference-architecture); the bottom lines that matter for pricing:

- **Steady-state production floor: ~$740/mo** (compute + Postgres dominate; the managed-browser line item is a small dev account for canaries/CI, and blob is a few GB of our own artifacts). Breakeven is roughly two Team seats or a fraction of one Scale seat.
- **POC floor: ~$27/mo idle, ~$35/mo light** (or ~$13/mo on a 12-month Azure free account), on consumption compute + a burstable database, validating the same ingress contract.
- **Gross margin approaches SaaS-normal (~90 %+)** because nothing is resold — the revenue economics improve precisely because the cost model is transparent BYO.

## Free-tier funnel risk

Because the hosted free tier is the **entire** top of funnel — there is no other on-ramp — a mis-sized free tier is a single point of failure: too stingy and there is no funnel; too generous and event-store bloat from short-run spam through 2 slots erodes the economics. Mitigation: the slot counts and guardrail numbers above are start points, instrumented per tenant (slot occupancy, p95 queue wait, events/run, runs/mo) and tuned quarterly; the runs/events guardrails plus short retention keep worst-case free-tenant cost under ~$0.50/mo.

## The Enterprise deployment boundary — decided

**Decision (2026-08-10, issue #49): operator-managed dedicated instance.** For the buyer who says "this must run inside our perimeter" (large farms, regulated industries, government), Enterprise offers a single-tenant Crawldad that we deploy and operate in the customer's cloud subscription or a VNet-peered enclave. In-perimeter deals are therefore in scope for the Enterprise tier rather than declined, which sizes the addressable Enterprise segment to include those buyers and accepts the corresponding ops burden per instance. The rest of the tier design is unchanged by this decision; the multi-tenant hosted platform remains the product for every other tier.
