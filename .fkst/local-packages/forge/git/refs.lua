local M = {}
local argv_render = require("forge.argv")
local gitref = require("forge.gitref")

local function require_commit_message(message)
  local bounded_message = tostring(message or "")
  if bounded_message == "" or #bounded_message > 200 then
    error("github-devloop: invalid git commit message")
  end
  return bounded_message
end

local function push_branch_argv(branch)
  return { "git", "push", "-u", "origin", tostring(branch) }
end

local function push_branch_plain_argv(branch)
  return { "git", "push", "origin", tostring(branch) }
end

local function show_ref_branch_argv(branch)
  return { "git", "show-ref", "--verify", "refs/heads/" .. tostring(branch) }
end

local function show_ref_branch_quiet_argv(branch)
  return { "git", "show-ref", "--verify", "--quiet", "refs/heads/" .. tostring(branch) }
end

local function show_ref_worktree_branch_quiet_argv(worktree, branch)
  return { "git", "-C", tostring(worktree), "show-ref", "--verify", "--quiet", "refs/heads/" .. tostring(branch) }
end

local function is_ancestor_argv(maybe_ancestor_sha, descendant_sha)
  return { "git", "merge-base", "--is-ancestor", tostring(maybe_ancestor_sha), tostring(descendant_sha) }
end

local function fetch_branch_argv(remote, branch)
  return { "git", "fetch", tostring(remote), tostring(branch) }
end

local function fetch_ref_argv(remote, ref)
  return { "git", "fetch", tostring(remote), tostring(ref) }
end

local function fetch_pr_merge_ref_argv(remote, pr_number)
  return fetch_ref_argv(remote, "refs/pull/" .. tostring(pr_number) .. "/merge")
end

local function ls_remote_ref_argv(remote, ref)
  return { "git", "ls-remote", tostring(remote), tostring(ref) }
end

local function ls_remote_branch_argv(remote, branch)
  return { "git", "ls-remote", tostring(remote), "refs/heads/" .. tostring(branch) }
end

local function fetch_remote_branch_to_tracking_ref_argv(remote, branch, tracking_ref)
  return { "git", "fetch", tostring(remote), "refs/heads/" .. tostring(branch) .. ":" .. tostring(tracking_ref) }
end

local function remote_branch_head_argv(remote, branch)
  return { "git", "rev-parse", "--verify", "refs/remotes/" .. tostring(remote) .. "/" .. tostring(branch) .. "^{commit}" }
end

local function rev_parse_ref_commit_argv(ref)
  return { "git", "rev-parse", "--verify", tostring(ref) .. "^{commit}" }
end

local function rev_parse_ref_tree_argv(ref)
  return { "git", "rev-parse", "--verify", tostring(ref) .. "^{tree}" }
end

local function cat_file_pretty_argv(ref)
  return { "git", "cat-file", "-p", tostring(ref) }
end

local function commit_tree_argv(tree_sha, parent_sha, message_file)
  local argv = { "git", "commit-tree", tostring(tree_sha) }
  if parent_sha ~= nil and tostring(parent_sha) ~= "" then
    table.insert(argv, "-p")
    table.insert(argv, tostring(parent_sha))
  end
  table.insert(argv, "-F")
  table.insert(argv, tostring(message_file))
  return argv
end

local function push_ref_update_argv(remote, sha, ref, force_with_lease)
  local argv = {
    "git",
    "push",
    tostring(remote),
    tostring(sha) .. ":" .. tostring(ref),
  }
  if force_with_lease ~= nil and force_with_lease ~= false then
    table.insert(argv, "--force-with-lease=" .. tostring(ref) .. ":" .. tostring(force_with_lease))
  end
  return argv
end

local function fetch_head_commit_argv()
  return { "git", "rev-parse", "--verify", "FETCH_HEAD^{commit}" }
end

local function current_branch_argv()
  return { "git", "rev-parse", "--abbrev-ref", "HEAD" }
end

local function branch_head_argv(branch)
  return { "git", "rev-parse", "--verify", "refs/heads/" .. tostring(branch) }
end

local function rev_parse_verify_head_argv()
  return { "git", "rev-parse", "--verify", "HEAD" }
end

local function worktree_argv(worktree, ...)
  local argv = { "git", "-C", tostring(worktree) }
  for _, value in ipairs({ ... }) do
    table.insert(argv, tostring(value))
  end
  return argv
end

local function current_branch_worktree_argv(worktree)
  return worktree_argv(worktree, "rev-parse", "--abbrev-ref", "HEAD")
end

local function merge_no_ff_argv(worktree, sha)
  return worktree_argv(worktree, "merge", "--no-ff", "--no-commit", sha)
