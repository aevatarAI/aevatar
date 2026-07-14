local S = {}
local check_runs = require("forge.github.check_runs")
local github_adapter = require("forge.github")
local forge_validators = require("forge.gitref")
local strings = require("contract.strings")

local merge_gate_reason_class_entries = {
  {
    key = "merge-state-unstable-with-failing-checks",
    row = require("forge.merge.reason_classes.merge_state_unstable_with_failing_checks"),
  },
  {
    key = "mergeable-conflicting",
    row = require("forge.merge.reason_classes.mergeable_conflicting"),
  },
  {
    key = "own-ci-red",
    row = require("forge.merge.reason_classes.own_ci_red"),
  },
  {
    key = "rollup-red",
    row = require("forge.merge.reason_classes.rollup_red"),
  },
}

function S.install(M)
local github = github_adapter.production_handle
local is_open_pr = check_runs.is_open_pr
local check_run_id = check_runs.check_run_id
local check_run_head_sha = check_runs.check_run_head_sha
local check_run_name = check_runs.check_run_name
local check_run_state = check_runs.check_run_state
local parse_commit_check_runs = check_runs.parse_commit_check_runs
local required_check_run_names = check_runs.required_check_run_names

local function log_check_runs_fallback(M, opts, repo, head_sha, runs, reason)
  if type(M.log_line) ~= "function" then
    return
  end
  M.log_line("info", tostring(opts and opts.dept or "merge"), tostring(opts and opts.proposal_id or "merge-gate"), "CI_FALLBACK", {
    "repo=" .. tostring(repo),
    "head_sha=" .. tostring(head_sha),
    "source=commit-check-runs",
    "required_checks=" .. table.concat(required_check_run_names or {}, ","),
    "check_runs=" .. tostring(type(runs) == "table" and #runs or 0),
    "reason=" .. tostring(reason or ""),
  })
end

local function fetch_commit_check_runs(repo, head_sha)
  if tostring(repo or "") == "" or not forge_validators.is_git_sha(head_sha) then
    return nil, "ci-unknown"
  end
  local result = github("forge.merge").gh_commit_check_runs(repo, head_sha, 30)
  if result.exit_code ~= 0 then
    return nil, "ci-unknown"
  end
  return parse_commit_check_runs(result.stdout), nil
end

local function required_head_check_run_status(runs, head_sha)
  return check_runs.required_head_check_run_status(runs, head_sha, required_check_run_names)
end

local function ci_classification(kind, reason, extra)
  local result = extra or {}
  result.kind = kind
  result.reason = reason
  result.merge_blocking = kind ~= "OK"
  result.actionable = kind == "OWN_CI_RED"
  return result
end

local function integration_or_external_red(pr, head_sha, runs)
  local gate_sha = M.rollup_failure_gate_sha(pr)
  if gate_sha ~= nil and tostring(gate_sha):lower() ~= tostring(head_sha):lower() then
    return ci_classification("INTEGRATION_RED", "integration-ci-red", { check_runs = runs })
  end
  return ci_classification("EXTERNAL_CI_RED", "external-ci-red", { check_runs = runs })
end

local function build_reason_class_map(entries)
  local map = {}
  for _, entry in ipairs(entries) do
    local key = entry.key
    local row = entry.row
    if type(key) ~= "string" or key == "" then
      error("forge.merge: reason class key must be a non-empty string")
    end
    if type(row) ~= "table" then
      error("forge.merge: reason class row must be a table: " .. key)
    end
    if row.reason ~= key then
      error("forge.merge: reason class key " .. key .. " does not match row reason " .. tostring(row.reason))
    end
    if map[key] ~= nil then
      error("forge.merge: duplicate reason class key " .. key)
    end
    map[key] = {
      class = row.class,
      requires_pr_merge_product = row.requires_pr_merge_product,
    }
  end
  return map
end

local merge_gate_reason_classes = build_reason_class_map(merge_gate_reason_class_entries)

local function merge_gate_reason_row(reason)
  local text = tostring(reason or "")
  if text:find("^rollup%-red:", 1) ~= nil then
    return merge_gate_reason_classes["rollup-red"]
  end
  return merge_gate_reason_classes[text]
end

local function merge_attempt_limit(request)
  local attempts = tonumber(request and request.match_head_retry_attempts or 1) or 1
  attempts = math.floor(attempts)
  if attempts < 1 then
    return 1
  end
  return attempts
end

local function expected_pr_identity(request, repo, head_sha)
  return {
    repo = repo,
    head_sha = head_sha,
    head_branch = request and request.head_branch,
    base_branch = request and request.base_branch,
  }
end

return {
  strings = strings,
  is_open_pr = is_open_pr,
  log_check_runs_fallback = log_check_runs_fallback,
  fetch_commit_check_runs = fetch_commit_check_runs,
  check_run_id = check_run_id,
  check_run_head_sha = check_run_head_sha,
  check_run_name = check_run_name,
  check_run_state = check_run_state,
  parse_commit_check_runs = parse_commit_check_runs,
  required_check_run_names = required_check_run_names,
  required_head_check_run_status = required_head_check_run_status,
  ci_classification = ci_classification,
  integration_or_external_red = integration_or_external_red,
  merge_gate_reason_classes = merge_gate_reason_classes,
  merge_gate_reason_row = merge_gate_reason_row,
  merge_attempt_limit = merge_attempt_limit,
  expected_pr_identity = expected_pr_identity,
}
end

function S.is_same_repo_pr_head(pr, repo)
  if type(pr) ~= "table" then
    return false
  end
  if pr.is_cross_repository == true then
    return false
  end
  if pr.head_repository == nil then
    return false
  end
  return tostring(pr.head_repository):lower() == tostring(repo):lower()
end

return S
