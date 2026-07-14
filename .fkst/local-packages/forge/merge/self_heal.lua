local S = {}
local check_runs = require("forge.github.check_runs")
local github_adapter = require("forge.github")
local forge_validators = require("forge.gitref")
local git_adapter = require("forge.git")
local strings = require("contract.strings")

function S.install(M, shared, ci_gate)
local github = github_adapter.production_handle
local git = git_adapter.production_handle
local pr_rollup_green = check_runs.pr_rollup_green

local function merge_ci_selfheal_worktree(repo, pr_number, head_sha)
  local runtime_result = exec_sync({ cmd = M.read_runtime_root_cmd(), timeout = 30 })
  if runtime_result.exit_code ~= 0 then
    error("forge.merge: FKST_RUNTIME_ROOT read failed: " .. tostring(runtime_result.stderr))
  end
  local runtime_root = M._trim(runtime_result.stdout)
  if runtime_root == "" or runtime_root:find("[\r\n]") ~= nil then
    error("forge.merge: invalid FKST_RUNTIME_ROOT")
  end
  return runtime_root:gsub("/+$", "")
    .. "/worktrees/merge-ci-selfheal-"
    .. strings.sanitize_key(tostring(repo), false)
    .. "-"
    .. tostring(pr_number)
    .. "-"
    .. tostring(head_sha):sub(1, 12)
end

