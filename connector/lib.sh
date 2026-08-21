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

# A TCP port entrypoint.sh may bind or target: a base-10 integer in 1..65535, no
# leading zero. Guards CDP_PORT/PROXY_PORT before they are threaded into
# Chromium's flags, the tunnel target, and the rendered nginx config, so a typo
# fails fast instead of producing a silently broken proxy (issue #103).
valid_port() {
    case "$1" in
        '' | *[!0-9]*) return 1 ;; # empty or contains a non-digit
        0*) return 1 ;;            # leading zero (incl. "0" itself) is not a port
    esac
    [ "$1" -ge 1 ] && [ "$1" -le 65535 ]
}

# Render the nginx reverse-proxy config from its template to stdout, substituting
# the two runtime ports entrypoint.sh also honors: PROXY_PORT (the port nginx
# listens on for the tunnel) and CDP_PORT (Chromium's loopback DevTools port it
# proxies to, and the Host it rewrites requests to). entrypoint.sh writes the
# result to a writable path and points `nginx -c` at it, so a user who overrides
# either port gets a matching proxy instead of a silently broken one (issue #103).
#
# Uses sed, not envsubst: it keeps the image dependency-free (envsubst is not
# installed) and, substituting only the two explicit __CDP_PORT__/__PROXY_PORT__
# tokens, it cannot touch nginx's own $-variables (e.g. $http_upgrade,
# $connection_upgrade) the way a naive envsubst pass would. The ports are digits
# only (validated by valid_port), so they are safe as sed replacement text.
render_nginx_conf() {
    local template="$1" cdp_port="$2" proxy_port="$3"
    [ -r "$template" ] || {
        printf 'render_nginx_conf: cannot read template %s\n' "$template" >&2
        return 1
    }
    sed -e "s/__CDP_PORT__/${cdp_port}/g" -e "s/__PROXY_PORT__/${proxy_port}/g" "$template"
}

# Verify a file against an expected SHA-256 hex digest. Returns 0 only on an
# exact match; non-zero (with a one-line reason on stderr) for a missing or
# unreadable file, an empty expected digest, or any mismatch. The expected digest
# is compared case-insensitively. The connector's Dockerfile calls this to gate
# the downloaded cloudflared binary *before* it is installed onto PATH, so a
# corrupted or tampered release asset is never made executable; selftest.sh
# covers the matching (happy) path and the mismatch-refusal path.
verify_sha256() {
    local file="$1" expected="$2" actual
    expected="$(printf '%s' "$expected" | tr '[:upper:]' '[:lower:]')"
    [ -n "$expected" ] || {
        printf 'verify_sha256: no expected digest given for %s\n' "$file" >&2
        return 1
    }
    [ -r "$file" ] || {
        printf 'verify_sha256: cannot read %s\n' "$file" >&2
        return 1
    }
    actual="$(sha256sum "$file" | cut -d' ' -f1)" || return 1
    if [ "$actual" != "$expected" ]; then
        printf 'verify_sha256: checksum mismatch for %s (want %s, got %s)\n' \
            "$file" "$expected" "$actual" >&2
        return 1
    fi
}

# One tick of a supervised component's restart-budget accounting. Pure and
# side-effect-free so selftest.sh can pin the reset semantics without spawning
# processes; the caller (entrypoint.sh `supervise`) owns every side effect.
#
#   restart_budget_step <alive> <restarts> <last_restart_at> <now> <window> <max>
#
#     alive            1 if the component is currently up, 0 if it just died
#     restarts         restarts consumed so far in the current budget window
#     last_restart_at  epoch second of the last restart (0 if never restarted)
#     now              current epoch second
#     window           seconds a component must stay up to have its budget forgiven
#     max              restart budget (MAX_RESTARTS)
#
# Prints three space-separated fields, "<restarts> <last_restart_at> <verdict>":
#     healthy    up, budget unchanged (nothing to forgive yet)
#     forgiven   up for >= window since the last restart; budget reset to 0
#     restart    down; the caller must (re)start it (count incremented, in budget)
#     exhausted  down and the incremented count exceeds max; the caller gives up
#
# Forgiving the budget after a healthy window keeps ordinary tunnel churn over a
# long-lived session from accumulating into a fatal restart storm (issue #69).
restart_budget_step() {
    local alive="$1" restarts="$2" last="$3" now="$4" window="$5" max="$6"
    if [ "$alive" -eq 1 ]; then
        # `now`/`last` are the caller's wall clock (date +%s), so a forward jump
        # (NTP step, VM resume) can make this window elapse early and forgive the
        # budget a little sooner than real elapsed time. Benign and left as-is
        # (issue #103): early forgiveness only relaxes the safety valve, never
        # trips it — exhaustion is purely count-based (below) and the container's
        # restart policy is the backstop. A monotonic clock would remove the skew,
        # but bash has no portable one and the fix belongs at the caller's clock
        # source, not in this pure arithmetic.
        if [ "$restarts" -gt 0 ] && [ "$last" -gt 0 ] && [ "$((now - last))" -ge "$window" ]; then
            printf '0 0 forgiven'
        else
            printf '%s %s healthy' "$restarts" "$last"
        fi
        return 0
    fi
    restarts=$((restarts + 1))
    if [ "$restarts" -gt "$max" ]; then
        printf '%s %s exhausted' "$restarts" "$now"
    else
        printf '%s %s restart' "$restarts" "$now"
    fi
}
