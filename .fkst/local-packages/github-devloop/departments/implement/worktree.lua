local devloop_base = require("devloop.base")
local core = require("core")
local exec_sync = exec_sync

local M = {}

function M.prepare_base(branches)
  local fetch_result = core.git_fetch_branch("origin", branches.integration, 60)
  if fetch_result.exit_code ~= 0 then
    error("github-devloop: git integration branch fetch failed: " .. tostring(fetch_result.stderr))
  end
  local base_result = core.git_remote_branch_head("origin", branches.integration, 30)
  if base_result.exit_code ~= 0 then
    error("github-devloop: git integration branch head failed: " .. tostring(base_result.stderr))
  end
  local base_head = tostring(base_result.stdout or ""):gsub("%s+$", "")
  if not require("devloop.pr_safety").is_safe_head_sha(base_head) then
    error("github-devloop: unsafe base head")
  end
  return base_head
end

function M.reconcile_worktree_to_branch(worktree, branch)
  local reset_result = core.git_worktree_reset_hard(worktree, branch, 60)
  if reset_result.exit_code ~= 0 then
    error("github-devloop: git worktree reset failed: " .. tostring(reset_result.stderr))
  end
  local clean_result = core.git_worktree_clean(worktree, 60)
  if clean_result.exit_code ~= 0 then
    error("github-devloop: git worktree clean failed: " .. tostring(clean_result.stderr))
  end
end

function M.remove_stale_worktree(path)
  local dir_result = exec_sync({ cmd = core.path_is_directory_cmd(path), timeout = 30 })
  if dir_result.exit_code ~= 0 and dir_result.exit_code ~= 1 then
    error("github-devloop: git worktree path check failed: " .. tostring(dir_result.stderr))
  end
  if dir_result.exit_code == 1 then
    local prune_result = core.git_worktree_prune(60)
    if prune_result.exit_code ~= 0 then
      error("github-devloop: git worktree prune failed: " .. tostring(prune_result.stderr))
    end
    return
  end
  local remove_result = core.git.worktree_remove(path, 60)
  if remove_result.exit_code ~= 0 then
    error("github-devloop: git worktree remove failed: " .. tostring(remove_result.stderr))
  end
end

function M.prepare_worktree(repo, issue_number, ready, branch, base_head)
  local branch_ref = core.git_show_ref_branch(branch, 30)
  local branch_exists = branch_ref.exit_code == 0
  if branch_ref.exit_code ~= 0 and branch_ref.exit_code ~= 1 then
    error("github-devloop: git branch ref check failed: " .. tostring(branch_ref.stderr))
  end

  local runtime_result = exec_sync({ cmd = core.read_runtime_root_cmd(), timeout = 30 })
  if runtime_result.exit_code ~= 0 then
    error("github-devloop: FKST_RUNTIME_ROOT read failed: " .. tostring(runtime_result.stderr))
  end
  local worktree = devloop_base.implement_worktree_path(runtime_result.stdout, repo, issue_number, ready.dedup_key)
  if branch_exists then
    local list_result = core.git_worktree_list(30)
    if list_result.exit_code ~= 0 then
      error("github-devloop: git worktree list failed: " .. tostring(list_result.stderr))
    end
    local existing_worktree = core.find_worktree_for_branch_under_runtime(list_result.stdout, branch, runtime_result.stdout)
    for _, stale_worktree in ipairs(core.find_worktrees_for_branch(list_result.stdout, branch)) do
      if not devloop_base.path_under_runtime_root(runtime_result.stdout, stale_worktree) then
        core.log_line("info", "implement", ready.proposal_id, "IMPLEMENT", {
          "branch=" .. tostring(branch),
          "worktree=" .. tostring(stale_worktree),
          "reason=removing non-current-runtime deterministic worktree",
        })
        M.remove_stale_worktree(stale_worktree)
      end
    end
    if existing_worktree ~= nil then
      worktree = existing_worktree
      core.log_line("info", "implement", ready.proposal_id, "IMPLEMENT", {
        "branch=" .. tostring(branch),
        "worktree=" .. tostring(worktree),
        "reason=reusing current-runtime deterministic worktree",
      })
    else
      local clean_result = core.git_worktree_force_clean(worktree, 60)
      if clean_result.exit_code ~= 0 then
        error("github-devloop: git worktree cleanup failed: " .. tostring(clean_result.stderr))
      end
      local worktree_result = core.git_worktree_add_existing_branch(worktree, branch, 60)
      if worktree_result.exit_code ~= 0 then
        error("github-devloop: git worktree add failed: " .. tostring(worktree_result.stderr))
      end
    end
  else
    local clean_result = core.git_worktree_force_clean(worktree, 60)
    if clean_result.exit_code ~= 0 then
      error("github-devloop: git worktree cleanup failed: " .. tostring(clean_result.stderr))
    end
    local worktree_result = core.git_worktree_add_new_branch(worktree, branch, base_head, 60)
    if worktree_result.exit_code ~= 0 then
      error("github-devloop: git worktree add failed: " .. tostring(worktree_result.stderr))
    end
  end
  M.reconcile_worktree_to_branch(worktree, branch)
  return worktree
end

return M
