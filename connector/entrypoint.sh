#!/usr/bin/env bash
#
# Crawldad connector entrypoint.
#
# Brings up a headless Chromium with a CDP port, fronts it with an nginx reverse
# proxy that rewrites the Host header to satisfy Chromium's DevTools host-check,
# opens a free ephemeral cloudflared quick tunnel, and self-registers the tunnel
# URL with Crawldad as a `browserbase`/`connectUrl` browser credential.
#
#   Crawldad  <--wss (CDP)--  cloudflared edge  <--  cloudflared  -->  nginx  -->  Chromium
#             registered secret: wss://<tunnel-host>/devtools/browser/<id>
#
# All three processes are supervised; a dead tunnel means a new URL, so the
# credential is re-registered under the same name. The API key is never printed
# and never placed on a command line.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib.sh
source "${SCRIPT_DIR}/lib.sh"

# --- configuration (env, with sane defaults) --------------------------------

CRAWLDAD_URL="${CRAWLDAD_URL:-https://ca-crawldad-stg.politeflower-5d65f34e.centralus.azurecontainerapps.io}"
CRAWLDAD_URL="${CRAWLDAD_URL%/}" # normalise: drop any trailing slash
BROWSER_NAME="${BROWSER_NAME:-my-laptop}"
CRAWLDAD_API_KEY="${CRAWLDAD_API_KEY:-}"

CDP_PORT="${CDP_PORT:-9222}"   # Chromium's remote-debugging port (loopback only)
PROXY_PORT="${PROXY_PORT:-9223}" # nginx listen port the tunnel targets

RUN_DIR="/tmp/crawldad-connector"
USER_DATA_DIR="${RUN_DIR}/chrome"
NGINX_PREFIX="${RUN_DIR}/nginx"
NGINX_CONF="${SCRIPT_DIR}/nginx.conf"
CF_LOG="${RUN_DIR}/cloudflared.log"

MAX_RESTARTS="${MAX_RESTARTS:-10}" # per-component restart budget before giving up
# A component that stays up this many seconds after a restart has its restart
# budget forgiven (reset to 0). Without this, budgets are cumulative for the whole
# process lifetime, so ordinary tunnel churn over a long session eventually trips
# MAX_RESTARTS and forces a full container restart (issue #69).
HEALTHY_RESET_SECONDS="${HEALTHY_RESET_SECONDS:-600}"

CHROMIUM_BIN="${CHROMIUM_BIN:-}"
CHROMIUM_PID=""
NGINX_PID=""
CF_PID=""

# Populated at runtime.
WS_PATH=""
TUNNEL=""
SECRET=""

# --- logging ----------------------------------------------------------------

log() { printf '%s  %s\n' "$(date -u '+%H:%M:%SZ')" "$*"; }
die() {
    printf '%s  FATAL: %s\n' "$(date -u '+%H:%M:%SZ')" "$*" >&2
    exit 1
}
alive() { kill -0 "$1" 2>/dev/null; }

# --- validation -------------------------------------------------------------

preflight() {
    [ -n "$CRAWLDAD_API_KEY" ] || die "CRAWLDAD_API_KEY is required (export it or put it in .env)."
    valid_slug "$BROWSER_NAME" ||
        die "BROWSER_NAME '${BROWSER_NAME}' is not a valid slug (lowercase a-z, 0-9, hyphen; 1-64; no leading/trailing hyphen)."
    case "$CRAWLDAD_URL" in
        https://* | http://*) : ;;
        *) die "CRAWLDAD_URL must start with http:// or https:// (got '${CRAWLDAD_URL}')." ;;
    esac
    if [ -z "$CHROMIUM_BIN" ]; then
        CHROMIUM_BIN="$(command -v chromium || command -v chromium-browser || command -v google-chrome || true)"
    fi
    [ -n "$CHROMIUM_BIN" ] || die "No Chromium binary found on PATH."
    mkdir -p "$RUN_DIR" "$NGINX_PREFIX"
}

# --- Chromium ---------------------------------------------------------------

start_chromium() {
    rm -rf "$USER_DATA_DIR"
    mkdir -p "$USER_DATA_DIR"
    # --remote-allow-origins=* lets the CDP WebSocket be opened from any Origin
    # (the tunnel URL is the security boundary); --no-sandbox is required to run
    # Chromium in an unprivileged container.
    "$CHROMIUM_BIN" \
        --headless=new \
        --no-sandbox \
        --disable-dev-shm-usage \
        --disable-gpu \
        --disable-background-networking \
        --no-first-run \
        --no-default-browser-check \
        --remote-debugging-address=127.0.0.1 \
        --remote-debugging-port="${CDP_PORT}" \
        --remote-allow-origins='*' \
        --user-data-dir="${USER_DATA_DIR}" \
        about:blank >"${RUN_DIR}/chromium.log" 2>&1 &
    CHROMIUM_PID=$!
    log "Chromium started (pid ${CHROMIUM_PID}) on 127.0.0.1:${CDP_PORT}."
}

