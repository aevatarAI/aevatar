local devloop_base = require("devloop.base")
local S = {}
local C = {}
local support = require("devloop.commands.support")
local validators = require("devloop.commands.validators")
local forge_validators = require("devloop.forge_validators")

function S.worktree_parent_dir(worktree)
  local value = tostring(worktree or "")
  if value == "" or value:find("[\r\n]") ~= nil then
    error("github-devloop: invalid worktree path")
  end
  return value:gsub("/+$", ""):match("^(.*)/[^/]+$") or "."
end

function S.run_mkdir(_M, path, timeout)
  local result = exec_sync({ cmd = devloop_base.mkdir_p_cmd(path), timeout = timeout or 30 })
  if result.exit_code ~= 0 then
    error("github-devloop: directory setup failed: " .. tostring(result.stderr))
  end
  return result
end

function S.run_path_is_directory(_M, path, timeout)
  return exec_sync({ cmd = C.path_is_directory_cmd(path), timeout = timeout or 30 })
end

  function C.git_status(worktree, timeout)
    return support.git().status_porcelain(worktree, timeout)
  end

  function C.git_add_all(worktree, timeout)
    return support.git().add_all(worktree, timeout)
  end

  function C.git_commit(worktree, message, timeout)
    local bounded_message = tostring(message or "")
    if bounded_message == "" or #bounded_message > 200 then
      error("github-devloop: invalid git commit message")
    end
    return support.git().commit_message(worktree, bounded_message, timeout)
  end

  function C.git_current_branch(worktree, timeout)
    if worktree == nil then
      return support.git().current_branch(timeout)
    end
    return support.git().current_branch_worktree(worktree, timeout)
  end

  function C.git_base_head(branch, timeout)
    return support.git().remote_branch_head("origin", validators.require_safe_branch("base branch", branch), timeout)
  end

  function C.git_fetch_branch(remote, branch, timeout)
    return support.git().fetch_branch(validators.require_safe_remote(remote), validators.require_safe_branch("fetch branch", branch), timeout)
  end

  function C.git_ls_remote_branch(remote, branch, timeout)
    return support.git().ls_remote_branch(validators.require_safe_remote(remote), validators.require_safe_branch("remote branch", branch), timeout)
  end

  function C.git_ls_remote_ref(remote, ref, timeout)
    return support.git().ls_remote_ref(
      validators.require_safe_remote(remote),
      validators.require_safe_ref("remote ref", ref),
      timeout
    )
  end

  function C.git_fetch_ref(remote, ref, timeout)
    return support.git().fetch_ref(
      validators.require_safe_remote(remote),
      validators.require_safe_ref("fetch ref", ref),
      timeout
    )
  end

  function C.git_fetch_remote_branch_to_tracking_ref(remote, branch, tracking_ref, timeout)
    return support.git().fetch_remote_branch_to_tracking_ref(
      validators.require_safe_remote(remote),
      validators.require_safe_branch("remote branch", branch),
      validators.require_safe_branch("tracking ref", tracking_ref),
      timeout
    )
  end

  function C.git_rev_parse_ref_commit(ref, timeout)
    return support.git().rev_parse_ref_commit(validators.require_safe_ref("ref", ref), timeout)
  end

  function C.git_rev_parse_ref_tree(ref, timeout)
    return support.git().rev_parse_ref_tree(validators.require_safe_ref("tree ref", ref), timeout)
  end

  function C.git_cat_file_pretty(ref, timeout)
    return support.git().cat_file_pretty(validators.require_safe_ref("object ref", ref), timeout)
  end

  function C.git_commit_tree(tree_sha, parent_sha, message_file, timeout)
    local parent = nil
    if parent_sha ~= nil and tostring(parent_sha) ~= "" then
      parent = validators.require_safe_sha("parent commit", parent_sha)
    end
    return support.git().commit_tree(
      validators.require_safe_sha("tree sha", tree_sha),
      parent,
      message_file,
      timeout
    )
  end

  function C.git_push_ref_update(remote, sha, ref, force_with_lease, timeout)
    local lease = false
    if force_with_lease ~= nil and force_with_lease ~= false then
      lease = validators.require_safe_sha("lease sha", force_with_lease)
    end
    return support.git().push_ref_update(
      validators.require_safe_remote(remote),
      validators.require_safe_sha("ref update sha", sha),
      validators.require_safe_ref("ref update ref", ref),
      lease,
      timeout
    )
  end

  function C.git_fetch_pr_merge_ref(remote, pr_number, timeout)
    return support.git().fetch_ref(validators.require_safe_remote(remote), "refs/pull/" .. validators.require_positive_pr_number(pr_number) .. "/merge", timeout)
  end

  function C.git_fetch_pr_head_ref(remote, pr_number, timeout)
    return support.git().fetch_ref(validators.require_safe_remote(remote), "refs/pull/" .. validators.require_positive_pr_number(pr_number) .. "/head", timeout)
  end

  function C.git_fetch_head_commit(timeout)
    return support.git().fetch_head_commit(timeout)
  end

  function C.git_remote_branch_head(remote, branch, timeout)
    return support.git().remote_branch_head(validators.require_safe_remote(remote), validators.require_safe_branch("remote branch", branch), timeout)
  end

  function C.git_worktree_merge_no_edit(worktree, sha, timeout)
    return support.git().merge_no_edit(worktree, validators.require_safe_sha("merge sha", sha), timeout)
  end

  function C.git_worktree_reset_hard(worktree, branch, timeout)
    return support.git().reset_hard_branch(worktree, validators.require_safe_branch("reset branch", branch), timeout)
  end

  function C.git_worktree_clean(worktree, timeout)
    return support.git().clean_fd(worktree, timeout)
  end

  function C.git_ahead_count(upstream, integration, timeout)
    return support.git().remote_ahead_count(
      validators.require_safe_branch("upstream branch", upstream),
      validators.require_safe_branch("integration branch", integration),
      timeout
    )
  end

  function C.git_show_ref_branch(branch, timeout)
    return support.git().show_ref_branch_quiet(validators.require_safe_branch("branch", branch), timeout)
  end

  function C.git_show_ref(worktree, branch, timeout)
    return support.git().show_ref_worktree_branch_quiet(worktree, validators.require_safe_branch("branch", branch), timeout)
  end

  function C.git_branch_ahead_count(base, branch, timeout)
    return support.git().branch_ahead_count(validators.require_safe_sha("base head", base), validators.require_safe_branch("branch", branch), timeout)
  end

  function C.git_branch_head(branch, timeout)
    return support.git().branch_head(validators.require_safe_branch("branch", branch), timeout)
  end

  function C.git_push_branch(branch, timeout)
    return support.git().push_branch_plain(validators.require_safe_branch("branch", branch), timeout)
  end

  function C.git_switch_branch(worktree, branch, timeout)
    return support.git().switch_branch(worktree, validators.require_safe_branch("branch", branch), timeout)
  end

  function C.git_worktree_remove_if_present(worktree, timeout)
    local dir_result = S.run_path_is_directory(nil, worktree, 30)
    if dir_result.exit_code == 1 then
      return { stdout = "", stderr = "", exit_code = 0 }
    end
    if dir_result.exit_code ~= 0 then
      return dir_result
    end
    return support.git().worktree_remove(worktree, timeout)
  end

  function C.git_worktree_force_clean(worktree, timeout)
    local value = tostring(worktree or "")
    if value == "" or value:find("[\r\n]") ~= nil then
      error("github-devloop: invalid worktree path")
    end
    support.git().worktree_remove(value, timeout)
    local prune = C.git_worktree_prune(timeout)
    if prune.exit_code ~= 0 then
      return prune
    end
    return { stdout = "", stderr = "", exit_code = 0 }
  end

  function C.git_worktree_add_new_branch(worktree, branch, base, timeout)
    S.run_mkdir(nil, S.worktree_parent_dir(worktree), 30)
    return support.git().worktree_add_new_branch(worktree, validators.require_safe_branch("branch", branch), validators.require_safe_sha("base head", base), timeout)
  end

  function C.git_worktree_add_reset_branch(worktree, branch, base, timeout)
    S.run_mkdir(nil, S.worktree_parent_dir(worktree), 30)
    return support.git().worktree_add_reset_branch(worktree, validators.require_safe_branch("branch", branch), validators.require_safe_sha("base head", base), timeout)
  end

  function C.git_worktree_add_existing_branch(worktree, branch, timeout)
    S.run_mkdir(nil, S.worktree_parent_dir(worktree), 30)
    return support.git().worktree_add_existing_branch(worktree, validators.require_safe_branch("branch", branch), timeout)
  end

  function C.git_worktree_add_remote_branch(worktree, remote, branch, force, timeout)
    S.run_mkdir(nil, S.worktree_parent_dir(worktree), 30)
    return support.git().worktree_add_remote_branch(
      worktree,
      validators.require_safe_remote(remote),
      validators.require_safe_branch("branch", branch),
      force == true,
      timeout
    )
  end

  function C.git_worktree_list(timeout)
    return support.git().worktree_list(timeout)
  end

  function C.git_worktree_prune(timeout)
    return support.git().worktree_prune(timeout)
  end

  function C.git_rev_parse_branch(worktree, branch, timeout)
    return support.git().rev_parse_worktree_branch(worktree, validators.require_safe_branch("branch", branch), timeout)
  end

  C.read_runtime_root_cmd = devloop_base.read_runtime_root_cmd
  C.mkdir_p_cmd = devloop_base.mkdir_p_cmd

  function C.path_is_directory_cmd(path)
    local value = tostring(path or "")
    if value == "" or value:find("[\r\n]") ~= nil then
      error("github-devloop: invalid directory path")
    end
    return "[ -d " .. devloop_base._shell_single_quote(value) .. " ]"
  end

  function C.existing_implementation_worktree(repo, issue_number, impl_version)
    if issue_number == nil or impl_version == nil then
      return nil
    end
    local runtime = exec_sync({ cmd = C.read_runtime_root_cmd(), timeout = 30 })
    if type(runtime) ~= "table" or runtime.exit_code ~= 0 or tostring(runtime.stdout or "") == "" then
      return nil
    end
    local worktree = devloop_base.implement_worktree_path(runtime.stdout, repo, issue_number, impl_version)
    local directory = exec_sync({ cmd = C.path_is_directory_cmd(worktree), timeout = 30 })
    if type(directory) == "table" and directory.exit_code == 0 then
      return worktree
    end
    return nil
  end

  function C.find_worktrees_for_branch(stdout, branch)
    if not forge_validators.is_git_ref_safe(branch) then
      error("github-devloop: invalid branch")
    end
    local wanted = "refs/heads/" .. tostring(branch)
    local path = nil
    local matches = {}
    for line in (tostring(stdout or "") .. "\n"):gmatch("([^\n]*)\n") do
      if line == "" then
        path = nil
      else
        local current_path = line:match("^worktree%s+(.+)$")
        if current_path ~= nil then
          path = current_path
        elseif line == "branch " .. wanted and path ~= nil and path ~= "" then
          table.insert(matches, path)
        end
      end
    end
    return matches
  end

  function C.find_worktree_for_branch(stdout, branch)
    local matches = C.find_worktrees_for_branch(stdout, branch)
    if #matches > 0 then
      return matches[1]
    end
    return nil
  end

  function C.find_worktree_for_branch_under_runtime(stdout, branch, runtime_root)
    if not forge_validators.is_git_ref_safe(branch) then
      error("github-devloop: invalid branch")
    end
    local wanted = "refs/heads/" .. tostring(branch)
    local path = nil
    for line in (tostring(stdout or "") .. "\n"):gmatch("([^\n]*)\n") do
      if line == "" then
        path = nil
      else
        local current_path = line:match("^worktree%s+(.+)$")
        if current_path ~= nil then
          path = current_path
        elseif line == "branch " .. wanted
          and path ~= nil
          and path ~= ""
          and devloop_base.path_under_runtime_root(runtime_root, path) then
          return path
        end
      end
    end
    return nil
  end

