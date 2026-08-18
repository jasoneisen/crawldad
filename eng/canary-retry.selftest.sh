#!/usr/bin/env bash
#
# Docker-free, dotnet-free unit test for the live-canary retry-once wrapper (eng/canary-retry.sh, issue #92).
#
# It sources canary-retry.sh (whose direct-run block is source-guarded, so nothing launches and no real canary runs),
# then drives the pure verdict helper and the retry orchestration with a SCRIPTED fake canary and a RECORDING no-op
# sleep. No Docker, no .NET, no live traffic, no real waiting. Mirrors connector/selftest.sh.
#
# What it pins (the retry-once semantics from issue #92's acceptance criteria):
#   * record_not_accessible on attempt 1 -> exactly ONE delayed retry, and
#       * ... then success               -> GREEN job, retry ::warning:: emitted, slept exactly once for the delay
#       * ... then record_not_accessible -> RED job (alarms)
#       * ... then a different failure    -> RED job (alarms)
#   * a first-attempt success             -> GREEN, no retry, never slept
#   * ANY other failure class on attempt 1 -> RED immediately, no retry, never slept (real drift fails fast)
set -euo pipefail

SELF_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=canary-retry.sh
source "${SELF_DIR}/canary-retry.sh"

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
trap 'rm -rf "$WORK"' EXIT
# Keep every mktemp the wrapper makes inside the throwaway dir.
export TMPDIR="$WORK"

echo "== canary_retry_verdict (pure decision helper) =="

printf 'Passed!  1 test\n' >"${WORK}/ok.log"
check "exit 0 -> pass" "$(canary_retry_verdict 0 "${WORK}/ok.log")" "pass"

# A realistic slice of the failed `dotnet test` output for the maintenance-window case: the canary emits the full
# response (pretty-printed) with the terminal record_not_accessible failure payload.
cat >"${WORK}/rna.log" <<'EOF'
  Failed Crawldad.Tests.Integration.LiveCanaryTests.Scrapes_one_live_record_into_a_structurally_valid_record
  Error Message:
   Live canary drift — the run did not succeed. Full response:
   {
     "status": "failed",
     "failure": {
       "class": "terminal",
       "code": "record_not_accessible",
       "message": "Record not accessible (redirected to /LJCMG/Error.aspx)"
     }
   }
EOF
check "exit != 0 with record_not_accessible -> retry" "$(canary_retry_verdict 1 "${WORK}/rna.log")" "retry"

# A different terminal failure (real drift): a shape/selector break carries a different code — never the retry slug.
cat >"${WORK}/other.log" <<'EOF'
  Error Message:
   { "status": "failed", "failure": { "class": "terminal", "code": "selector_not_found" } }
EOF
check "exit != 0 with a different code -> fail" "$(canary_retry_verdict 1 "${WORK}/other.log")" "fail"
check "exit != 0 with empty output -> fail (e.g. infra/build error)" "$(canary_retry_verdict 1 /dev/null)" "fail"

echo "== run_canary_with_retry (scripted fake canary + recording sleep) =="

# A scripted fake canary: per-attempt exit code + emitted output come from parallel arrays indexed by a counter FILE.
# A plain global counter won't do — each attempt runs inside the tee pipeline's subshell, which cannot mutate the
# parent's variables; a file persists across the subshells. The arrays ARE inherited by the subshell (read-only there).
FAKE_RCS=()
FAKE_OUTS=()
ATTEMPT_FILE="${WORK}/attempts"
fake_canary() {
    local n
    n="$(cat "$ATTEMPT_FILE")"
    n=$((n + 1))
    printf '%s' "$n" >"$ATTEMPT_FILE"
    printf '%s\n' "${FAKE_OUTS[n - 1]}"
    return "${FAKE_RCS[n - 1]}"
}

# A recording no-op sleep: the retry path never actually waits. Counts calls and remembers the last requested delay.
SLEEP_CALLS=0
SLEEP_LAST=""
record_sleep() {
    SLEEP_CALLS=$((SLEEP_CALLS + 1))
    SLEEP_LAST="$1"
    return 0
}