end

local function merge_no_edit_argv(worktree, sha)
  return worktree_argv(worktree, "merge", "--no-edit", sha)
end

local function fast_forward_argv(worktree, sha)
  return worktree_argv(worktree, "merge", "--ff-only", sha)
end

local function remote_trees_equal_quiet_argv(upstream, integration)
  return {
    "git",
    "diff",
    "--quiet",
    "refs/remotes/origin/" .. tostring(upstream),
    "refs/remotes/origin/" .. tostring(integration),
  }
end

local function trees_equal_quiet_argv(sha_a, sha_b)
  return { "git", "diff", "--quiet", tostring(sha_a), tostring(sha_b) }
end

local function show_file_argv(ref, path)
  return { "git", "show", tostring(ref) .. ":" .. tostring(path) }
end

local function diff_name_only_argv(worktree, ref)
  if worktree == nil then
    return { "git", "diff", "--name-only", tostring(ref) }
  end
  return worktree_argv(worktree, "diff", "--name-only", tostring(ref))
end

local function merge_tree_argv(approved_head_sha, base_head_sha)
  return { "git", "merge-tree", "--write-tree", tostring(approved_head_sha), tostring(base_head_sha) }
end

local function push_branch_force_with_lease_argv(branch, new_sha, expected_old_sha)
  local ref = "refs/heads/" .. tostring(branch)
  return {
    "git",
    "push",
    "origin",
    tostring(new_sha) .. ":" .. ref,
    "--force-with-lease=" .. ref .. ":" .. tostring(expected_old_sha),
  }
end

local function push_branch_update_argv(branch)
  return { "git", "push", "origin", "HEAD:refs/heads/" .. tostring(branch) }
end

local function push_worktree_branch_update_argv(worktree, branch, expected_old_sha)
  local ref = "refs/heads/" .. tostring(branch)
  local argv = worktree_argv(worktree, "push", "origin", "HEAD:" .. ref)
  if expected_old_sha ~= nil then
    table.insert(argv, "--force-with-lease=" .. ref .. ":" .. tostring(expected_old_sha))
  end
  return argv
end

local function unmerged_paths_argv(worktree)
  if worktree == nil then
    return { "git", "ls-files", "-u" }
  end
  return worktree_argv(worktree, "ls-files", "-u")
end

local function diff_check_argv(worktree, cached)
  local args = cached and { "diff", "--cached", "--check" } or { "diff", "--check" }
  if worktree == nil then
    table.insert(args, 1, "git")
    return args
  end
  if cached then
    return worktree_argv(worktree, "diff", "--cached", "--check")
  end
  return worktree_argv(worktree, "diff", "--check")
end

local function conflict_markers_argv(worktree)
  local pattern = "^(" .. string.rep("<", 7) .. "|" .. string.rep("=", 7) .. "|" .. string.rep(">", 7) .. ")"
  if worktree == nil then
    return { "git", "grep", "-n", "-I", "-E", pattern, "--", "." }
  end
  return worktree_argv(worktree, "grep", "-n", "-I", "-E", pattern, "--", ".")
end

local function commit_message_file_argv(worktree, message_file)
  return worktree_argv(worktree, "commit", "-F", message_file)
end

local function worktree_add_detached_argv(worktree, sha)
  return { "git", "worktree", "add", "--detach", tostring(worktree), tostring(sha) }
end

local function worktree_add_reset_branch_argv(worktree, branch, base)
  return { "git", "worktree", "add", "-B", tostring(branch), tostring(worktree), tostring(base) }
end

local function worktree_remove_argv(worktree)
  return { "git", "worktree", "remove", "--force", tostring(worktree) }
end

local function worktree_list_argv()
  return { "git", "worktree", "list", "--porcelain" }
end

local function add_all_argv(worktree)
  return worktree_argv(worktree, "add", "-A")
end

local function commit_message_argv(worktree, message)
  return worktree_argv(worktree, "commit", "-m", message)
end

local function empty_commit_message_argv(worktree, message)
  return worktree_argv(worktree, "commit", "--allow-empty", "-m", message)
end

local function status_porcelain_argv(worktree)
  return worktree_argv(worktree, "status", "--porcelain")
end

local function clean_fd_argv(worktree)
  return worktree_argv(worktree, "clean", "-fd")
end

local function reset_hard_branch_argv(worktree, branch)
  return worktree_argv(worktree, "reset", "--hard", "refs/heads/" .. tostring(branch))
end

local function switch_branch_argv(worktree, branch)
  return worktree_argv(worktree, "switch", tostring(branch))
end

