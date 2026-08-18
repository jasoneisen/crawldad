#!/usr/bin/env bash
#
# Docker-free validation of the connector's parsing and registration logic.
#
# It sources entrypoint.sh (whose main() is source-guarded, so nothing launches)
# to exercise the real helpers, then drives the real register path against a
# throwaway local mock HTTP server — no Docker, no real API key, no real tunnel.
#
# What it proves:
#   * the ephemeral trycloudflare URL is parsed out of a cloudflared log
#   * the CDP ws path is extracted from /json/version and composed into the
#     wss:// connectUrl secret Crawldad connects over
#   * the browser-name slug rule matches the server's
#   * PUT /browsers/{name} is issued with the right method, path, auth header,
#     and JSON body
#   * the API key never appears in the connector's own log output

set -euo pipefail

SELF_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=entrypoint.sh
source "${SELF_DIR}/entrypoint.sh"

pass=0
fail=0
check() { # desc actual expected
    if [ "$2" = "$3" ]; then
        printf 'ok   - %s\n' "$1"
        pass=$((pass + 1))
    else
        printf 'FAIL - %s\n       expected: [%s]\n       actual:   [%s]\n' "$1" "$3" "$2"
        fail=$((fail + 1))
    fi
}
check_contains() { # desc haystack needle
    if printf '%s' "$2" | grep -qF -- "$3"; then
        printf 'ok   - %s\n' "$1"
        pass=$((pass + 1))
    else
        printf 'FAIL - %s (missing: %s)\n' "$1" "$3"
        fail=$((fail + 1))
    fi
}
check_absent() { # desc haystack needle
    if printf '%s' "$2" | grep -qF -- "$3"; then
        printf 'FAIL - %s (unexpectedly present: %s)\n' "$1" "$3"
        fail=$((fail + 1))
    else
        printf 'ok   - %s\n' "$1"
        pass=$((pass + 1))
    fi
}

WORK="$(mktemp -d)"
MOCK_PID=""
cleanup_selftest() {
    if [ -n "$MOCK_PID" ]; then
        kill "$MOCK_PID" 2>/dev/null || true
    fi
    rm -rf "$WORK"
}
trap cleanup_selftest EXIT

echo "== pure helpers =="

# A realistic cloudflared quick-tunnel banner.
cat >"${WORK}/cf.log" <<'EOF'
2026-08-10T00:00:00Z INF Thank you for trying Cloudflare Tunnel.
2026-08-10T00:00:00Z INF +--------------------------------------------------------------------------------------------+
2026-08-10T00:00:00Z INF |  Your quick Tunnel has been created! Visit it at (it may take some time to be reachable):  |
2026-08-10T00:00:00Z INF |  https://random-forest-1234.trycloudflare.com                                              |
2026-08-10T00:00:00Z INF +--------------------------------------------------------------------------------------------+
EOF
check "parse_tunnel_url extracts the quick-tunnel URL" \
    "$(parse_tunnel_url "${WORK}/cf.log")" \
    "https://random-forest-1234.trycloudflare.com"

check "parse_tunnel_url is empty before the URL appears" \
    "$(parse_tunnel_url /dev/null)" \
    ""

VERSION_JSON='{"Browser":"HeadlessChrome/149.0","webSocketDebuggerUrl":"ws://127.0.0.1:9222/devtools/browser/9b1de9c7-abcd"}'
check "cdp_ws_path extracts the browser CDP path" \
    "$(cdp_ws_path "$VERSION_JSON")" \
    "/devtools/browser/9b1de9c7-abcd"

check "build_secret composes the wss connectUrl secret" \
    "$(build_secret "https://random-forest-1234.trycloudflare.com" "/devtools/browser/9b1de9c7-abcd")" \
    "wss://random-forest-1234.trycloudflare.com/devtools/browser/9b1de9c7-abcd"

# End-to-end compose, exactly as the entrypoint chains them.
E2E_TUNNEL="$(parse_tunnel_url "${WORK}/cf.log")"
E2E_PATH="$(cdp_ws_path "$VERSION_JSON")"
check "full parse -> path -> secret pipeline" \
    "$(build_secret "$E2E_TUNNEL" "$E2E_PATH")" \
    "wss://random-forest-1234.trycloudflare.com/devtools/browser/9b1de9c7-abcd"