RNA_LINE='  { "failure": { "code": "record_not_accessible" } }'
OTHER_LINE='  { "failure": { "code": "selector_not_found" } }'

# Drive one orchestration scenario end to end. Resets the attempt counter and sleep recorder, runs the real
# orchestration with the fake canary + recording sleep, and captures the outcome. Output is redirected to a FILE (not
# $(...)), so the orchestration runs in THIS shell and the recorded globals (SLEEP_CALLS/SLEEP_LAST) survive.
run_scenario() { # delay
    printf '0' >"$ATTEMPT_FILE"
    SLEEP_CALLS=0
    SLEEP_LAST=""
    set +e
    CANARY_SLEEP_CMD=record_sleep run_canary_with_retry "$1" fake_canary >"${WORK}/orc.out" 2>&1
    ORC_RC=$?
    set -e
    ORC_OUT="$(cat "${WORK}/orc.out")"
    ATTEMPTS="$(cat "$ATTEMPT_FILE")"
}

# A: first attempt succeeds -> green, no retry, never slept.
FAKE_RCS=(0 0)
FAKE_OUTS=("Passed!  1 test" "unused")
run_scenario 600
check "A: first-attempt success is a GREEN job" "$ORC_RC" "0"
check "A: no retry (exactly one attempt)" "$ATTEMPTS" "1"
check "A: never slept" "$SLEEP_CALLS" "0"

# B: record_not_accessible then success -> green, exactly one retry, slept once for the delay, retry warning emitted.
FAKE_RCS=(1 0)
FAKE_OUTS=("$RNA_LINE" "Passed!  1 test")
run_scenario 600
check "B: RNA-then-success is a GREEN job" "$ORC_RC" "0"
check "B: exactly one retry (two attempts)" "$ATTEMPTS" "2"
check "B: slept exactly once" "$SLEEP_CALLS" "1"
check "B: slept for the configured delay" "$SLEEP_LAST" "600"
check_contains "B: emitted a retry ::warning::" "$ORC_OUT" "::warning::"
check_contains "B: the warning names record_not_accessible" "$ORC_OUT" "record_not_accessible"
check_absent "B: no ::error:: on an eventual success" "$ORC_OUT" "::error::"

# C: record_not_accessible twice -> red, exactly one retry, slept once.
FAKE_RCS=(1 1)
FAKE_OUTS=("$RNA_LINE" "$RNA_LINE")
run_scenario 600
check "C: RNA-twice is a RED job" "$ORC_RC" "1"
check "C: exactly one retry (two attempts)" "$ATTEMPTS" "2"
check "C: slept exactly once" "$SLEEP_CALLS" "1"
check_contains "C: emitted an ::error::" "$ORC_OUT" "::error::"

# D: record_not_accessible then a DIFFERENT failure on the retry -> still red (one retry is spent; any second failure alarms).
FAKE_RCS=(1 1)
FAKE_OUTS=("$RNA_LINE" "$OTHER_LINE")
run_scenario 600
check "D: RNA-then-other-failure is a RED job" "$ORC_RC" "1"
check "D: exactly one retry (two attempts)" "$ATTEMPTS" "2"
check "D: slept exactly once" "$SLEEP_CALLS" "1"
check_contains "D: emitted an ::error::" "$ORC_OUT" "::error::"

# E: a non-record_not_accessible failure on attempt 1 -> red IMMEDIATELY, no retry, never slept. The fake's second
# attempt is scripted to PASS to prove the wrapper never reaches it (real drift must fail fast, not be masked).
FAKE_RCS=(1 0)
FAKE_OUTS=("$OTHER_LINE" "Passed!  1 test")
run_scenario 600
check "E: a non-RNA failure is a RED job" "$ORC_RC" "1"
check "E: no retry (exactly one attempt)" "$ATTEMPTS" "1"
check "E: never slept" "$SLEEP_CALLS" "0"
check_contains "E: emitted an ::error::" "$ORC_OUT" "::error::"
check_absent "E: no retry ::warning::" "$ORC_OUT" "::warning::"

echo
echo "passed: ${pass}, failed: ${fail}"
[ "$fail" -eq 0 ]
