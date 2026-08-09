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

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/../.." && pwd)"
lock_file="$repo_root/fkst.lock"
[[ -f "$lock_file" ]] || fail "fkst.lock not found"

extract_attr() {
  local line="$1"
  local attr="$2"
  sed -nE "s/.* ${attr}=\"([^\"]*)\".*/\1/p" <<<"$line"
}

state_line="$(grep -F 'fkst:github-devloop:state:v1' "$fixture_file" | head -n 1 || true)"
timeout_line="$(grep -F 'fkst:github-devloop:timeout-reconcile:v1' "$fixture_file" | head -n 1 || true)"

[[ -n "$state_line" ]] || fail "fixture lacks github-devloop state marker"
[[ -n "$timeout_line" ]] || fail "fixture lacks timeout-reconcile marker"
if grep -Fq 'fkst:github-proxy:issue-create:output-obligation/' "$fixture_file"; then
  fail "fixture must contain terminal facts only; the guard must verify the real owner path"
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

lock_fields="$(
  python3 -B - "$lock_file" <<'PY'
import sys
import tomllib
from pathlib import Path

data = tomllib.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
for source in data.get("external_source", []):
    if source.get("id") == "fkst-packages":
        git = source.get("git") or ""
        resolved = source.get("resolved") or {}
        intent = source.get("intent") or {}
        rev = resolved.get("rev") or intent.get("rev") or ""
        tree = resolved.get("tree_sha256") or ""
        print("\t".join([git, rev, tree]))
        break
else:
    raise SystemExit("fkst-packages source not found")
PY
)"

IFS=$'\t' read -r lock_git lock_rev lock_tree_sha256 <<<"$lock_fields"
[[ -n "$lock_git" ]] || fail "fkst-packages git URL missing from fkst.lock"
[[ -n "$lock_rev" ]] || fail "fkst-packages resolved rev missing from fkst.lock"
[[ -n "$lock_tree_sha256" ]] || fail "fkst-packages tree_sha256 missing from fkst.lock"

resolve_locked_tree() {
  local cache_root="${FKST_CACHE_ROOT:-${XDG_CACHE_HOME:-$HOME/.cache}/fkst/store}"
  local cached="$cache_root/$lock_tree_sha256"
  if [[ -d "$cached" ]]; then
    printf '%s\n' "$cached"
    return 0
  fi

  if [[ -n "${FKST_PLATFORM_ROOT:-}" && -d "$FKST_PLATFORM_ROOT/.git" ]]; then
    local platform_rev platform_status
    platform_rev="$(git -C "$FKST_PLATFORM_ROOT" rev-parse HEAD 2>/dev/null || true)"
    platform_status="$(git -C "$FKST_PLATFORM_ROOT" status --porcelain 2>/dev/null || true)"
    if [[ "$platform_rev" == "$lock_rev" && -z "$platform_status" ]]; then
      printf '%s\n' "$FKST_PLATFORM_ROOT"
      return 0
    fi
  fi

  fail "locked fkst-packages tree is not materialized: expected $cached for rev $lock_rev"
}

resolve_engine_bin() {
  if [[ -n "${BIN:-}" && -x "$BIN" ]]; then
    printf '%s\n' "$BIN"
    return 0
  fi

  local env_file="${FKST_ENV_FILE:-$HOME/.config/fkst/aevatar.env}"
  if [[ -f "$env_file" ]]; then
    local env_bin
    env_bin="$(sed -nE 's/^BIN=(.*)$/\1/p' "$env_file" | tail -n 1)"
    if [[ -n "$env_bin" && -x "$env_bin" ]]; then
      printf '%s\n' "$env_bin"
      return 0
    fi
  fi

  if command -v fkst-framework >/dev/null 2>&1; then
    command -v fkst-framework
    return 0
  fi

  if [[ -n "${FKST_PLATFORM_ROOT:-}" ]]; then
    local sibling_bin
    sibling_bin="$(cd -- "$FKST_PLATFORM_ROOT/.." 2>/dev/null && pwd)/fkst-substrate/target/debug/fkst-framework"
    if [[ -x "$sibling_bin" ]]; then
      printf '%s\n' "$sibling_bin"
      return 0
    fi
  fi

  fail "fkst-framework BIN is unreachable; set BIN or FKST_ENV_FILE"
}

locked_tree="$(resolve_locked_tree)"
engine_bin="$(resolve_engine_bin)"

[[ -f "$locked_tree/fkst.workspace.toml" ]] ||
  fail "locked fkst-packages tree lacks fkst.workspace.toml: $locked_tree"
[[ -d "$locked_tree/libraries" ]] ||
  fail "locked fkst-packages tree lacks libraries: $locked_tree"
[[ -d "$locked_tree/packages/github-devloop" ]] ||
  fail "locked fkst-packages tree lacks packages/github-devloop: $locked_tree"
[[ -d "$locked_tree/packages/github-proxy" ]] ||
  fail "locked fkst-packages tree lacks packages/github-proxy: $locked_tree"

store_dir="$(mktemp -d)"
trap 'rm -rf "$store_dir"' EXIT

