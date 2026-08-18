# Crawldad browser connector

Turn the Chromium on your laptop into a Crawldad backend with one command. This
container starts a headless Chromium, opens a **free, no-account** [cloudflared
quick tunnel](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/do-more-with-tunnels/trycloudflare/),
and **self-registers** the tunnel with Crawldad as a browser credential — so the
only things you supply are your Crawldad API key and a name.

It automates, end to end, the manual flow in
[`../docs/TUNNEL_BACKEND.md`](../docs/TUNNEL_BACKEND.md). This is the free-tier
on-ramp: a laptop-grade authoring/debugging backend, not production (see
[Limitations](#limitations)).

```
 Crawldad ── wss (CDP) ──▶ cloudflared edge ──▶ cloudflared ──▶ nginx ──▶ Chromium
   registers:  wss://<sub>.trycloudflare.com/devtools/browser/<id>   (Host rewritten to localhost)
```

## Quickstart (3 steps)

**1. Export your Crawldad API key** (your tenant key — the same one you use on
`POST /runs`):

```bash
export CRAWLDAD_API_KEY="sk_your_tenant_key"
```

**2. Start the connector:**

```bash
docker compose up --build
```

It builds the image, launches Chromium behind the tunnel, and prints a line like
`Registered browser 'my-laptop' with Crawldad` once the credential is live. (The
tunnel URL is a secret and is stored server-side; it is intentionally **not**
printed.)

**3. Reference it from a payload.** The registered name is the `credentialRef`.
Put this `backend` binding in your run's `backend` input:

```jsonc
{
  "adapter": "browserbase",
  "credentialRef": "my-laptop",
  "options": {
    "mode": "connectUrl",   // REQUIRED: selects connectUrl mode at connect time
    "region": "laptop"      // free-form cache-locality tag; optional
  }
}
```

> **`options.mode: "connectUrl"` is not optional.** The registered credential is
> metadata; what actually selects "the secret IS the CDP URL, connect straight
> over CDP" is the **payload binding's** `options.mode`. Omit it and Crawldad
> treats the secret as a provider API key and the connect fails
> (`backend_unavailable`).

A complete run, against the `tunnel.smoke` payload from
[`../docs/TUNNEL_BACKEND.md` §5](../docs/TUNNEL_BACKEND.md#5-give-crawldad-the-backend-binding):

```jsonc
POST $CRAWLDAD_URL/runs        // Authorization: Bearer <key>  (or  X-Api-Key: <key>)
{
  "payload": {
    "crawldad": "1",
    "name": "tunnel.smoke",
    "inputs": { "backend": { "type": "backend", "required": true } },
    "config": { "backend": "input.backend" },
    "steps": [
      { "goto": { "url": "https://example.com" } },
      { "waitForLoadState": { "state": "load" } },
      { "set": { "var": "title", "value": "trim(coalesce(text('h1'), ''))" } }
    ],
    "result": "{ title: title }"
  },
  "inputs": {
    "backend": {
      "adapter": "browserbase",
      "credentialRef": "my-laptop",
      "options": { "mode": "connectUrl", "region": "laptop" }
    }
  }
}
```

A healthy tunnel returns `200 { "status": "succeeded", "result": { "title": "Example Domain" } }`.

## Configuration

| Env var | Required | Default | Meaning |
|---|---|---|---|
| `CRAWLDAD_API_KEY` | **yes** | — | Your Crawldad tenant API key. Never printed or written to a log. |
| `CRAWLDAD_URL` | no | `https://ca-crawldad-stg.politeflower-5d65f34e.centralus.azurecontainerapps.io` (staging) | Crawldad base URL to register against. |
| `BROWSER_NAME` | no | `my-laptop` | The name the browser registers under — this is your `credentialRef`. Must be a slug (lowercase `a-z`, `0-9`, `-`; 1–64 chars; no leading/trailing hyphen). |

Set them inline, in your shell, or in a `.env` file next to `docker-compose.yml`:

```dotenv
CRAWLDAD_API_KEY=sk_your_tenant_key
BROWSER_NAME=my-laptop
# CRAWLDAD_URL=https://your-crawldad-host
```

## What "ephemeral tunnel" means

A quick tunnel's `https://<random>.trycloudflare.com` hostname is **not stable** —
it changes every time cloudflared reconnects. The connector handles that for you:

- **The tunnel churns → the connector re-registers.** All three processes
  (Chromium, nginx, cloudflared) are supervised. If cloudflared drops and comes
  back with a new URL, the connector re-issues `PUT /browsers/<name>` with the
  new secret under the **same name**, so your `credentialRef` keeps working.
  Each process has a restart budget (`MAX_RESTARTS`, default 10); the budget is
  **forgiven once the process has stayed up `HEALTHY_RESET_SECONDS` (default 600)**,
  so ordinary churn over a long-lived session never accumulates into a fatal
  restart storm. Only a burst of restarts with no healthy recovery in between
  gives up (and the container's `restart: unless-stopped` then self-heals).
- **The container restarts → it re-registers on boot.** Nothing to do; the next
  `docker compose up` registers a fresh tunnel under the same name.
- **A run must fit inside one tunnel lifetime.** Crawldad connects to a backend
  **once** per run and never reconnects mid-run
  ([`../docs/TUNNEL_BACKEND.md` §7](../docs/TUNNEL_BACKEND.md#7-connect-retry--backoff-under-tunnel-flakiness)).
  Keep the laptop awake and the container up for the duration of a run. If a
  connect races a tunnel restart, just re-issue `POST /runs` once it settles.

To remove the registration when you're done, delete it (any authenticated
client):

```bash
curl -X DELETE -H "X-Api-Key: $CRAWLDAD_API_KEY" "$CRAWLDAD_URL/browsers/my-laptop"
```

## Limitations

- **Laptop-grade, 1–2 concurrent browsers.** A public tunnel adds real
  round-trip latency and your machine caps concurrency. This is an
  authoring/debugging topology and the natural upsell is a managed backend
  (Browserbase/Browserless) once a payload works.
- **The tunnel URL is a bearer token.** Anyone with it can drive your browser.
  Crawldad treats it as a secret (encrypted at rest, scrubbed from every log,
  event, timeline, and response). The connector never prints it. Don't paste it
  anywhere.
- **Chromium runs with `--no-sandbox` and `--remote-allow-origins=*`.** The
  sandbox is disabled to run unprivileged in a container; origins are allowed
  because the CDP port is loopback-only and the tunnel URL is the real security
  boundary. Fine for a personal beta backend; don't expose port 9222.

## How it works (and why the proxy)

Chromium's DevTools port rejects any request whose `Host` header isn't
`localhost`/an IP (a DNS-rebinding guard) with `403`. A request arriving via the
tunnel carries `Host: <sub>.trycloudflare.com`, so a small **nginx** reverse
proxy sits between the tunnel and Chromium and rewrites `Host` to
`127.0.0.1:9222`.

The registered secret is the **browser-WebSocket** form
(`wss://<host>/devtools/browser/<id>`), not the `https://` endpoint form. That's
deliberate: Playwright's `connectOverCDP` uses a `wss://` URL **verbatim**,
whereas for an `https://` endpoint it fetches `/json/version` and follows the
returned `webSocketDebuggerUrl` **verbatim** — and behind the Host-rewriting
proxy that URL points at `127.0.0.1:9222`, which isn't reachable through the
tunnel. The `wss://` form sidesteps that entirely.

## Verify without Docker

`selftest.sh` exercises the real parsing and registration code against a local
mock server — no Docker, no real key, no real tunnel:

```bash
bash selftest.sh
```

It asserts the tunnel-URL parse, the `wss://` secret composition, the slug rule,
the exact `PUT /browsers/{name}` request shape, and that the API key never leaks
into log output.

## See also

- [`../docs/TUNNEL_BACKEND.md`](../docs/TUNNEL_BACKEND.md) — the manual flow this
  automates, and the full backend/failure-mode reference.
- [`../docs/API.md` §12](../docs/API.md#12-browsers--browsers) — the browser
  registration API.
