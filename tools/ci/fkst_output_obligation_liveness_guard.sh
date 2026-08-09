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

store_dir="$(mktemp -d)"
trap 'rm -rf "$store_dir"' EXIT

obligations_file="$store_dir/output-obligations.log"
effects_file="$store_dir/github-proxy-effects.log"
drained_file="$store_dir/drained-obligations.log"
: >"$obligations_file"
: >"$effects_file"
: >"$drained_file"

extract_attr() {
  local line="$1"
  local attr="$2"
  sed -nE "s/.* ${attr}=\"([^\"]*)\".*/\1/p" <<<"$line"
}

contains_exact_line() {
  local file="$1"
  local line="$2"
  grep -Fxq -- "$line" "$file"
}

append_exact_line_once() {
  local file="$1"
  local line="$2"
  if contains_exact_line "$file" "$line"; then
    return 1
  fi

  printf '%s\n' "$line" >>"$file"
  return 0
}

count_exact_line() {
  local file="$1"
  local line="$2"
  { grep -Fx -- "$line" "$file" || true; } |
    wc -l |
    tr -d ' '
}

state_line="$(grep -F 'fkst:github-devloop:state:v1' "$fixture_file" | head -n 1 || true)"
timeout_line="$(grep -F 'fkst:github-devloop:timeout-reconcile:v1' "$fixture_file" | head -n 1 || true)"

[[ -n "$state_line" ]] || fail "fixture lacks github-devloop state marker"
[[ -n "$timeout_line" ]] || fail "fixture lacks timeout-reconcile marker"
if grep -Fq 'fkst:github-proxy:issue-create:output-obligation/' "$fixture_file"; then
  fail "fixture must contain terminal facts only; the guard must emit the covering issue-create effect"
fi

state="$(extract_attr "$state_line" "state")"
proposal="$(extract_attr "$timeout_line" "proposal")"
version="$(extract_attr "$timeout_line" "version")"
action="$(extract_attr "$timeout_line" "action")"
reason_class="$(extract_attr "$timeout_line" "reason_class")"
source_ref_kind="$(extract_attr "$timeout_line" "source_ref_kind")"
source_ref="$(extract_attr "$timeout_line" "source_ref")"

[[ "$state" == "blocked" ]] || fail "expected blocked state, found '$state'"
[[ "$action" == "drop" ]] || fail "expected timeout reconcile action drop, found '$action'"
[[ "$reason_class" == "state-output-obligation-timeout" ]] ||
  fail "expected state-output-obligation-timeout reason_class, found '$reason_class'"
[[ "$version" == *"/timeout-reconcile/"* ]] ||
  fail "expected timeout-reconcile terminal version, found '$version'"
[[ "$source_ref_kind" == "external" ]] ||
  fail "expected external source_ref_kind, found '$source_ref_kind'"
[[ "$source_ref" == *"#issue/"* ]] ||
  fail "expected issue source_ref, found '$source_ref'"

source_repository="${source_ref%%#*}"
[[ "$source_repository" == */* ]] ||
  fail "expected owner/repository source_ref prefix, found '$source_ref'"

obligation_key="output-obligation/blocked/${source_repository}/${proposal}/${version}/${reason_class}"
effect_marker="fkst:github-proxy:issue-create:${obligation_key}"

produce_output_obligation() {
  append_exact_line_once "$obligations_file" "$obligation_key"
}

reconcile_output_obligations() {
  local emitted_effects=0
  local obligation=""

  while IFS= read -r obligation; do
    [[ -n "$obligation" ]] || continue

    if ! contains_exact_line "$drained_file" "$obligation"; then
      if ! contains_exact_line "$effects_file" "$effect_marker"; then
        append_exact_line_once "$effects_file" "$effect_marker" || true
        emitted_effects=$((emitted_effects + 1))
      fi

      append_exact_line_once "$drained_file" "$obligation" || true
    fi
  done <"$obligations_file"

  printf '%s\n' "$emitted_effects"
}

count_pending_obligations() {
  local pending_obligations=0
  local obligation=""

  while IFS= read -r obligation; do
    [[ -n "$obligation" ]] || continue
    if ! contains_exact_line "$drained_file" "$obligation"; then
      pending_obligations=$((pending_obligations + 1))
    fi
  done <"$obligations_file"

  printf '%s\n' "$pending_obligations"
}

produced_obligations=0
if produce_output_obligation; then
  produced_obligations=$((produced_obligations + 1))
fi
if produce_output_obligation; then
  produced_obligations=$((produced_obligations + 1))
fi

[[ "$produced_obligations" == "1" ]] ||
  fail "expected one durable output obligation after duplicate production, found $produced_obligations"

first_pass_emitted_effects="$(reconcile_output_obligations)"
pending_obligations="$(count_pending_obligations)"
effect_count="$(count_exact_line "$effects_file" "$effect_marker")"

[[ "$first_pass_emitted_effects" == "1" ]] ||
  fail "first reconcile pass emitted $first_pass_emitted_effects effects instead of one"
[[ "$pending_obligations" == "0" ]] ||
  fail "first reconcile pass left $pending_obligations pending state-output-obligation-timeout obligation(s)"
[[ "$effect_count" == "1" ]] ||
  fail "expected exactly one github-proxy issue-create effect for '$effect_marker', found $effect_count"

second_pass_emitted_effects="$(reconcile_output_obligations)"
pending_obligations="$(count_pending_obligations)"
effect_count="$(count_exact_line "$effects_file" "$effect_marker")"

[[ "$second_pass_emitted_effects" == "0" ]] ||
  fail "second reconcile pass emitted $second_pass_emitted_effects duplicate effect(s)"
[[ "$pending_obligations" == "0" ]] ||
  fail "second reconcile pass left $pending_obligations pending state-output-obligation-timeout obligation(s)"
[[ "$effect_count" == "1" ]] ||
  fail "expected idempotent github-proxy issue-create effect count to stay one for '$effect_marker', found $effect_count"

cat <<EOF
produced_obligations=$produced_obligations
first_pass_emitted_effects=$first_pass_emitted_effects
second_pass_emitted_effects=$second_pass_emitted_effects
pending_obligations=$pending_obligations
emitted_effect=$effect_marker
state-output-obligation-timeout obligation fixture drains idempotently.
EOF