local function rev_parse_worktree_branch_argv(worktree, branch)
  return worktree_argv(worktree, "rev-parse", "--verify", "refs/heads/" .. tostring(branch))
end

local function head_sha_argv(worktree)
  return worktree_argv(worktree, "rev-parse", "HEAD")
end

local function remote_ahead_count_argv(upstream, integration)
  return {
    "git",
    "rev-list",
    "--count",
    "refs/remotes/origin/" .. tostring(upstream) .. "..refs/remotes/origin/" .. tostring(integration),
  }
end

local function branch_ahead_count_argv(base, branch)
  return { "git", "rev-list", "--count", tostring(base) .. "..refs/heads/" .. tostring(branch) }
end

local function worktree_add_new_branch_argv(worktree, branch, base)
  return { "git", "worktree", "add", "-b", tostring(branch), tostring(worktree), tostring(base) }
end

local function worktree_add_existing_branch_argv(worktree, branch)
  return { "git", "worktree", "add", tostring(worktree), tostring(branch) }
end

local function worktree_add_remote_branch_argv(worktree, remote, branch, force)
  local argv = { "git", "worktree", "add" }
  if force then
    table.insert(argv, "--force")
  end
  table.insert(argv, "-B")
  table.insert(argv, tostring(branch))
  table.insert(argv, tostring(worktree))
  table.insert(argv, "refs/remotes/" .. tostring(remote) .. "/" .. tostring(branch))
  return argv
end

local function worktree_prune_argv()
  return { "git", "worktree", "prune" }
end

local function exec_result(handle, argv, timeout, context)
  local ok, result_or_error = pcall(handle._exec, argv, timeout, context)
  if ok then
    return result_or_error
  end
  if type(result_or_error) == "table" and result_or_error.result ~= nil then
    return result_or_error.result
  end
  error(result_or_error)
end

