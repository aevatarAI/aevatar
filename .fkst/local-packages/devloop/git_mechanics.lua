local base_ids = require("devloop.base_ids")
local strings = require("contract.strings")
local C = {}
local forge_validators = require("devloop.forge_validators")


  local function require_safe_branch(name, branch)
    if not forge_validators.is_git_ref_safe(branch) then
      error("github-devloop: invalid " .. tostring(name))
    end
    return tostring(branch)
  end

  local function require_safe_remote(remote)
    return forge_validators.require_safe_remote(remote, "github-devloop")
  end

  local function require_safe_sha(name, sha)
    if not forge_validators.is_git_sha(sha) then
      error("github-devloop: invalid " .. tostring(name))
    end
    return tostring(sha)
  end

  local function require_safe_repo(repo)
    local value = tostring(repo or "")
    if value == "" or base_ids.safe_repo(value) ~= value then
      error("github-devloop: invalid branch sync repo")
    end
    return value
  end

  local function require_sync_result(result)
    if result ~= "clean" and result ~= "resolved" then
      error("github-devloop: invalid branch sync result")
    end
    return result
  end

  local function runtime_root_path(M, runtime_root)
    local root = strings.trim(runtime_root)
    if root == "" or root:find("[\r\n]") ~= nil then
      error("github-devloop: invalid FKST_RUNTIME_ROOT")
    end
    return root:gsub("/+$", "")
  end


  local function run_git(fn, label)
    local ok, result_or_error = pcall(fn)
    if ok then
      return result_or_error
    end
    if type(result_or_error) == "table" and result_or_error.result ~= nil then
      return result_or_error.result
    end
    error(tostring(label or "git-adapter operation") .. " failed: " .. tostring(result_or_error))
  end

  local function run_git_ok(fn, label)
    local result = run_git(fn, label)
    if result.exit_code ~= 0 then
      return nil, tostring(label or "git-adapter operation") .. " failed: " .. tostring(result.stderr)
    end
    return result
  end

  function C.repo_ref_store_lock_key(repo)
    local key = "github-devloop/git/"
      .. base_ids.safe_repo(require_safe_repo(repo))
      .. "/fetch"
    if not strings.is_path_safe_key(key, require("devloop.base")._max_key_len) then
      error("github-devloop: invalid git ref-store lock key")
    end
    return key
  end

  function C.with_repo_ref_store_lock(repo, fn)
    return with_lock(C.repo_ref_store_lock_key(repo), fn)
  end

  local function trim_stdout(result)
    return tostring(result.stdout or ""):gsub("%s+$", "")
  end

  function C.run_required(result, error_class)
    if result.exit_code ~= 0 then
      error("github-devloop: " .. error_class .. " failed: " .. tostring(result.stderr))
    end
    return result
  end

  function C.fetch_branch(git, branch, error_class)
    C.run_required(git.fetch_branch(require_safe_remote("origin"), require_safe_branch("fetch branch", branch), 60), error_class)
  end

  function C.fetch_branches(git, repo, branches, error_class)
    C.with_repo_ref_store_lock(repo, function()
      for _, branch in ipairs(branches) do
        C.fetch_branch(git, branch, error_class)
      end
    end)
  end

  function C.remote_head(git, branch, error_class, unsafe_error)
    local result = C.run_required(git.remote_branch_head(require_safe_remote("origin"), require_safe_branch("remote branch", branch), 30), error_class)
    local head = trim_stdout(result)
    if not require("devloop.pr_safety").is_safe_head_sha(head) then
      error("github-devloop: " .. unsafe_error)
    end
    return head
  end

  function C.is_ancestor(git, ancestor_sha, descendant_sha, error_class)
    local result = C.git_is_ancestor(git, ancestor_sha, descendant_sha, 30)
    if result.exit_code == 0 then
      return true
    end
    if result.exit_code == 1 then
      return false
    end
    error("github-devloop: " .. error_class .. " failed: " .. tostring(result.stderr))
  end

  function C.runtime_root_with_exec(exec_sync_fn)
    local result = C.run_required(exec_sync_fn({ cmd = require("devloop.base").read_runtime_root_cmd(), timeout = 30 }), "FKST_RUNTIME_ROOT read")
    return result.stdout
  end

  function C.git_is_ancestor(git, maybe_ancestor_sha, descendant_sha, timeout)
    return git.is_ancestor(
      require_safe_sha("ancestor sha", maybe_ancestor_sha),
      require_safe_sha("descendant sha", descendant_sha),
      timeout
    )
  end

  function C.git_merge_no_ff(git, worktree, sha, timeout)
    return git.merge_no_ff(worktree, require_safe_sha("merge sha", sha), timeout)
  end

  function C.git_fast_forward(git, worktree, sha, timeout)
    return git.fast_forward(worktree, require_safe_sha("fast-forward sha", sha), timeout)
  end

  function C.git_remote_trees_equal_quiet(git, upstream, integration, timeout)
    return git.remote_trees_equal_quiet(
      require_safe_branch("upstream branch", upstream),
      require_safe_branch("integration branch", integration),
      timeout
    )
  end

  function C.git_trees_equal_quiet(git, sha_a, sha_b, timeout)
    return git.trees_equal_quiet(
      require_safe_sha("tree compare sha", sha_a),
      require_safe_sha("tree compare sha", sha_b),
      timeout
    )
  end

  function C.current_base_head(git, base_branch)
    local branch = require_safe_branch("base branch", base_branch)
    local fetch_result, fetch_error = run_git_ok(function()
      return git.fetch_branch("origin", branch, 60)
    end, "base fetch")
    if fetch_result == nil then
      return nil, fetch_error
    end
    local head_result, head_error = run_git_ok(function()
      return git.remote_branch_head("origin", branch, 30)
    end, "base head")
    if head_result == nil then
      return nil, head_error
    end
    local base_head = tostring(head_result.stdout or ""):gsub("%s+$", "")
    if not require("devloop.pr_safety").is_safe_head_sha(base_head) then
      return nil, "unsafe base head"
    end
    return base_head
  end

  function C.has_empty_resolution_delta(git, approved_head_sha, base_head_sha, new_head_sha)
    local approved = require_safe_sha("approved head sha", approved_head_sha)
    local base = require_safe_sha("base head sha", base_head_sha)
    local new_head = require_safe_sha("new head sha", new_head_sha)
    local merge_tree = git.merge_tree(approved, base, 120)
    local tree = tostring(merge_tree.stdout or ""):gsub("%s+$", "")
    if tree == "" then
      return false, "merge-tree produced no tree"
    end
    local result = git.trees_equal_quiet(tree, new_head, 30)
    if result.exit_code == 0 then
      return true, "empty"
    end
    return false, tostring(result.stderr or "")
  end

  function C.current_branch_head_sha(git, branch)
    local safe_branch = require_safe_branch("branch", branch)
    local fetch_result = git.fetch_branch("origin", safe_branch, 60)
    if fetch_result.exit_code ~= 0 then
      return nil
    end
    local head_result = git.fetch_head_commit(30)
    if head_result.exit_code ~= 0 then
      return nil
    end
    local head_sha = tostring(head_result.stdout or ""):gsub("%s+$", "")
    if not require("devloop.pr_safety").is_safe_head_sha(head_sha) then
      error("github-devloop: unsafe PR origin branch head sha")
    end
    return head_sha
  end

  function C.git_push_branch_force_with_lease(git, branch, new_sha, expected_old_sha, timeout)
    return git.push_branch_force_with_lease(
      require_safe_branch("push branch", branch),
      require_safe_sha("new branch sha", new_sha),
      require_safe_sha("expected old branch sha", expected_old_sha),
      timeout
    )
  end

  function C.git_push_branch_update(git, branch, timeout)
    return git.push_branch_update(require_safe_branch("push branch", branch), timeout)
  end

  function C.git_push_worktree_branch_update(git, worktree, branch, timeout)
    return git.push_worktree_branch_update(worktree, require_safe_branch("push branch", branch), nil, timeout)
  end


  function C.git_diff_check(git, worktree, timeout)
    return git.diff_check(worktree, false, timeout)
  end

  function C.git_diff_cached_check(git, worktree, timeout)
    return git.diff_check(worktree, true, timeout)
  end




function C.helpers(M)
  return {
    require_safe_branch = require_safe_branch,
    require_safe_sha = require_safe_sha,
    require_safe_repo = require_safe_repo,
    require_sync_result = require_sync_result,
    runtime_root_path = function(runtime_root) return runtime_root_path(M, runtime_root) end,
  }
end

return C
