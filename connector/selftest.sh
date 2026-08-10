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

echo
echo "passed: ${pass}, failed: ${fail}"
[ "$fail" -eq 0 ]
