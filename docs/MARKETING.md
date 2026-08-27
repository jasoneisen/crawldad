# Crawldad — Positioning & go-to-market strategy

This is the strategy doc: the offer framing, the personas and their on-ramps, and the named risks. It is deliberately strategy-only — every price, tier quantity, and reference bill lives in [`BUSINESS_MODEL.md`](BUSINESS_MODEL.md), which this cross-links rather than duplicates.

---

## The offer (value-equation framing)

Under the transparent BYO model the offer is not "we run browsers for you" — the customer already owns infra. It is *reliable, maintainable browser automation without owning a browser-orchestration codebase.* What they don't want to own is the interpreter, the retry/error taxonomy, the checkpoint saga, and the drift tooling that rot on a shelf. Applied to the four levers of the value equation:

- **Dream outcome** — browser automations that don't silently rot: declarative, versioned, replayable, drift-detected.
- **Perceived likelihood of achievement** — you watch *your own* browser session driven live over SSE, replay the exact pinned revision, and see the failure screenshot. The proof is in the product, not the pitch.
- **Time delay** — point a payload at your existing `connectUrl`: no new infra, no VPN, first run in minutes.
- **Effort & sacrifice** — a JSON payload instead of a Playwright codebase, and no infrastructure to operate.

**Transparency is itself the risk-reversal.** Nothing is resold, so no bill can surprise you; your artifacts land in *your* storage; and your payloads are *your* JSON, exportable any day. Closed-source + hosted-only is not a compromise of that transparency — it is what keeps the cross-customer drift signal (below) accreting, while the customer's data and infra stay theirs.

## The three personas and their on-ramps

Crawldad always dials **out** to a CDP endpoint, so each persona's only question is "can Crawldad's egress reach my browser?" That single topology fact defines three natural on-ramps, and the slot-priced ladder maps onto them exactly (see [`BUSINESS_MODEL.md`](BUSINESS_MODEL.md)).

- **Solo dev → their own machine.** The on-ramp is a dev tunnel they already know (`ngrok`/`cloudflared` exposing local Chrome's debugging port); the public wss URL is the backend binding, passed as a credential reference. Zero novelty — the same motion devs use to receive webhooks locally. This is the free tier, and it is the **entire** top of funnel, so it must be a real lead magnet. The step-by-step on-ramp is [`docs/TUNNEL_BACKEND.md`](TUNNEL_BACKEND.md).
- **Mid team → Browserbase / Browserless.** The designed happy path: trivial to connect, nothing to build. This is where collaboration value (shared payload registry, revision review) and continuity (retention, webhooks) begin to matter — the paid boundary.
- **Crawling farm → their data centers.** The farm exposes a TLS-terminated CDP edge and allowlists Crawldad's stable egress IP (now a product feature, not just plumbing). This is where our peak cost lives, and where the volume slot curve and dedicated egress/SSO/SLA/single-tenant options apply.

The free/paid boundary is drawn accordingly: **the free hosted tier is the whole funnel entrance**; the paid boundary is where collaboration and continuity live — seats, shared registry, retention, drift monitoring, webhooks, SLA.

## The grand-slam framing

For the Team and Scale buyer, the one-line offer is: *every automation versioned, watched, and replayable — on your browsers, into your storage, under your keys.* The risk-reversal stack is closed-source-compatible:

- **Export everything, always** — payloads and timelines are your JSON in your storage, so there is no data hostage and switching cost is near zero (which, counter-intuitively, *increases* willingness to commit).
- **Engine-class failure credit** — a run that fails because of an engine or infra fault (not a payload `guard`/terminal or a target-site failure) earns a goodwill/queue-priority credit; because slots are the priced axis, this is a trust gesture, not a metered refund.
- **No lock-in below Enterprise** — month-to-month, plus a 30-day refund.

## Upgrade triggers (each a felt moment)

Growth comes from capacity and collaboration walls arriving together, surfaced honestly in the product:

- **Free → Team.** The solo dev's second concurrent run queues behind the first (the 2-slot wall), or a teammate asks "which revision is prod running?" — capacity and collaboration walls at once.
- **Team → Scale.** The team's p95 queue wait creeps up ("add slots?"), or their scraper breaks silently on a Tuesday and they find out from *their* customer — Scale's drift monitoring tells them first.
- **Scale → Enterprise.** The farm needs hundreds of committed slots, dedicated egress, SSO/SLA, or a single-tenant instance inside their perimeter.

## The moat

Because all execution is data and runs through one hosted plane, cross-customer signal — which selector broke, on which host, when — accretes in the trace events and the asset cache. That is the long-term product: per-tenant drift detection has shipped (issue #47 — the on-read drift-status signal), and turning the *aggregate*, cross-customer drift signal into proactive, scheduled monitoring is the durable differentiator still ahead, fed by the nightly canary (**issue #7**). Hosted-only is what keeps this signal intact; it is the strategic reason the product is not source-available.

## Named risks & mitigations

| Risk | Mitigation |
|---|---|
| **Free-tier funnel concentration.** With no other on-ramp, a mis-sized free tier is a single point of failure (too stingy → no funnel; too generous → event-store bloat from short-run spam). | Size free as a genuine lead magnet, instrument it per tenant, and tune quarterly; runs/events guardrails + short retention cap worst-case free-tenant cost (see [`BUSINESS_MODEL.md`](BUSINESS_MODEL.md#free-tier-funnel-risk)). |
| **Free-tier abuse** (scraping farms, credential stuffing, card testing). Our brand is on the run records even though egress is the customer's browser. | Low slot count + fair-use guardrails make farming uneconomic; identity for anything above free; and the **declarative-only DSL is itself a mitigation** — no `eval`, an inspectable effect surface — worth marketing to that effect. |
| **Event-store bloat outruns revenue.** Chatty payloads write megabytes of events per run; Postgres growth is the dominant marginal cost. | Per-run event caps, slot-gated peak concurrency, and archival of completed streams to customer storage (**issue #46**); Scale pricing already assumes heavy runs. |
| **Single-vendor browser dependency.** | Structurally defused by BYO — the browser is the customer's account — and the `IBrowserBackend` seam is multi-backend by design, so no single provider is load-bearing. |
| **Oversold concurrency.** Sold slots could exceed provisioned peak capacity. | Slots are a claim on *provisioned* capacity, and admission is queue-not-fail, so a burst degrades to visible queue wait (the upgrade signal), never a hard failure; track sold-slots vs provisioned peak. |