wait_for_cdp() {
    local i
    for ((i = 0; i < 60; i++)); do
        if curl -fsS "http://127.0.0.1:${CDP_PORT}/json/version" >/dev/null 2>&1; then
            return 0
        fi
        alive "$CHROMIUM_PID" || return 1
        sleep 1
    done
    return 1
}

# Read the browser CDP path (/devtools/browser/<id>) straight from Chromium over
# loopback, where the Host header is already localhost and needs no rewrite.
discover_ws_path() {
    local json
    json="$(curl -fsS "http://127.0.0.1:${CDP_PORT}/json/version" 2>/dev/null)" || return 1
    cdp_ws_path "$json"
}

# --- nginx (Host-rewriting reverse proxy) -----------------------------------

start_nginx() {
    mkdir -p "$NGINX_PREFIX"
    nginx -p "$NGINX_PREFIX" -c "$NGINX_CONF" -g 'daemon off;' >"${RUN_DIR}/nginx.log" 2>&1 &
    NGINX_PID=$!
    log "nginx started (pid ${NGINX_PID}) proxying 127.0.0.1:${PROXY_PORT} -> 127.0.0.1:${CDP_PORT} (Host rewritten)."
}

# --- cloudflared quick tunnel -----------------------------------------------

start_cloudflared() {
    : >"$CF_LOG" # truncate so we parse the URL from this run, not a stale one
    cloudflared tunnel --no-autoupdate --url "http://127.0.0.1:${PROXY_PORT}" >"$CF_LOG" 2>&1 &
    CF_PID=$!
    log "cloudflared started (pid ${CF_PID}); waiting for a quick-tunnel URL..."
}

await_tunnel_url() {
    local i url
    for ((i = 0; i < 60; i++)); do
        url="$(parse_tunnel_url "$CF_LOG")"
        if [ -n "$url" ]; then
            printf '%s' "$url"
            return 0
        fi
        alive "$CF_PID" || return 1
        sleep 1
    done
    return 1
}

# --- registration -----------------------------------------------------------

# Issue the PUT /browsers/{name}. The API key travels in a 0600 curl config file
# and the body (which contains the secret tunnel URL) travels in a 0600 file, so
# neither ever appears in the process list or a log line. Echoes the HTTP code.
register() {
    local secret="$1" url hdr body code
    url="${CRAWLDAD_URL}/browsers/${BROWSER_NAME}"
    hdr="$(mktemp)"
    body="$(mktemp)"
    chmod 600 "$hdr" "$body"
    printf 'header = "X-Api-Key: %s"\n' "$CRAWLDAD_API_KEY" >"$hdr"
    jq -nc --arg secret "$secret" \
        '{adapter:"browserbase",mode:"connectUrl",secret:$secret}' >"$body"
    code="$(curl -sS -o /dev/null -w '%{http_code}' \
        -X PUT \
        -K "$hdr" \
        -H 'Content-Type: application/json' \
        --data-binary "@${body}" \
        "$url" 2>/dev/null || echo 000)"
    rm -f "$hdr" "$body"
    printf '%s' "$code"
}

# Register with bounded retry. Auth / bad-request failures are fatal (a retry
# cannot fix them); transient failures back off and retry.
register_with_retry() {
    local secret="$1" attempt code
    for ((attempt = 1; attempt <= 5; attempt++)); do
        code="$(register "$secret")"
        case "$code" in
            200)
                log "Registered browser '${BROWSER_NAME}' with Crawldad (connectUrl secret stored server-side; not logged)."
                return 0
                ;;
            401 | 403)
                die "Registration rejected (HTTP ${code}): check CRAWLDAD_API_KEY."
                ;;
            400)
                die "Registration rejected (HTTP 400): check BROWSER_NAME and CRAWLDAD_URL."
                ;;
            *)
                log "Registration attempt ${attempt} failed (HTTP ${code}); retrying in $((attempt * 2))s."
                sleep "$((attempt * 2))"
                ;;
        esac
    done
    return 1
}

# --- lifecycle --------------------------------------------------------------

cleanup() {
    trap - EXIT INT TERM
    log "Stopping connector; tearing down child processes."
    local pid
    for pid in "${CF_PID}" "${NGINX_PID}" "${CHROMIUM_PID}"; do
        if [ -n "$pid" ]; then
            kill "$pid" 2>/dev/null || true
        fi
    done
}

