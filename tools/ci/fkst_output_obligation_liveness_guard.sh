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
from_state="$(extract_attr "$timeout_line" "from_state")"
from_version="$(extract_attr "$timeout_line" "from_version")"
age_minutes="$(extract_attr "$timeout_line" "age_minutes")"
budget_minutes="$(extract_attr "$timeout_line" "budget_minutes")"
attempt="$(extract_attr "$timeout_line" "attempt")"
attempt_limit="$(extract_attr "$timeout_line" "attempt_limit")"
driving_queue="$(extract_attr "$timeout_line" "driving_queue")"

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
[[ -n "$from_state" ]] || fail "fixture timeout-reconcile marker lacks from_state"
[[ -n "$from_version" ]] || fail "fixture timeout-reconcile marker lacks from_version"
[[ "$from_state" == "impl-failed" ]] ||
  fail "expected incident predecessor from_state impl-failed, found '$from_state'"
[[ "$attempt" =~ ^[0-9]+$ ]] || fail "expected numeric attempt, found '$attempt'"
[[ "$attempt_limit" =~ ^[0-9]+$ ]] || fail "expected numeric attempt_limit, found '$attempt_limit'"
[[ "$age_minutes" =~ ^[0-9]+$ ]] || fail "expected numeric age_minutes, found '$age_minutes'"
[[ "$budget_minutes" =~ ^[0-9]+$ ]] || fail "expected numeric budget_minutes, found '$budget_minutes'"
[[ "$attempt" == "$attempt_limit" ]] ||
  fail "expected timeout attempt to equal attempt_limit, found attempt=$attempt limit=$attempt_limit"
