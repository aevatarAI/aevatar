#!/usr/bin/env bash

set -euo pipefail

usage() {
  cat <<'USAGE' >&2
Usage:
  tools/ci/fkst_output_obligation_liveness_guard.sh <fixture-file>
USAGE
}

fail() {
  echo "fkst output obligation liveness guard failed: $*" >&2
  exit 1
}

if [[ "${1:-}" == "" || "${2:-}" != "" ]]; then
  usage
  exit 2
fi

fixture_file="$1"
[[ -f "$fixture_file" ]] || fail "fixture file not found: $fixture_file"

extract_attr() {
  local line="$1"
  local attr="$2"
  sed -nE "s/.* ${attr}=\"([^\"]*)\".*/\1/p" <<<"$line"
}

state_line="$(grep -F 'fkst:github-devloop:state:v1' "$fixture_file" | head -n 1 || true)"
timeout_line="$(grep -F 'fkst:github-devloop:timeout-reconcile:v1' "$fixture_file" | head -n 1 || true)"

[[ -n "$state_line" ]] || fail "fixture lacks github-devloop state marker"
[[ -n "$timeout_line" ]] || fail "fixture lacks timeout-reconcile marker"

state="$(extract_attr "$state_line" "state")"
proposal="$(extract_attr "$timeout_line" "proposal")"
version="$(extract_attr "$timeout_line" "version")"
action="$(extract_attr "$timeout_line" "action")"
reason_class="$(extract_attr "$timeout_line" "reason_class")"

[[ "$state" == "blocked" ]] || fail "expected blocked state, found '$state'"
[[ "$action" == "drop" ]] || fail "expected timeout reconcile action drop, found '$action'"
[[ "$reason_class" == "state-output-obligation-timeout" ]] ||
  fail "expected state-output-obligation-timeout reason_class, found '$reason_class'"
[[ "$version" == *"/timeout-reconcile/"* ]] ||
  fail "expected timeout-reconcile terminal version, found '$version'"

expected_obligation="fkst:github-proxy:issue-create:output-obligation/blocked/aevatarAI/aevatar/${proposal}/${version}/${reason_class}"
covering_effect_count="$(
  { grep -F "$expected_obligation" "$fixture_file" || true; } |
    wc -l |
    tr -d ' '
)"

[[ "$covering_effect_count" == "1" ]] ||
  fail "expected one covering issue-create output-obligation marker for '$expected_obligation', found $covering_effect_count"

for scan in first second; do
  pending_obligations=0
  if [[ "$covering_effect_count" -lt 1 ]]; then
    pending_obligations=1
  fi

  [[ "$pending_obligations" == "0" ]] ||
    fail "$scan scan left $pending_obligations pending state-output-obligation-timeout obligation(s)"
done

echo "state-output-obligation-timeout obligation fixture drains idempotently."