reregister() {
    SECRET="$(build_secret "$TUNNEL" "$WS_PATH")" || die "Could not compose the connect secret."
    register_with_retry "$SECRET" || die "Re-registration failed after retries; the browser would be unreachable."
}

# Per-component supervision. Each restart consumes one unit of that component's
# budget; the budget is forgiven once the component has stayed up for
# HEALTHY_RESET_SECONDS since its last restart (restart_budget_step in lib.sh owns
# that arithmetic), so a long session's ordinary tunnel churn never accumulates
# into a fatal restart storm. Exhausting the budget within a single unhealthy
# window is still fatal.
supervise() {
    local chr_restarts=0 ngx_restarts=0 cf_restarts=0
    local chr_last=0 ngx_last=0 cf_last=0
    local chr_up ngx_up cf_up verdict need_register now step
    while :; do
        sleep 3
        need_register=0
        now="$(date +%s)"

        if alive "$CHROMIUM_PID"; then chr_up=1; else chr_up=0; fi
        step="$(restart_budget_step "$chr_up" "$chr_restarts" "$chr_last" "$now" "$HEALTHY_RESET_SECONDS" "$MAX_RESTARTS")"
        read -r chr_restarts chr_last verdict <<<"$step"
        case "$verdict" in
            exhausted) die "Chromium restarted ${chr_restarts} times without staying up ${HEALTHY_RESET_SECONDS}s; giving up." ;;
            forgiven) log "Chromium healthy for ${HEALTHY_RESET_SECONDS}s; restart budget reset." ;;
            restart)
                log "Chromium exited; restarting (browser id changes, so re-registering)."
                start_chromium
                wait_for_cdp || die "Chromium did not come back up after restart."
                WS_PATH="$(discover_ws_path)" || die "Could not rediscover the CDP WebSocket path after restart."
                need_register=1
                ;;
        esac

        if alive "$NGINX_PID"; then ngx_up=1; else ngx_up=0; fi
        step="$(restart_budget_step "$ngx_up" "$ngx_restarts" "$ngx_last" "$now" "$HEALTHY_RESET_SECONDS" "$MAX_RESTARTS")"
        read -r ngx_restarts ngx_last verdict <<<"$step"
        case "$verdict" in
            exhausted) die "nginx restarted ${ngx_restarts} times without staying up ${HEALTHY_RESET_SECONDS}s; giving up." ;;
            forgiven) log "nginx healthy for ${HEALTHY_RESET_SECONDS}s; restart budget reset." ;;
            restart)
                log "nginx exited; restarting."
                start_nginx
                ;;
        esac

        if alive "$CF_PID"; then cf_up=1; else cf_up=0; fi
        step="$(restart_budget_step "$cf_up" "$cf_restarts" "$cf_last" "$now" "$HEALTHY_RESET_SECONDS" "$MAX_RESTARTS")"
        read -r cf_restarts cf_last verdict <<<"$step"
        case "$verdict" in
            exhausted) die "cloudflared restarted ${cf_restarts} times without staying up ${HEALTHY_RESET_SECONDS}s; giving up." ;;
            forgiven) log "cloudflared healthy for ${HEALTHY_RESET_SECONDS}s; restart budget reset." ;;
            restart)
                log "cloudflared exited; opening a new quick tunnel (URL changes, so re-registering)."
                start_cloudflared
                TUNNEL="$(await_tunnel_url)" || die "cloudflared did not advertise a new tunnel URL."
                need_register=1
                ;;
        esac

        if [ "$need_register" -eq 1 ]; then
            reregister
        fi
    done
}

main() {
    preflight
    trap cleanup EXIT INT TERM

    log "Starting Crawldad connector: name='${BROWSER_NAME}', target='${CRAWLDAD_URL}'."

    start_chromium
    wait_for_cdp || die "Chromium's CDP port never came up (see ${RUN_DIR}/chromium.log)."
    WS_PATH="$(discover_ws_path)" || die "No webSocketDebuggerUrl from Chromium's /json/version."

    start_nginx

    start_cloudflared
    TUNNEL="$(await_tunnel_url)" || die "cloudflared did not advertise a quick-tunnel URL (see ${CF_LOG})."

    reregister

    log "Connector is up. Use credentialRef: '${BROWSER_NAME}' in your payload's backend block."
    supervise
}

# Run only when executed directly; when sourced (e.g. by selftest.sh) the
# functions are exposed without launching any processes.
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    main "$@"
fi
