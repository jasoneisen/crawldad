#!/usr/bin/env bash
#
# Live-canary retry-once wrapper (issue #92).
#
# The nightly live canary (.github/workflows/canary.yml, issue #7) scrapes ONE real record from the LIVE Accela
# portal. Its cron fires 07:17 UTC ~= 1:30am agency-local — inside Accela's overnight maintenance / app-pool-recycle
# window, when CapDetail briefly redirects to Error.aspx and the payload's CapDetail guard raises a TERMINAL
# `record_not_accessible`. That is a portal maintenance-window false alarm, not drift: a single-shot canary trips on
# it and pages a false drift alert (it did on 2026-08-18, after 11+ green nightlies).
#
# This wrapper retries the canary exactly ONCE, narrowly scoped to that one failure mode:
#   * attempt 1 succeeds                                        -> green, no retry
#   * attempt 1 fails, emitted failure payload is ONLY a transient record_not_accessible
#                                                               -> ::warning::, sleep ~10m, ONE more attempt
#       * attempt 2 succeeds                                    -> green (attempt 1 was a maintenance-window false alarm)
#       * attempt 2 fails (record_not_accessible or anything)   -> red (real drift alert)
#   * attempt 1 fails with ANY other failure class              -> red immediately, NO retry (real drift fails fast)
#
# The retry lives here + in the workflow, NOT in product code, and preserves the live workflow's concurrency-1 (a
# queued second canary still waits behind this one, including through the retry sleep). The decision and orchestration
# are pure/injectable so eng/canary-retry.selftest.sh unit-tests them with a scripted fake canary and a recording
# no-op sleep — no .NET, no live traffic, no real waiting (mirrors connector/selftest.sh).
set -euo pipefail

# The stable failure slug (Contracts RunFailureDetail.Code) the CapDetail guard raises when the record redirects to
# Error.aspx during the maintenance window. The canary test emits the full failure payload — both via
# ITestOutputHelper AND in its `status.ShouldBe("succeeded", ...)` assertion message — so this exact token appears in
# the captured `dotnet test` output on precisely this failure, and on no other (only the code that actually fired is
# emitted). See tests/Crawldad.Tests/Integration/LiveCanaryTests.cs.
CANARY_RETRYABLE_CODE="record_not_accessible"

# canary_retry_verdict <exit_code> <attempt_log_file>  ->  prints one of: pass | retry | fail
#   pass   the attempt succeeded (exit 0)
#   retry  the attempt failed AND its captured output carries the transient record_not_accessible payload
#   fail   the attempt failed with any other output (a real drift signal) — alarm now, do NOT retry
canary_retry_verdict() {
    local code="$1" log="$2"
    if [ "$code" -eq 0 ]; then
        printf 'pass'
        return 0
    fi
    if grep -qF -- "$CANARY_RETRYABLE_CODE" "$log" 2>/dev/null; then
        printf 'retry'
    else
        printf 'fail'
    fi
    return 0
}

# run_canary_attempt <log_file> <cmd...>
# Runs one canary attempt, mirroring its combined stdout+stderr to the workflow log (so a human sees it live) AND
# capturing it to <log_file> for the verdict grep. Returns the CANARY's own exit code, never tee's.
run_canary_attempt() {
    local log="$1"
    shift
    "$@" 2>&1 | tee "$log"
    return "${PIPESTATUS[0]}"
}

# run_canary_with_retry <delay_seconds> <cmd...>
# The retry-once orchestration. Returns 0 for a green job, 1 for a red one. Sleeps via ${CANARY_SLEEP_CMD:-sleep} so
# the selftest can inject a recording no-op instead of really waiting ~10 minutes.
run_canary_with_retry() {
    local delay="$1"
    shift
    local sleep_cmd="${CANARY_SLEEP_CMD:-sleep}"
    local log1 log2 rc verdict

    log1="$(mktemp)"
    if run_canary_attempt "$log1" "$@"; then rc=0; else rc=$?; fi
    verdict="$(canary_retry_verdict "$rc" "$log1")"

    case "$verdict" in
        pass)
            echo "Live canary passed on the first attempt."
            return 0
            ;;
        fail)
            echo "::error::Live canary failed with a non-retryable failure (not a transient ${CANARY_RETRYABLE_CODE}) — real drift, alarming now (issue #92)."
            return 1
            ;;
    esac

    # verdict == retry: a transient record_not_accessible, almost certainly the portal maintenance window. Retry once.
    echo "::warning::Live canary hit a transient ${CANARY_RETRYABLE_CODE} (likely the Accela overnight maintenance window); retrying once in ${delay}s before alarming (issue #92)."
    "$sleep_cmd" "$delay"

    log2="$(mktemp)"
    if run_canary_attempt "$log2" "$@"; then
        echo "::warning::Live canary passed on the second attempt; the first ${CANARY_RETRYABLE_CODE} was a maintenance-window false alarm (issue #92)."
        return 0
    fi

    echo "::error::Live canary failed on the retry too (a second ${CANARY_RETRYABLE_CODE}, or a new failure) — this is a real drift alert (issue #92)."
    return 1
}

# Run only when executed directly (from canary.yml); when sourced (by selftest.sh) the functions are exposed without
# launching the real canary. Direct runs execute the REAL canary with the real ~10-minute delay from the repo root.
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
    cd "$REPO_ROOT"
    run_canary_with_retry "${CANARY_RETRY_DELAY_SECONDS:-600}" \
        dotnet test Crawldad.slnx -c Debug --filter "Category=LiveCanary" /p:CollectCoverage=false
fi