function M.install(handle)
  function handle.push_branch(branch, timeout)
    return exec_result(handle, push_branch_argv(branch), timeout, "git push")
  end

  function handle.push_branch_plain(branch, timeout)
    return exec_result(handle, push_branch_plain_argv(branch), timeout, "git push branch")
  end

  function handle.show_ref_branch(branch, timeout)
    return exec_result(handle, show_ref_branch_argv(branch), timeout, "git show-ref")
  end

  function handle.show_ref_branch_quiet(branch, timeout)
    return exec_result(handle, show_ref_branch_quiet_argv(branch), timeout, "git show-ref --quiet")
  end

  function handle.show_ref_worktree_branch_quiet(worktree, branch, timeout)
    return exec_result(handle, show_ref_worktree_branch_quiet_argv(worktree, branch), timeout, "git show-ref --quiet")
  end

  function handle.is_ancestor(maybe_ancestor_sha, descendant_sha, timeout)
    return exec_result(handle, is_ancestor_argv(maybe_ancestor_sha, descendant_sha), timeout, "git merge-base --is-ancestor")
  end

  function handle.fetch_branch(remote, branch, timeout)
    return exec_result(handle, fetch_branch_argv(remote, branch), timeout, "git fetch")
  end

  function handle.fetch_ref(remote, ref, timeout)
    return exec_result(handle, fetch_ref_argv(remote, ref), timeout, "git fetch ref")
  end

  function handle.fetch_ref_cmd(remote, ref)
    return "git fetch " .. argv_render.shell_single_quote(remote) .. " " .. argv_render.shell_single_quote(ref)
  end

  function handle.fetch_pr_merge_ref(remote, pr_number, timeout)
    return exec_result(handle, fetch_pr_merge_ref_argv(remote, pr_number), timeout, "git fetch PR merge ref")
  end

  function handle.fetch_pr_merge_ref_cmd(remote, pr_number)
    return handle.fetch_ref_cmd(remote, "refs/pull/" .. tostring(pr_number) .. "/merge")
  end

  function handle.ls_remote_ref(remote, ref, timeout)
    return exec_result(handle, ls_remote_ref_argv(remote, ref), timeout, "git ls-remote ref")
  end

  function handle.ls_remote_branch(remote, branch, timeout)
    return exec_result(handle, ls_remote_branch_argv(remote, branch), timeout, "git ls-remote branch")
  end

  function handle.fetch_remote_branch_to_tracking_ref(remote, branch, tracking_ref, timeout)
    return exec_result(handle, fetch_remote_branch_to_tracking_ref_argv(remote, branch, tracking_ref), timeout, "git fetch remote branch")
  end

  function handle.remote_branch_head(remote, branch, timeout)
    return exec_result(handle, remote_branch_head_argv(remote, branch), timeout, "git rev-parse remote branch")
  end

  function handle.rev_parse_ref_commit(ref, timeout)
    return exec_result(handle, rev_parse_ref_commit_argv(ref), timeout, "git rev-parse ref commit")
  end

  function handle.rev_parse_ref_tree(ref, timeout)
    return exec_result(handle, rev_parse_ref_tree_argv(ref), timeout, "git rev-parse ref tree")
  end

  function handle.cat_file_pretty(ref, timeout)
    return exec_result(handle, cat_file_pretty_argv(ref), timeout, "git cat-file -p")
  end

  function handle.commit_tree(tree_sha, parent_sha, message_file, timeout)
    return exec_result(handle, commit_tree_argv(tree_sha, parent_sha, message_file), timeout, "git commit-tree")
  end

  function handle.push_ref_update(remote, sha, ref, force_with_lease, timeout)
    return exec_result(handle, push_ref_update_argv(remote, sha, ref, force_with_lease), timeout, "git push ref update")
  end

  function handle.fetch_head_commit(timeout)
    return exec_result(handle, fetch_head_commit_argv(), timeout, "git rev-parse FETCH_HEAD")
  end

  function handle.current_branch(timeout)
    return exec_result(handle, current_branch_argv(), timeout, "git rev-parse current branch")
  end

  function handle.current_branch_worktree(worktree, timeout)
    return exec_result(handle, current_branch_worktree_argv(worktree), timeout, "git rev-parse current branch")
  end

  function handle.branch_head(branch, timeout)
    return exec_result(handle, branch_head_argv(branch), timeout, "git rev-parse branch")
  end

  function handle.rev_parse_verify_head(timeout)
    return exec_result(handle, rev_parse_verify_head_argv(), timeout, "git rev-parse --verify HEAD")
  end

  function handle.head_sha(worktree, timeout)
    return exec_result(handle, head_sha_argv(worktree), timeout, "git rev-parse HEAD")
  end

  function handle.merge_no_ff(worktree, sha, timeout)
    return exec_result(handle, merge_no_ff_argv(worktree, sha), timeout, "git merge --no-ff")
  end

  function handle.merge_no_edit(worktree, sha, timeout)
    return exec_result(handle, merge_no_edit_argv(worktree, sha), timeout, "git merge --no-edit")
  end

  function handle.merge_no_edit_cmd(worktree, sha)
    return "git -C " .. argv_render.shell_single_quote(worktree) .. " merge --no-edit " .. argv_render.shell_single_quote(sha)
  end

  function handle.fast_forward(worktree, sha, timeout)
    return exec_result(handle, fast_forward_argv(worktree, sha), timeout, "git merge --ff-only")
  end

  function handle.remote_trees_equal_quiet(upstream, integration, timeout)
    return exec_result(handle, remote_trees_equal_quiet_argv(upstream, integration), timeout, "git diff --quiet remote trees")
  end

  function handle.trees_equal_quiet(sha_a, sha_b, timeout)
    return exec_result(handle, trees_equal_quiet_argv(sha_a, sha_b), timeout, "git diff --quiet trees")
  end

  function handle.show_file(ref, path, timeout)
    return exec_result(handle, show_file_argv(ref, path), timeout, "git show file")
  end

  function handle.diff_name_only(worktree, ref, timeout)
    return exec_result(handle, diff_name_only_argv(worktree, ref), timeout, "git diff --name-only")
  end

  function handle.merge_tree(approved_head_sha, base_head_sha, timeout)
    return exec_result(handle, merge_tree_argv(approved_head_sha, base_head_sha), timeout, "git merge-tree --write-tree")
  end

  function handle.push_branch_force_with_lease(branch, new_sha, expected_old_sha, timeout)
    return exec_result(handle, push_branch_force_with_lease_argv(branch, new_sha, expected_old_sha), timeout, "git push --force-with-lease")
  end

  function handle.push_branch_update(branch, timeout)
    return exec_result(handle, push_branch_update_argv(branch), timeout, "git push branch update")
  end

  function handle.push_worktree_branch_update(worktree, branch, expected_old_sha, timeout)
    return exec_result(handle, push_worktree_branch_update_argv(worktree, branch, expected_old_sha), timeout, "git worktree push")
  end

  function handle.git_push_worktree_branch_update_with_lease(worktree, branch, expected_old_sha, timeout)
    return handle.push_worktree_branch_update(
      worktree,
      gitref.require_safe_branch("push branch", branch, "github-devloop"),
      gitref.require_safe_sha("expected old branch sha", expected_old_sha, "github-devloop"),
      timeout
    )
  end

  function handle.unmerged_paths(worktree, timeout)
    return exec_result(handle, unmerged_paths_argv(worktree), timeout, "git ls-files -u")
  end

  function handle.diff_check(worktree, cached, timeout)
    return exec_result(handle, diff_check_argv(worktree, cached), timeout, "git diff --check")
  end

  function handle.conflict_markers(worktree, timeout)
    return exec_result(handle, conflict_markers_argv(worktree), timeout, "git grep conflict markers")
  end

  function handle.commit_message_file(worktree, message_file, timeout)
    return exec_result(handle, commit_message_file_argv(worktree, message_file), timeout, "git commit -F")
  end

  function handle.worktree_add_detached(worktree, sha, timeout)
    return exec_result(handle, worktree_add_detached_argv(worktree, sha), timeout, "git worktree add --detach")
  end

  function handle.git_worktree_add_detached_plan(worktree, sha)
    local value = tostring(worktree or "")
    if value == "" or value:find("[\r\n]") ~= nil then
      error("github-devloop: invalid worktree path")
    end
    return {
      parent_dir = value:gsub("/+$", ""):match("^(.*)/[^/]+$") or ".",
      worktree = value,
      sha = gitref.require_safe_sha("worktree base sha", sha, "github-devloop"),
    }
  end

  function handle.git_worktree_add_detached(worktree, sha, timeout)
    local plan = handle.git_worktree_add_detached_plan(worktree, sha)
    return handle.worktree_add_detached(plan.worktree, plan.sha, timeout)
  end

  function handle.worktree_add_reset_branch(worktree, branch, base, timeout)
    return exec_result(handle, worktree_add_reset_branch_argv(worktree, branch, base), timeout, "git worktree add -B")
  end

  function handle.worktree_add_new_branch(worktree, branch, base, timeout)
    return exec_result(handle, worktree_add_new_branch_argv(worktree, branch, base), timeout, "git worktree add -b")
  end

  function handle.worktree_add_existing_branch(worktree, branch, timeout)
    return exec_result(handle, worktree_add_existing_branch_argv(worktree, branch), timeout, "git worktree add branch")
  end

  function handle.worktree_add_remote_branch(worktree, remote, branch, force, timeout)
    return exec_result(handle, worktree_add_remote_branch_argv(worktree, remote, branch, force), timeout, "git worktree add remote branch")
  end

  function handle.worktree_remove(worktree, timeout)
    return exec_result(handle, worktree_remove_argv(worktree), timeout, "git worktree remove")
  end

  function handle.worktree_list(timeout)
    return exec_result(handle, worktree_list_argv(), timeout, "git worktree list")
  end

  function handle.worktree_prune(timeout)
    return exec_result(handle, worktree_prune_argv(), timeout, "git worktree prune")
  end

  function handle.add_all(worktree, timeout)
    return exec_result(handle, add_all_argv(worktree), timeout, "git add -A")
  end

  function handle.commit_message(worktree, message, timeout)
    return exec_result(handle, commit_message_argv(worktree, message), timeout, "git commit -m")
  end

  function handle.empty_commit_message(worktree, message, timeout)
    return exec_result(handle, empty_commit_message_argv(worktree, message), timeout, "git commit --allow-empty")
  end

  function handle.git_empty_commit(worktree, message, timeout)
    return handle.empty_commit_message(worktree, require_commit_message(message), timeout)
  end

  function handle.git_head_sha(worktree, timeout)
    return handle.head_sha(worktree, timeout)
  end

  function handle.status_porcelain(worktree, timeout)
    return exec_result(handle, status_porcelain_argv(worktree), timeout, "git status --porcelain")
  end

  function handle.clean_fd(worktree, timeout)
    return exec_result(handle, clean_fd_argv(worktree), timeout, "git clean -fd")
  end

  function handle.reset_hard_branch(worktree, branch, timeout)
    return exec_result(handle, reset_hard_branch_argv(worktree, branch), timeout, "git reset --hard")
  end

  function handle.switch_branch(worktree, branch, timeout)
    return exec_result(handle, switch_branch_argv(worktree, branch), timeout, "git switch")
  end

  function handle.rev_parse_worktree_branch(worktree, branch, timeout)
    return exec_result(handle, rev_parse_worktree_branch_argv(worktree, branch), timeout, "git rev-parse branch")
  end

  function handle.remote_ahead_count(upstream, integration, timeout)
    return exec_result(handle, remote_ahead_count_argv(upstream, integration), timeout, "git rev-list remote ahead count")
  end

  function handle.branch_ahead_count(base, branch, timeout)
    return exec_result(handle, branch_ahead_count_argv(base, branch), timeout, "git rev-list branch ahead count")
  end
end

return M