(( 10#$age_minutes >= 10#$budget_minutes )) ||
  fail "expected age_minutes to meet or exceed budget_minutes, found age=$age_minutes budget=$budget_minutes"
[[ "$driving_queue" == "devloop_ready" ]] ||
  fail "expected incident driving_queue devloop_ready, found '$driving_queue'"
[[ "$version" == "$from_version/timeout-reconcile/$from_state/$attempt" ]] ||
  fail "terminal version is not derived from from_version/from_state/attempt"

source_repository="${source_ref%%#*}"
[[ "$source_repository" == */* ]] ||
  fail "expected owner/repository source_ref prefix, found '$source_ref'"

export FKST_INCIDENT_STATE="$state"
export FKST_INCIDENT_PROPOSAL="$proposal"
export FKST_INCIDENT_TERMINAL_VERSION="$version"
export FKST_INCIDENT_REASON_CLASS="$reason_class"
export FKST_INCIDENT_SOURCE_REF="$source_ref"
export FKST_INCIDENT_SOURCE_REPOSITORY="$source_repository"
export FKST_INCIDENT_FROM_STATE="$from_state"
export FKST_INCIDENT_FROM_VERSION="$from_version"
export FKST_INCIDENT_AGE_MINUTES="$age_minutes"
export FKST_INCIDENT_BUDGET_MINUTES="$budget_minutes"
export FKST_INCIDENT_ATTEMPT="$attempt"
export FKST_INCIDENT_ATTEMPT_LIMIT="$attempt_limit"
export FKST_INCIDENT_DRIVING_QUEUE="$driving_queue"

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

source_wrapper='local suite = require("tests.liveness_timeout_attempt_owner_path")
local base_ids = require("devloop.base_ids")
local conv_attempts = require("devloop.convergence.attempts")
local devloop_base = require("devloop.base")
local devloop_logging = require("devloop.logging")
local entity_lib = require("devloop.entity")
local entity_read_mocks = require("tests.entity_read_mock_helpers")
local reconcile_department = require("departments.reconcile.main")
local h = require("tests.devloop_helpers")

local t = h.t
local core = h.core
local opts = h.opts

local function required_env(name)
  local value = os.getenv(name)
  assert(value, name)
  return value
end

local incident = {
  state = required_env("FKST_INCIDENT_STATE"),
  proposal = required_env("FKST_INCIDENT_PROPOSAL"),
  terminal_version = required_env("FKST_INCIDENT_TERMINAL_VERSION"),
  reason_class = required_env("FKST_INCIDENT_REASON_CLASS"),
  source_ref = required_env("FKST_INCIDENT_SOURCE_REF"),
  source_repository = required_env("FKST_INCIDENT_SOURCE_REPOSITORY"),
  from_state = required_env("FKST_INCIDENT_FROM_STATE"),
  from_version = required_env("FKST_INCIDENT_FROM_VERSION"),
  age_minutes = tonumber(required_env("FKST_INCIDENT_AGE_MINUTES")),
  budget_minutes = tonumber(required_env("FKST_INCIDENT_BUDGET_MINUTES")),
  attempt = tonumber(required_env("FKST_INCIDENT_ATTEMPT")),
  attempt_limit = tonumber(required_env("FKST_INCIDENT_ATTEMPT_LIMIT")),
  driving_queue = required_env("FKST_INCIDENT_DRIVING_QUEUE"),
}

local repo, issue_number_text = base_ids.parse_proposal_id(incident.proposal)
local issue_number = tonumber(issue_number_text)
local source_ref = entity_lib.issue_source_ref(repo, issue_number_text)

local function assert_incident_shape()
  t.eq(incident.state, "blocked", "incident terminal state")
  t.eq(incident.from_state, "impl-failed", "incident predecessor state")
  t.eq(incident.reason_class, "state-output-obligation-timeout", "incident reason class")
  t.eq(incident.driving_queue, "devloop_ready", "incident driving queue")
  t.eq(incident.source_repository, repo, "incident source repository")
  t.eq(incident.source_ref, source_ref.ref, "incident source ref")
  t.eq(incident.terminal_version,
    incident.from_version .. "/timeout-reconcile/" .. incident.from_state .. "/" .. tostring(incident.attempt),
    "incident terminal version derives from predecessor")
  t.eq(incident.attempt, incident.attempt_limit, "incident attempt reached limit")
  t.is_true(incident.age_minutes >= incident.budget_minutes, "incident exceeded output-obligation budget")
  t.is_true(issue_number ~= nil, "incident issue number parses")
end

local function encode_json_string(value)
  return h.encode_json_string(value)
end

local function mock_repo()
  t.mock_command(devloop_base.read_env_command("FKST_GITHUB_REPO"), {
    stdout = repo,
    stderr = "",
    exit_code = 0,
  })
end

local function mock_issue_list(updated_at)
  t.mock_command(core.gh_issue_list_observe_cmd(repo), {
    stdout = "[{\"number\":" .. tostring(issue_number) .. ",\"state\":\"open\",\"updated_at\":\""
      .. encode_json_string(updated_at or "2026-06-03T01:02:03Z") .. "\"}]\n",
    stderr = "",
    exit_code = 0,
  })
end

local function mock_empty_pr_list()
  t.mock_command(core.gh_pr_list_observe_cmd(repo), {
    stdout = "[]\n",
    stderr = "",
    exit_code = 0,
  })
end

local function issue_comment(body, created_at)
  return {
    body = body,
    author_login = "fkst-test-bot",
    created_at = created_at or "2026-06-03T00:00:00Z",
  }
end

local function state_comment(state_name, state_version, created_at)
  return issue_comment(core.state_marker(incident.proposal, state_name, state_version), created_at)
end

local function mock_issue_state(labels, comments, updated_at)
  entity_read_mocks.mock_issue_read_forms(t, {
    repo = repo,
    number = issue_number,
    title = "Incident issue",
    body = "",
    state = "OPEN",
    updated_at = updated_at or "2026-06-03T01:02:03Z",
    labels = labels,
    comments = comments,
    assignees = { "fkst-test-bot" },
    times = 1,
  })
  entity_read_mocks.mock_issue_view_selector(t, {
    repo = repo,
    number = issue_number,
    title = "Incident issue",
    body = "",
    state = "OPEN",
    updated_at = updated_at or "2026-06-03T01:02:03Z",
    labels = labels,
    comments = comments,
    assignees = { "fkst-test-bot" },
    author_login = "fkst-test-bot",
  }, "title,updatedAt,labels,comments,state,author", 1)
end

local function run_liveness_scan(name)
  return h.run_department("departments/liveness_scan/main.lua", {
    queue = "devloop_liveness_tick",
    payload = { schema = "github-devloop.tick.v1" },
    ts = "2026-06-03T01:32:03Z",
  }, opts(name))
end

local function find_raise(result, queue)
  return h.find_raise(result.raises, queue)
end

local function incident_timeout_attempt_comments(state_name, state_version, include_impl_failure)
  local comments = {
    state_comment(state_name, state_version, "2026-06-01T00:00:00Z"),
  }
  if include_impl_failure then
    table.insert(comments, issue_comment(core.impl_failure_marker(
      incident.proposal,
      state_version,
      "codex-failed",
      core._max_impl_auto_retry_attempts
    )))
  end
  for round = 1, incident.attempt - 1 do
    table.insert(comments, issue_comment(conv_attempts.timeout_attempt_marker(
      incident.proposal,
      state_version,
      state_name,
      round,
      source_ref
    )))
  end
  return comments
end

local function capture_cas(run)
  local decisions = {}
  local original = devloop_logging.log_cas_decision
  devloop_logging.log_cas_decision = function(dept, proposal_id, current, from_state, to_state, outcome, reason)
    table.insert(decisions, {
      dept = dept,
      proposal_id = proposal_id,
      current = current,
      from_state = from_state,
      to_state = to_state,
      outcome = outcome,
      reason = reason,
    })
    return original(dept, proposal_id, current, from_state, to_state, outcome, reason)
  end
  local ok, result = pcall(run)
  devloop_logging.log_cas_decision = original
  if not ok then
    error(result, 0)
  end
  return result, decisions
end

local function timeout_reconcile_event()
  return {
    queue = "devloop_timeout_reconcile",
    payload = {
      schema = "github-devloop.timeout-reconcile.v1",
      proposal_id = incident.proposal,
      state = incident.from_state,
      issue_version = incident.from_version,
      round = incident.attempt,
      dedup_key = "timeout-reconcile:" .. incident.terminal_version,
      source_ref = source_ref,
    },
  }
end

local function run_reconcile_event(event)
  local raises = {}
  local original_raise = raise
  raise = function(queue, payload)
    table.insert(raises, { queue = queue, payload = payload })
  end
  local ok, failure = pcall(reconcile_department.pipeline, event)
  raise = original_raise
  return {
    exit_code = ok and 0 or 1,
    error = ok and nil or tostring(failure),
    raises = raises,
  }
end

local function assert_no_terminal_reconcile_raise(result)
  t.eq(find_raise(result, "devloop_timeout_reconcile"), nil)
  t.eq(find_raise(result, "devloop_ready"), nil)
end

return {
  test_incident_impl_failed_timeout_source_is_redriven_from_fixture = function()
    assert_incident_shape()
    local comments = incident_timeout_attempt_comments(incident.from_state, incident.from_version, true)
    mock_repo()
    mock_issue_list("2026-06-03T01:02:03Z")
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:impl-failed" }, comments, "2026-06-03T01:02:03Z")
    mock_empty_pr_list()

    local result = run_liveness_scan("incident-impl-failed-timeout-source")
    t.eq(result.exit_code, 0)
    assert_no_terminal_reconcile_raise(result)
    local attempt = find_raise(result, "github-proxy.github_issue_comment_request")
    t.is_true(attempt ~= nil, "incident impl-failed timeout attempt is emitted")
    t.is_true(attempt.payload.body:find(conv_attempts.timeout_attempt_marker(
      incident.proposal,
      incident.from_version,
      incident.from_state,
      incident.attempt,
      source_ref
    ), 1, true) ~= nil)
  end,

  test_incident_impl_failed_timeout_reconcile_skips_stale_terminal_drop = function()
    assert_incident_shape()
    local comments = incident_timeout_attempt_comments(incident.from_state, incident.from_version, true)
    h.mock_bot_env()
    mock_repo()
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:impl-failed" }, comments, "2026-06-03T01:02:03Z")

    local result, decisions = capture_cas(function()
      return run_reconcile_event(timeout_reconcile_event())
    end)

    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 0)
    local matched = false
    for _, decision in ipairs(decisions) do
      if decision.dept == "reconcile"
        and decision.proposal_id == incident.proposal
        and decision.from_state == incident.from_state
        and decision.to_state == "blocked"
        and decision.outcome == "skip-stale(no-longer-over-budget)" then
        matched = true
      end
    end
    t.is_true(matched, "incident timeout reconciler consumes impl-failed terminal shape")
  end,

  test_incident_blocked_output_obligation_drains_once_from_fixture = function()
    assert_incident_shape()
    local comments = incident_timeout_attempt_comments("blocked", incident.terminal_version, false)
    mock_repo()
    mock_issue_list("2026-06-03T01:03:03Z")
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:blocked" }, comments, "2026-06-03T01:03:03Z")
    mock_empty_pr_list()

    local first = run_liveness_scan("incident-blocked-output-obligation")
    t.eq(first.exit_code, 0)
    t.eq(find_raise(first, "github-devloop-decompose.devloop_decompose"), nil)
    t.eq(find_raise(first, "devloop_timeout_reconcile"), nil)
    local exhausted = find_raise(first, "github-proxy.github_issue_comment_request")
    t.is_true(exhausted ~= nil, "incident blocked output obligation emits terminal stop")
    local exhausted_marker = conv_attempts.decompose_exhausted_marker(
      incident.proposal,
      incident.terminal_version,
      incident.attempt,
      source_ref
    )
    t.is_true(exhausted.payload.body:find(exhausted_marker, 1, true) ~= nil)

    local exhausted_body = conv_attempts.build_decompose_exhausted_comment_request({
      kind = "issue",
      repo = repo,
      number = issue_number,
    }, incident.proposal, {
      state = "blocked",
      version = incident.terminal_version,
    }, source_ref, incident.attempt).body
    table.insert(comments, issue_comment(exhausted_body))
    mock_repo()
    mock_issue_list("2026-06-03T01:04:03Z")
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:blocked" }, comments, "2026-06-03T01:04:03Z")
    mock_empty_pr_list()

    local second = run_liveness_scan("incident-blocked-output-obligation-second")
    t.eq(second.exit_code, 0)
    t.eq(#second.raises, 0)
  end,

  test_impl_failed_retry_limit_replay_decline_climbs_to_timeout_reconcile_without_seeded_timeout_markers =
    suite.test_impl_failed_retry_limit_replay_decline_climbs_to_timeout_reconcile_without_seeded_timeout_markers,
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
  "liveness_timeout_attempt_issue_test.lua" \
  "liveness_timeout_attempt_owner_path.lua" \
  "selected_incident_timeout_source_test.lua" \
  "test_incident_impl_failed_timeout_source_is_redriven_from_fixture" \
  "$source_wrapper"

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
verified_from_state=$from_state
verified_from_version=$from_version
verified_terminal_version=$version
verified_source_test=liveness_timeout_attempt_issue_test
verified_source_case=test_impl_failed_retry_limit_replay_decline_climbs_to_timeout_reconcile_without_seeded_timeout_markers
verified_incident_source_case=test_incident_impl_failed_timeout_source_is_redriven_from_fixture
verified_incident_reconciler_case=test_incident_impl_failed_timeout_reconcile_skips_stale_terminal_drop
verified_incident_terminal_case=test_incident_blocked_output_obligation_drains_once_from_fixture
verified_reconciler_test=timeout_reconcile_cas_parity_test
verified_effect_package=github-proxy
verified_effect_test=integration_issue_create_test
state-output-obligation-timeout obligation owner path verified.
EOF