function S.install(M)
  for _, n in ipairs({"find_worktree_for_branch", "find_worktree_for_branch_under_runtime", "find_worktrees_for_branch", "git_add_all", "git_ahead_count", "git_base_head", "git_branch_ahead_count", "git_branch_head", "git_cat_file_pretty", "git_commit", "git_commit_tree", "git_current_branch", "git_fetch_branch", "git_fetch_head_commit", "git_fetch_pr_head_ref", "git_fetch_pr_merge_ref", "git_fetch_ref", "git_fetch_remote_branch_to_tracking_ref", "git_ls_remote_branch", "git_ls_remote_ref", "git_push_branch", "git_push_ref_update", "git_remote_branch_head", "git_rev_parse_branch", "git_rev_parse_ref_commit", "git_rev_parse_ref_tree", "git_show_ref", "git_show_ref_branch", "git_status", "git_switch_branch", "git_worktree_add_existing_branch", "git_worktree_add_new_branch", "git_worktree_add_remote_branch", "git_worktree_add_reset_branch", "git_worktree_clean", "git_worktree_force_clean", "git_worktree_list", "git_worktree_merge_no_edit", "git_worktree_prune", "git_worktree_remove_if_present", "git_worktree_reset_hard", "mkdir_p_cmd", "path_is_directory_cmd", "read_runtime_root_cmd"}) do M[n] = C[n] end
end
C.install = S.install

for k, v in pairs(S) do if C[k] == nil then C[k] = v end end
return C