echo "== slug rule =="
slug_ok() { if valid_slug "$1"; then echo 0; else echo 1; fi; }
check "slug: my-laptop is valid" "$(slug_ok "my-laptop")" "0"
check "slug: lone token is valid" "$(slug_ok "laptop")" "0"
check "slug: uppercase is rejected" "$(slug_ok "MyLaptop")" "1"
check "slug: underscore is rejected" "$(slug_ok "my_laptop")" "1"
check "slug: leading hyphen is rejected" "$(slug_ok "-laptop")" "1"
check "slug: trailing hyphen is rejected" "$(slug_ok "laptop-")" "1"
check "slug: empty is rejected" "$(slug_ok "")" "1"

echo "== registration shape (mock server, synthetic key) =="

# A throwaway HTTP server that records the PUT it receives and answers 200.
cat >"${WORK}/mock.py" <<'PY'
import sys, json
from http.server import BaseHTTPRequestHandler, HTTPServer

capture, port_file = sys.argv[1], sys.argv[2]

class H(BaseHTTPRequestHandler):
    def do_PUT(self):
        n = int(self.headers.get('Content-Length', 0))
        body = self.rfile.read(n).decode('utf-8', 'replace')
        with open(capture, 'w') as f:
            f.write("METHOD %s\n" % self.command)
            f.write("PATH %s\n" % self.path)
            for k, v in self.headers.items():
                f.write("HEADER %s: %s\n" % (k, v))
            f.write("BODY %s\n" % body)
        payload = json.dumps({"name": "my-laptop"}).encode()
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)
    def log_message(self, *a):
        pass

srv = HTTPServer(('127.0.0.1', 0), H)
with open(port_file, 'w') as f:
    f.write(str(srv.server_address[1]))
srv.serve_forever()
PY

python3 "${WORK}/mock.py" "${WORK}/capture.txt" "${WORK}/port.txt" &
MOCK_PID=$!
for _ in $(seq 1 50); do
    [ -s "${WORK}/port.txt" ] && break
    sleep 0.1
done
MOCK_PORT="$(cat "${WORK}/port.txt")"
[ -n "$MOCK_PORT" ] || {
    echo "mock server did not start"
    exit 1
}

# Point the real register path at the mock, with synthetic (non-secret) values.
SYNTH_KEY="synthetic-key-do-not-use-abc123"
CRAWLDAD_URL="http://127.0.0.1:${MOCK_PORT}"
CRAWLDAD_API_KEY="$SYNTH_KEY"
BROWSER_NAME="my-laptop"
SECRET_UNDER_TEST="wss://random-forest-1234.trycloudflare.com/devtools/browser/9b1de9c7-abcd"

set +e
REG_LOG="$(register_with_retry "$SECRET_UNDER_TEST" 2>&1)"
REG_RC=$?
set -e
check "register_with_retry succeeds against a 200" "$REG_RC" "0"

CAP="$(cat "${WORK}/capture.txt")"
check_contains "method is PUT" "$CAP" "METHOD PUT"
check_contains "path is /browsers/my-laptop" "$CAP" "PATH /browsers/my-laptop"
check_contains "X-Api-Key header carries the key" "$CAP" "$SYNTH_KEY"

# Validate the JSON body field-by-field.
BODY_JSON="$(printf '%s\n' "$CAP" | sed -n 's/^BODY //p')"
check "body.adapter == browserbase" "$(printf '%s' "$BODY_JSON" | jq -r '.adapter')" "browserbase"
check "body.mode == connectUrl" "$(printf '%s' "$BODY_JSON" | jq -r '.mode')" "connectUrl"
check "body.secret == the wss connectUrl" "$(printf '%s' "$BODY_JSON" | jq -r '.secret')" "$SECRET_UNDER_TEST"

# Secret hygiene: the key must never surface in the connector's own log output.
check_absent "API key absent from connector log output" "$REG_LOG" "$SYNTH_KEY"
check_contains "log confirms registration by name" "$REG_LOG" "Registered browser 'my-laptop'"

echo "== cloudflared checksum verification (verify_sha256) =="

# The exact gate the Dockerfile runs on the downloaded cloudflared binary before
# it is ever installed onto PATH: a matching digest verifies, anything else
# refuses (so a tampered/corrupted asset never becomes executable).
SHA_FILE="${WORK}/asset.bin"
printf 'pretend cloudflared payload\n' >"$SHA_FILE"
SHA_GOOD="$(sha256sum "$SHA_FILE" | cut -d' ' -f1)"
SHA_BAD="0000000000000000000000000000000000000000000000000000000000000000"