local function rerequest_head_check_runs(repo, pr_number, head_sha, runs, proposal_id, first_observed_seconds, age_seconds, key)
  local ids = ci_gate.rerunnable_check_run_ids_for_head(runs, head_sha)
  if #ids == 0 then
    return false, "ci-selfheal-no-rerunnable-check-runs"
  end
  for _, id in ipairs(ids) do
    local result = github("forge.merge").gh_check_run_rerequest(repo, id, 30)
    if result.exit_code ~= 0 then
      error("forge.merge: check-run rerequest failed: " .. tostring(result.stderr))
    end
  end
  M.log_line("info", "merge", proposal_id, "ci-selfheal-rerequest", {
    "repo=" .. tostring(repo),
    "pr=" .. tostring(pr_number),
    "head_sha=" .. tostring(head_sha),
    "check_run_count=" .. tostring(#ids),
    "first_observed_seconds=" .. tostring(first_observed_seconds),
    "age_seconds=" .. tostring(age_seconds or ""),
    "once_key=" .. key,
  })
  return true, "ci-selfheal-rerequested"
end

local function nudge_pr_head(repo, pr_number, pr, proposal_id, first_observed_seconds, age_seconds, key)
  local head_sha = tostring(pr and pr.head_sha or "")
  local head_ref = tostring(pr and pr.head_ref_name or "")
  if not forge_validators.is_git_sha(head_sha) then
    return false, "ci-selfheal-invalid-head"
  end
  if not forge_validators.is_git_ref_safe(head_ref) then
    return false, "ci-selfheal-invalid-branch"
  end
  if not require("forge.merge.shared").is_same_repo_pr_head(pr, repo) then
    return false, "ci-selfheal-foreign-head"
  end
  local worktree = merge_ci_selfheal_worktree(repo, pr_number, head_sha)
  local remove_result = M.git_worktree_remove_if_present(worktree, 60)
  if remove_result.exit_code ~= 0 then
    error("forge.merge: merge CI self-heal worktree cleanup failed: " .. tostring(remove_result.stderr))
  end
  local plan = git("forge.merge").git_worktree_add_detached_plan(worktree, head_sha)
  local mkdir_result = exec_sync({ cmd = M.mkdir_p_cmd(plan.parent_dir), timeout = 30 })
  if mkdir_result.exit_code ~= 0 then
    error("forge.merge: merge CI self-heal worktree parent setup failed: " .. tostring(mkdir_result.stderr))
  end
  local add_result = git("forge.merge").git_worktree_add_detached(plan.worktree, plan.sha, 60)
  if add_result.exit_code ~= 0 then
    error("forge.merge: merge CI self-heal worktree add failed: " .. tostring(add_result.stderr))
  end
  local commit_result = git("forge.merge").git_empty_commit(worktree, "chore: nudge PR CI", 60)
  if commit_result.exit_code ~= 0 then
    error("forge.merge: merge CI self-heal empty commit failed: " .. tostring(commit_result.stderr))
  end
  local push_result = git("forge.merge").git_push_worktree_branch_update_with_lease(worktree, head_ref, head_sha, 120)
  if push_result.exit_code ~= 0 then
    error("forge.merge: merge CI self-heal push failed: " .. tostring(push_result.stderr))
  end
  local pushed_head = git("forge.merge").git_head_sha(worktree, 30)
  if pushed_head.exit_code ~= 0 then
    error("forge.merge: merge CI self-heal head read failed: " .. tostring(pushed_head.stderr))
  end
  local new_head_sha = tostring(pushed_head.stdout or ""):gsub("%s+$", "")
  if not forge_validators.is_git_sha(new_head_sha) or new_head_sha == head_sha then
    error("forge.merge: merge CI self-heal did not create a fresh head")
  end
  M.invalidate_entity_after_write(repo, "pr", pr_number)
  M.log_line("info", "merge", proposal_id, "ci-selfheal-head-nudge", {
    "repo=" .. tostring(repo),
    "pr=" .. tostring(pr_number),
    "old_head_sha=" .. tostring(head_sha),
    "new_head_sha=" .. tostring(new_head_sha),
    "head_ref=" .. head_ref,
    "first_observed_seconds=" .. tostring(first_observed_seconds),
    "age_seconds=" .. tostring(age_seconds or ""),
    "once_key=" .. key,
  })
  return true, "ci-selfheal-head-nudged"
end

local function ci_missing_status_dispatch_eligible(pr, now_seconds, first_observed_seconds, grace_seconds)
  local green, green_reason = pr_rollup_green(pr)
  if green or green_reason ~= "missing-status-rollup" then
    return false, green_reason
  end
  local current_seconds = tonumber(now_seconds)
  local observed_seconds = tonumber(first_observed_seconds)
  local grace = tonumber(grace_seconds or 300)
  if observed_seconds == nil or current_seconds == nil then
    return false, "missing-status-age-unknown"
  end
  local age_seconds = current_seconds - observed_seconds
  if age_seconds < grace then
    return false, "missing-status-grace"
  end
  return true, "missing-status-rollup", age_seconds
end

local function ci_selfheal_once(repo, pr_number, pr, proposal_id, grace_seconds, runs)
  local green, green_reason = pr_rollup_green(pr)
  if green or green_reason ~= "missing-status-rollup" then
    return false, green_reason
  end
  local head_sha = tostring(pr and pr.head_sha or "")
  local now_seconds = now()
  local observed_key = M.ci_missing_status_first_observed_key(repo, pr_number, head_sha)
  local first_observed_seconds = tonumber(cache_get(observed_key) or "")
  if first_observed_seconds == nil then
    first_observed_seconds = tonumber(now_seconds)
    if first_observed_seconds == nil then
      return false, "missing-status-age-unknown"
    end
    cache_set(observed_key, tostring(first_observed_seconds))
  end
  local eligible, reason, age_seconds = ci_missing_status_dispatch_eligible({
    status_check_rollup = pr and pr.status_check_rollup,
  }, now_seconds, first_observed_seconds, grace_seconds)
  if not eligible then
    return false, reason
  end
  local key = M.ci_selfheal_once_key(repo, pr_number, head_sha)
  local ran = once(key, function()
    local rerequested, rerequest_reason = rerequest_head_check_runs(
      repo,
      pr_number,
      head_sha,
      runs,
      proposal_id,
      first_observed_seconds,
      age_seconds,
      key
    )
    if rerequested then
      return
    end
    local nudged, nudge_reason = nudge_pr_head(repo, pr_number, pr, proposal_id, first_observed_seconds, age_seconds, key)
    if not nudged then
      error("forge.merge: CI self-heal failed: " .. tostring(rerequest_reason) .. "; " .. tostring(nudge_reason))
    end
  end)
  if not ran then
    return false, "ci-selfheal-already-ran"
  end
  return true, "ci-selfheal-triggered"
end
rawset(M, "ci_missing_status_dispatch_eligible", ci_missing_status_dispatch_eligible)
rawset(M, "ci_selfheal_once", ci_selfheal_once)
return {
  ci_missing_status_dispatch_eligible = ci_missing_status_dispatch_eligible,
  ci_selfheal_once = ci_selfheal_once,
}
end

return S
