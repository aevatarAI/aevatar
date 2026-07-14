local S = {}
local github_adapter = require("forge.github")
local check_runs = require("forge.github.check_runs")
local forge_validators = require("forge.gitref")

function S.install(M, shared)
local github = github_adapter.production_handle
local strings = shared.strings
local is_open_pr = shared.is_open_pr
local log_check_runs_fallback = shared.log_check_runs_fallback
local fetch_commit_check_runs = shared.fetch_commit_check_runs
local check_run_id = shared.check_run_id
local check_run_head_sha = shared.check_run_head_sha
local parse_commit_check_runs = shared.parse_commit_check_runs
local commit_check_runs_green = check_runs.commit_check_runs_green
local pr_rollup_green = check_runs.pr_rollup_green
local pr_mergeable = check_runs.pr_mergeable
local is_not_mergeable_reason = check_runs.is_not_mergeable_reason
local required_head_check_run_status = shared.required_head_check_run_status
local ci_classification = shared.ci_classification
local integration_or_external_red = shared.integration_or_external_red
local merge_gate_reason_row = shared.merge_gate_reason_row

local function pr_identity_matches(pr, expected)
  if type(pr) ~= "table" then
    return false, "missing-pr"
  end
  if not is_open_pr(pr) then
    return false, "pr-not-open"
  end
  if tostring(pr.head_sha or "") ~= tostring(expected and expected.head_sha or "") then
    return false, "head-sha-mismatch"
  end
  if tostring(pr.head_ref_name or "") ~= tostring(expected and expected.head_branch or "") then
    return false, "head-branch-mismatch"
  end
  if tostring(pr.base_ref_name or "") ~= tostring(expected and expected.base_branch or "") then
    return false, "base-branch-mismatch"
  end
  if not require("forge.merge.shared").is_same_repo_pr_head(pr, expected and expected.repo) then
    return false, "foreign-head-repository"
  end
  return true, "pr-ok"
end

local function commit_check_runs_merge_gate(repo, head_sha, opts)
  local result = github("forge.merge").gh_commit_check_runs(repo, head_sha, 30)
  if result.exit_code ~= 0 then
    error("forge.merge: gh commit check-runs failed: " .. tostring(result.stderr))
  end
  local runs = parse_commit_check_runs(result.stdout)
  local green, reason = commit_check_runs_green(runs)
  log_check_runs_fallback(M, opts, repo, head_sha, runs, reason)
  return green, reason, runs
end

local function classify_pr_ci_gate(pr, opts)
  local green, reason = pr_rollup_green(pr)
  if green then
    return ci_classification("OK", "rollup-green")
  end
  if reason == "rollup-pending" then
    return ci_classification("CHECKS_PENDING", "checks-pending")
  end
  local repo = opts and opts.repo or nil
  local head_sha = tostring(pr and pr.head_sha or "")
  if not forge_validators.is_git_sha(head_sha) then
    return ci_classification("CI_UNKNOWN", "ci-unknown")
  end
  if tostring(repo or "") == "" then
    return ci_classification("CI_UNKNOWN", "ci-unknown")
  end
  local runs, fetch_reason = fetch_commit_check_runs(repo, head_sha)
  if runs == nil then
    return ci_classification("CI_UNKNOWN", fetch_reason or "ci-unknown")
  end
  log_check_runs_fallback(M, opts, repo, head_sha, runs, reason)
  local head_status = required_head_check_run_status(runs, head_sha)
  if head_status == "red" then
    return ci_classification("OWN_CI_RED", "own-ci-red", { check_runs = runs })
  end
  if head_status == "pending" then
    return ci_classification("CHECKS_PENDING", "checks-pending", { check_runs = runs })
  end
  if head_status == "unknown" then
    return ci_classification("CI_UNKNOWN", "ci-unknown", { check_runs = runs })
  end
  if reason == "rollup-red" then
    return integration_or_external_red(pr, head_sha, runs)
  end
  return ci_classification("OK", "rollup-green", { check_runs = runs })
end

local function rerunnable_check_run_ids_for_head(runs, head_sha)
  if type(runs) ~= "table" or not forge_validators.is_git_sha(head_sha) then
    return {}
  end
  local ids = {}
  local seen = {}
  local expected = tostring(head_sha):lower()
  for _, run in ipairs(runs) do
    local id = check_run_id(run)
    local run_head = check_run_head_sha(run)
    if id ~= nil
      and (run_head == nil or run_head == expected)
      and not seen[id] then
      table.insert(ids, id)
      seen[id] = true
    end
  end
  return ids
end

local function evaluate_ci_status_gate(pr, opts)
  local green, green_reason = pr_rollup_green(pr)
  local check_runs = nil
  if not green and green_reason == "missing-status-rollup" and type(opts) == "table" and opts.repo ~= nil then
    local head_sha = tostring(pr and pr.head_sha or "")
    if head_sha ~= "" then
      green, green_reason, check_runs = commit_check_runs_merge_gate(opts.repo, head_sha, opts)
    end
  end
  return green, green_reason, check_runs
end

local function evaluate_ci_merge_gate(pr, opts)
  local mergeable, mergeable_reason = pr_mergeable(pr)
  if not mergeable then
    return false, mergeable_reason
  end
  local green, green_reason = evaluate_ci_status_gate(pr, opts)
  if not green then
    if green_reason == "rollup-red" then
      local classification = classify_pr_ci_gate(pr, opts)
      return false, classification.reason
    end
    return false, green_reason
  end
  return true, "merge-gate-ok"
end

local function merge_gate_reason_class(reason)
  local row = merge_gate_reason_row(reason)
  if row ~= nil then
    return row.class
  end
  local text = tostring(reason or "")
  if is_not_mergeable_reason(text) then
    return text
  end
  return strings.sanitize_key(text ~= "" and text or "gate-failed", false):gsub("/", "-")
end

local function merge_gate_reason_requires_pr_merge_product(reason)
  local row = merge_gate_reason_row(reason)
  if row ~= nil then
    return row.requires_pr_merge_product == true
  end
  return false
end

rawset(M, "pr_identity_matches", pr_identity_matches)
rawset(M, "commit_check_runs_merge_gate", commit_check_runs_merge_gate)
rawset(M, "classify_pr_ci_gate", classify_pr_ci_gate)
rawset(M, "rerunnable_check_run_ids_for_head", rerunnable_check_run_ids_for_head)
rawset(M, "evaluate_ci_status_gate", evaluate_ci_status_gate)
rawset(M, "evaluate_ci_merge_gate", evaluate_ci_merge_gate)
rawset(M, "merge_gate_reason_class", merge_gate_reason_class)
rawset(M, "merge_gate_reason_requires_pr_merge_product", merge_gate_reason_requires_pr_merge_product)
return {
  pr_identity_matches = pr_identity_matches,
  commit_check_runs_merge_gate = commit_check_runs_merge_gate,
  classify_pr_ci_gate = classify_pr_ci_gate,
  rerunnable_check_run_ids_for_head = rerunnable_check_run_ids_for_head,
  evaluate_ci_status_gate = evaluate_ci_status_gate,
  evaluate_ci_merge_gate = evaluate_ci_merge_gate,
  merge_gate_reason_class = merge_gate_reason_class,
  merge_gate_reason_requires_pr_merge_product = merge_gate_reason_requires_pr_merge_product,
}
end

return S
