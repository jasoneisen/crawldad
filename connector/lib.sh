# shellcheck shell=bash
#
# Pure helper functions for the Crawldad connector entrypoint.
#
# These are deliberately side-effect-free (no process launches, no globals
# mutated) so they can be unit-tested in isolation by selftest.sh. entrypoint.sh
# sources this file; it owns `set -euo pipefail`, logging, and orchestration.

# Echo the first ephemeral quick-tunnel URL found in a cloudflared log file.
# cloudflared prints a line like "https://random-words-1234.trycloudflare.com".
# Prints nothing (and returns success) when the URL has not appeared yet.
parse_tunnel_url() {
    grep -oE 'https://[a-z0-9][a-z0-9.-]*\.trycloudflare\.com' "$1" 2>/dev/null | head -n1
}

# Extract the path of the browser-level webSocketDebuggerUrl from a
# /json/version JSON document, e.g. "/devtools/browser/<id>". Returns non-zero
# when the field is absent or malformed.
cdp_ws_path() {
    local json="$1" ws rest
    ws="$(printf '%s' "$json" | jq -r '.webSocketDebuggerUrl // empty')" || return 1
    [ -n "$ws" ] || return 1
    rest="${ws#*://}" # strip "ws://" / "wss://"
    case "$rest" in
        */*) printf '/%s' "${rest#*/}" ;; # everything after the host is the path
        *) return 1 ;;
    esac
}

# Compose the connectUrl secret Crawldad connects over: a wss:// URL whose host
# is the tunnel host and whose path is the browser CDP path.
#   build_secret "https://x.trycloudflare.com" "/devtools/browser/ID"
#     -> "wss://x.trycloudflare.com/devtools/browser/ID"
# Playwright's connectOverCDP takes a wss:// URL verbatim (no /json/version
# round-trip), so this is the form that stays reachable through the tunnel.
build_secret() {
    local tunnel="$1" path="$2" host
    host="${tunnel#*://}"
    host="${host%%/*}"
    [ -n "$host" ] || return 1
    printf 'wss://%s%s' "$host" "$path"
}

# Mirror the server's browser-name slug rule: lowercase alnum and hyphen,
# 1..64 chars, no leading or trailing hyphen (BrowserRegistrationRules.NameSlug).
valid_slug() {
    [[ "$1" =~ ^[a-z0-9]([a-z0-9-]{0,62}[a-z0-9])?$ ]]
}