set +e
verify_sha256 "$SHA_FILE" "$SHA_GOOD" 2>/dev/null
RC_GOOD=$?
verify_sha256 "$SHA_FILE" "${SHA_GOOD^^}" 2>/dev/null # uppercase digest
RC_UPPER=$?
MISMATCH_ERR="$(verify_sha256 "$SHA_FILE" "$SHA_BAD" 2>&1 >/dev/null)"
RC_BAD=$?
verify_sha256 "$SHA_FILE" "" 2>/dev/null
RC_EMPTY=$?
verify_sha256 "${WORK}/does-not-exist" "$SHA_GOOD" 2>/dev/null
RC_MISSING=$?
set -e

refused() { if [ "$1" -ne 0 ]; then echo refused; else echo executed; fi; }
check "matching checksum verifies (happy path)" "$RC_GOOD" "0"
check "uppercase expected digest still verifies" "$RC_UPPER" "0"
check "mismatched checksum refuses to execute" "$(refused "$RC_BAD")" "refused"
check_contains "mismatch reason names the checksum" "$MISMATCH_ERR" "checksum mismatch"
check "empty expected digest is refused" "$(refused "$RC_EMPTY")" "refused"
check "missing file is refused" "$(refused "$RC_MISSING")" "refused"

echo "== restart budget (restart_budget_step) =="

# window = 600s healthy-before-forgiven, max = 10 restarts.
check "alive with no restarts stays at zero" \
    "$(restart_budget_step 1 0 0 1000 600 10)" "0 0 healthy"
check "alive within the window keeps the count" \
    "$(restart_budget_step 1 3 1000 1599 600 10)" "3 1000 healthy"
check "alive at exactly the window forgives the budget" \
    "$(restart_budget_step 1 3 1000 1600 600 10)" "0 0 forgiven"
check "alive past the window forgives the budget" \
    "$(restart_budget_step 1 3 1000 5000 600 10)" "0 0 forgiven"
check "alive with an unset anchor never forgives" \
    "$(restart_budget_step 1 3 0 99999 600 10)" "3 0 healthy"
check "death increments and asks for a restart" \
    "$(restart_budget_step 0 0 0 1000 600 10)" "1 1000 restart"
check "the MAX_RESTARTS-th restart is still allowed" \
    "$(restart_budget_step 0 9 500 1000 600 10)" "10 1000 restart"
check "one past MAX_RESTARTS is exhausted" \
    "$(restart_budget_step 0 10 500 1000 600 10)" "11 1000 exhausted"

# Replay the *real* accounting through a scripted clock and report the terminal
# state, pinning the reset semantics end-to-end. sample = alive|dead:<epoch>.
replay_budget() { # window max sample...
    local window="$1" max="$2"
    shift 2
    local restarts=0 last=0 verdict a n out sample
    for sample in "$@"; do
        case "${sample%%:*}" in
            alive) a=1 ;;
            dead) a=0 ;;
        esac
        n="${sample##*:}"
        out="$(restart_budget_step "$a" "$restarts" "$last" "$n" "$window" "$max")"
        read -r restarts last verdict <<<"$out"
        [ "$verdict" = "exhausted" ] && {
            printf 'exhausted@%s' "$n"
            return 0
        }
    done
    printf 'survived:%s' "$restarts"
}

# Rapid churn (a death every tick, far inside the window) still trips the budget
# after MAX_RESTARTS+1 deaths — the safety valve is intact.
check "rapid churn exhausts after MAX_RESTARTS+1 deaths" \
    "$(replay_budget 600 10 \
        dead:0 dead:3 dead:6 dead:9 dead:12 dead:15 \
        dead:18 dead:21 dead:24 dead:27 dead:30)" \
    "exhausted@30"

# Churn spaced by a full healthy window forgives each time, so 40 death/recovery
# cycles — four times MAX_RESTARTS — never exhaust the budget (issue #69).
spaced=()
t=0
for _ in $(seq 1 40); do
    spaced+=("dead:${t}")
    spaced+=("alive:$((t + 600))") # up for a full window -> forgiven
    t=$((t + 600))
done
check "churn spaced by a healthy window never exhausts" \
    "$(replay_budget 600 10 "${spaced[@]}")" "survived:0"

echo
echo "passed: ${pass}, failed: ${fail}"
[ "$fail" -eq 0 ]