prepare_project() {
  local package_name="$1"
  local selected_test="$2"
  local helper_module_file="$3"
  local wrapper_file="$4"
  local wrapper_body="$5"
  local project_dir="$store_dir/project-$package_name"
  local package_dir="$project_dir/packages/$package_name"

  rm -rf "$project_dir"
  mkdir -p "$project_dir/packages"
  cp "$locked_tree/fkst.workspace.toml" "$project_dir/fkst.workspace.toml"
  cp "$locked_tree/fkst.lock" "$project_dir/fkst.lock"
  cp -R "$locked_tree/libraries" "$project_dir/libraries"
  if [[ -d "$locked_tree/migration" ]]; then
    cp -R "$locked_tree/migration" "$project_dir/migration"
  fi
  cp -R "$locked_tree/packages/$package_name" "$package_dir"
  find "$package_dir/tests" -type f -name '*_test.lua' -delete
  cp "$locked_tree/packages/$package_name/tests/$selected_test" "$package_dir/tests/$helper_module_file"
  printf '%s\n' "$wrapper_body" >"$package_dir/tests/$wrapper_file"

  printf '%s\n' "$project_dir"
}

run_selected_test() {
  local package_name="$1"
  local selected_test="$2"
  local helper_module_file="$3"
  local wrapper_file="$4"
  local expected_test="$5"
  local wrapper_body="$6"
  local project_dir package_dir rt dur report output

  project_dir="$(prepare_project "$package_name" "$selected_test" "$helper_module_file" "$wrapper_file" "$wrapper_body")"
  package_dir="$project_dir/packages/$package_name"
  rt="$(mktemp -d "$store_dir/runtime-$package_name.XXXXXX")"
  dur="$(mktemp -d "$store_dir/durable-$package_name.XXXXXX")"
  report="$rt/report.json"

  if ! output="$(
    cd "$project_dir" &&
      FKST_RUNTIME_ROOT="$rt" FKST_DURABLE_ROOT="$dur" "$engine_bin" test \
        --project-root "$project_dir" \
        --package-root "$package_dir" \
        --report-json "$report"
  )"; then
    printf '%s\n' "$output" >&2
    fail "locked FKST package test failed: $package_name/$selected_test"
  fi

  printf '%s\n' "$output"
  python3 -B - "$report" "$wrapper_file" "$expected_test" <<'PY'
import json
import sys
from pathlib import Path

report = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
expected_file = "tests/" + sys.argv[2]
expected_test = sys.argv[3]
summary = report.get("summary") or {}
if report.get("schema") != "fkst.test.report.v1":
    raise SystemExit("bad FKST test report schema")
if int(summary.get("failed", 0)) != 0:
    raise SystemExit("FKST test report contains failures")
for test in report.get("tests", []):
    if (
        test.get("file") == expected_file
        and test.get("name") == expected_test
        and test.get("status") == "pass"
    ):
        break
else:
    raise SystemExit(f"expected passing test not found: {expected_file}::{expected_test}")
PY
}

owner_wrapper='local suite = require("tests.restart_timeout_obligations_owner_path")
return {
  test_timeout_obligations_link_expected_decisions_and_payloads_to_frozen_parity = suite.test_timeout_obligations_link_expected_decisions_and_payloads_to_frozen_parity,
}'

reconciler_wrapper='local suite = require("tests.timeout_reconcile_cas_parity_owner_path")
return {
  test_timeout_reconcile_source_is_pre_cas_no_longer_over_budget = suite.test_timeout_reconcile_source_is_pre_cas_no_longer_over_budget,
}'

effect_wrapper='local suite = require("tests.integration_issue_create_owner_path")
return {
  test_issue_create_parent_ledger_marker_skips_create = suite.test_issue_create_request_parent_ledger_marker_skips_create,
  test_issue_create_second_delivery_same_dedup_skips_create = suite.test_issue_create_request_second_delivery_same_dedup_skips_create,
}'

run_selected_test \
  "github-devloop" \
  "restart_timeout_obligations_test.lua" \
  "restart_timeout_obligations_owner_path.lua" \
  "selected_timeout_obligations_test.lua" \
  "test_timeout_obligations_link_expected_decisions_and_payloads_to_frozen_parity" \
  "$owner_wrapper"

run_selected_test \
  "github-devloop" \
  "timeout_reconcile_cas_parity_test.lua" \
  "timeout_reconcile_cas_parity_owner_path.lua" \
  "selected_timeout_reconcile_test.lua" \
  "test_timeout_reconcile_source_is_pre_cas_no_longer_over_budget" \
  "$reconciler_wrapper"

run_selected_test \
  "github-proxy" \
  "integration_issue_create_test.lua" \
  "integration_issue_create_owner_path.lua" \
  "selected_issue_create_test.lua" \
  "test_issue_create_second_delivery_same_dedup_skips_create" \
  "$effect_wrapper"

obligation_key="output-obligation/blocked/${source_repository}/${proposal}/${version}/${reason_class}"

cat <<EOF
locked_fkst_package_tree=$locked_tree
locked_fkst_package_rev=$lock_rev
output_obligation=$obligation_key
verified_owner_package=github-devloop
verified_owner_test=restart_timeout_obligations_test
verified_reconciler_test=timeout_reconcile_cas_parity_test
verified_effect_package=github-proxy
verified_effect_test=integration_issue_create_test
state-output-obligation-timeout obligation owner path verified.
EOF
