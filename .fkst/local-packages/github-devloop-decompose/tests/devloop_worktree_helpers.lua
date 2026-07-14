local devloop_base = require("devloop.base")
local base_ids = require("devloop.base_ids")
local base = require("tests.devloop_base_helpers")
local t = base.t
local core = base.core
local gh_argv = require("testkit.gh_argv_mock")
local function mock_setup_worktree(path)
  t.mock_command("git -C", {
    stdout = "dev\n",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("git -C", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("rev-parse --abbrev-ref HEAD", {
    stdout = "devloop-owner-repo-42-01HY\n",
    stderr = "",
    exit_code = 0,
  })
  return path
end

local function deterministic_branch_for(event)
  local repo, issue_number = base_ids.parse_proposal_id(event.proposal_id)
  return devloop_base.implement_branch(repo, issue_number, event.dedup_key)
end

local function mock_implement_worktree_reconcile()
  t.mock_command("reset --hard", {
    stdout = "HEAD is now at abc123 implementation branch\n",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("clean -fd", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
end

local function mock_worktree_parent_mkdir()
  t.mock_command("mkdir -p", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
end

local function mock_fresh_implement_worktree(path)
  t.mock_command("git fetch 'origin' 'dev'", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("refs/remotes/'origin'/'dev'^{commit}", {
    stdout = "abc123\n",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("show-ref --verify --quiet", {
    stdout = "",
    stderr = "",
    exit_code = 1,
  })
  t.mock_command('printf %s "$FKST_RUNTIME_ROOT"', {
    stdout = path or "/tmp/fkst-packages-test/github-devloop/runtime",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("git worktree remove --force", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("git worktree prune", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  mock_worktree_parent_mkdir()
  t.mock_command("git worktree add -b", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  mock_implement_worktree_reconcile()
  t.mock_command("merge --no-edit 'abc123'", {
    stdout = "Already up to date.\n",
    stderr = "",
    exit_code = 0,
  })
end

local function mock_existing_empty_implement_worktree(path)
  t.mock_command("git fetch 'origin' 'dev'", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("refs/remotes/'origin'/'dev'^{commit}", {
    stdout = "abc123\n",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("show-ref --verify --quiet", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("rev-list --count", {
    stdout = "0\n",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command('printf %s "$FKST_RUNTIME_ROOT"', {
    stdout = path or "/tmp/fkst-packages-test/github-devloop/runtime",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("git worktree list --porcelain", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("git worktree remove --force", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("git worktree prune", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  mock_worktree_parent_mkdir()
  t.mock_command("git worktree add", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  mock_implement_worktree_reconcile()
  t.mock_command("merge --no-edit 'abc123'", {
    stdout = "Already up to date.\n",
    stderr = "",
    exit_code = 0,
  })
end

local function mock_existing_empty_implement_worktree_reuse(path, branch, ahead_count)
  local worktree = (path or "/tmp/fkst-packages-test/github-devloop/runtime")
    .. "/worktrees/devloop-owner-repo-42-01HY"
  t.mock_command("git fetch 'origin' 'dev'", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("refs/remotes/'origin'/'dev'^{commit}", {
    stdout = "abc123\n",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("show-ref --verify --quiet", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("rev-list --count", {
    stdout = tostring(ahead_count or "0") .. "\n",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command('printf %s "$FKST_RUNTIME_ROOT"', {
    stdout = path or "/tmp/fkst-packages-test/github-devloop/runtime",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("git worktree list --porcelain", {
    stdout = "worktree " .. worktree .. "\nHEAD abc123\nbranch refs/heads/" .. tostring(branch) .. "\n\n",
    stderr = "",
    exit_code = 0,
  })
  mock_implement_worktree_reconcile()
  t.mock_command("merge --no-edit 'abc123'", {
    stdout = "Already up to date.\n",
    stderr = "",
    exit_code = 0,
  })
  return worktree
end

local function mock_existing_dirty_implement_worktree_reuse(path, branch, ahead_count)
  return mock_existing_empty_implement_worktree_reuse(path, branch, ahead_count)
end

local function mock_outside_runtime_implement_worktree_rebuild(runtime_root, branch)
  local runtime = runtime_root or "/tmp/fkst-packages-test/github-devloop/runtime"
  local stale = "/tmp/fkst-packages-test/github-devloop/old-runtime/worktrees/devloop-owner-repo-42-01HY"
  t.mock_command("git fetch 'origin' 'dev'", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("refs/remotes/'origin'/'dev'^{commit}", {
    stdout = "abc123\n",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("show-ref --verify --quiet", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("rev-list --count", {
    stdout = "1\n",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command('printf %s "$FKST_RUNTIME_ROOT"', {
    stdout = runtime,
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("git worktree list --porcelain", {
    stdout = "worktree " .. stale .. "\nHEAD abc123\nbranch refs/heads/" .. tostring(branch) .. "\n\n",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("[ -d '" .. stale .. "' ]", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("git worktree remove --force", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("git worktree remove --force", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("git worktree prune", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  mock_worktree_parent_mkdir()
  t.mock_command("git worktree add", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  mock_implement_worktree_reconcile()
  t.mock_command("merge --no-edit 'abc123'", {
    stdout = "Already up to date.\n",
    stderr = "",
    exit_code = 0,
  })
  return runtime .. "/worktrees/devloop-owner-repo-42-01HY"
end

local function mock_multiple_outside_runtime_implement_worktrees_rebuild(runtime_root, branch)
  local runtime = runtime_root or "/tmp/fkst-packages-test/github-devloop/runtime"
  local stale_one = "/tmp/fkst-packages-test/github-devloop/old-runtime-a/worktrees/devloop-owner-repo-42-01HY"
  local stale_two = "/tmp/fkst-packages-test/github-devloop/old-runtime-b/worktrees/devloop-owner-repo-42-01HY"
  t.mock_command("git fetch 'origin' 'dev'", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("refs/remotes/'origin'/'dev'^{commit}", {
    stdout = "abc123\n",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("show-ref --verify --quiet", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("rev-list --count", {
    stdout = "1\n",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command('printf %s "$FKST_RUNTIME_ROOT"', {
    stdout = runtime,
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("git worktree list --porcelain", {
    stdout = "worktree " .. stale_one .. "\nHEAD abc123\nbranch refs/heads/" .. tostring(branch) .. "\n\n"
      .. "worktree " .. stale_two .. "\nHEAD abc123\nbranch refs/heads/" .. tostring(branch) .. "\n\n",
    stderr = "",
    exit_code = 0,
  })
  for _ = 1, 2 do
    t.mock_command("[ -d ", {
      stdout = "",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command("git worktree remove --force", {
      stdout = "",
      stderr = "",
      exit_code = 0,
    })
  end
  t.mock_command("git worktree remove --force", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("git worktree prune", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  mock_worktree_parent_mkdir()
  t.mock_command("git worktree add", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  mock_implement_worktree_reconcile()
  t.mock_command("merge --no-edit 'abc123'", {
    stdout = "Already up to date.\n",
    stderr = "",
    exit_code = 0,
  })
  return runtime .. "/worktrees/devloop-owner-repo-42-01HY"
end

local function mock_existing_implement_branch(head)
  t.mock_command("git fetch 'origin' 'dev'", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("refs/remotes/'origin'/'dev'^{commit}", {
    stdout = "abc123\n",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("show-ref --verify --quiet", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
end

local function mock_git_commit(new_head, branch)
  t.mock_command("git -C", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("commit -m", {
    stdout = "[" .. tostring(branch or "devloop-owner-repo-42-01HY") .. " 1234567] Implement github-devloop ready state\n",
    stderr = "",
    exit_code = 0,
  })
  if branch ~= nil then
    t.mock_command("rev-parse --abbrev-ref HEAD", {
      stdout = tostring(branch) .. "\n",
      stderr = "",
      exit_code = 0,
    })
  end
  t.mock_command("rev-parse HEAD", {
    stdout = (new_head or "def456") .. "\n",
    stderr = "",
    exit_code = 0,
  })
end

local function mock_git_push(branch)
  t.mock_command("git push origin", {
    stdout = "pushed " .. tostring(branch or "branch") .. "\n",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("push origin HEAD:refs/heads/" .. tostring(branch or "branch"), {
    stdout = "pushed " .. tostring(branch or "branch") .. "\n",
    stderr = "",
    exit_code = 0,
  })
end

local function mock_existing_devloop_worktree(issue_slug)
  local slug = tostring(issue_slug or "owner-repo-42")
  t.mock_command("git worktree list", {
    stdout = "/tmp/devloop-" .. slug .. "-01HY"
      .. " abcdef1 [devloop-" .. slug .. "-01HY]\n",
    stderr = "",
    exit_code = 0,
  })
end

local function mock_implement_codex(exit_code, stdout, stderr)
  t.mock_command("codex exec", {
    stdout = stdout or "implemented",
    stderr = stderr or "",
    exit_code = exit_code or 0,
  })
end

local function mock_git_status(stdout, exit_code, stderr)
  t.mock_command("status --porcelain", {
    stdout = stdout or "",
    stderr = stderr or "",
    exit_code = exit_code or 0,
  })
end

local function mock_no_unmerged_paths()
  t.mock_command("ls-files -u", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
end

local function mock_no_conflict_markers()
  t.mock_command("grep -n -I -E", {
    stdout = "",
    stderr = "",
    exit_code = 1,
  })
end

local function mock_existing_fix_worktree(branch, head, path, merge)
  local worktree = path or "/tmp/fkst-packages-test/github-devloop/runtime/worktrees/fix-worktree"
  t.mock_command("git worktree list --porcelain", {
    stdout = "worktree " .. worktree .. "\nHEAD " .. tostring(head or "def456")
      .. "\nbranch refs/heads/" .. tostring(branch) .. "\n\n",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("[ -d '" .. worktree .. "' ]", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("git fetch 'origin' 'dev'", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("refs/remotes/'origin'/'dev'^{commit}", {
    stdout = tostring(merge and merge.sha or "abc123") .. "\n",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("merge --no-edit '" .. tostring(merge and merge.sha or "abc123") .. "'", {
    stdout = merge and merge.stdout or "Already up to date.\n",
    stderr = merge and merge.stderr or "",
    exit_code = merge and merge.exit_code or 0,
  })
  if merge ~= nil and merge.exit_code ~= nil and merge.exit_code ~= 0 then
    t.mock_command("ls-files -u", {
      stdout = merge.unmerged_stdout or "100644 abc123 1\tpackages/github-devloop/core.lua\n",
      stderr = merge.unmerged_stderr or "",
      exit_code = merge.unmerged_exit_code or 0,
    })
  end
  if merge ~= nil and merge.post_codex_unmerged_stdout ~= nil then
    t.mock_command("ls-files -u", {
      stdout = merge.post_codex_unmerged_stdout,
      stderr = merge.post_codex_unmerged_stderr or "",
      exit_code = merge.post_codex_unmerged_exit_code or 0,
    })
  else
    mock_no_unmerged_paths()
  end
  if merge ~= nil and merge.post_codex_conflict_markers_stdout ~= nil then
    t.mock_command("grep -n -I -E", {
      stdout = merge.post_codex_conflict_markers_stdout,
      stderr = merge.post_codex_conflict_markers_stderr or "",
      exit_code = merge.post_codex_conflict_markers_exit_code or 0,
    })
  else
    mock_no_conflict_markers()
  end
  return worktree
end

local function mock_missing_fix_worktree(branch, head, path)
  local worktree = path or "/tmp/fkst-packages-test/github-devloop/old-runtime/worktrees/fix-worktree"
  t.mock_command("git worktree list --porcelain", {
    stdout = "worktree " .. worktree .. "\nHEAD " .. tostring(head or "def456")
      .. "\nbranch refs/heads/" .. tostring(branch) .. "\n\n",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("[ -d '" .. worktree .. "' ]", {
    stdout = "",
    stderr = "",
    exit_code = 1,
  })
  t.mock_command("git worktree prune", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("git fetch 'origin' '" .. tostring(branch) .. "'", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  mock_worktree_parent_mkdir()
  t.mock_command("git worktree add --force -B", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("git fetch 'origin' 'dev'", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("refs/remotes/'origin'/'dev'^{commit}", {
    stdout = "abc123\n",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("merge --no-edit 'abc123'", {
    stdout = "Already up to date.\n",
    stderr = "",
    exit_code = 0,
  })
  mock_no_unmerged_paths()
  mock_no_conflict_markers()
  return worktree
end

local function mock_outside_runtime_fix_worktree(branch, head, path)
  local worktree = path or "/tmp/fkst-packages-test/github-devloop/old-runtime/worktrees/fix-worktree"
  t.mock_command("git worktree list --porcelain", {
    stdout = "worktree " .. worktree .. "\nHEAD " .. tostring(head or "def456")
      .. "\nbranch refs/heads/" .. tostring(branch) .. "\n\n",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("[ -d '" .. worktree .. "' ]", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("git worktree remove --force", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("git fetch 'origin' '" .. tostring(branch) .. "'", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  mock_worktree_parent_mkdir()
  t.mock_command("git worktree add --force -B", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("git fetch 'origin' 'dev'", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("refs/remotes/'origin'/'dev'^{commit}", {
    stdout = "abc123\n",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("merge --no-edit 'abc123'", {
    stdout = "Already up to date.\n",
    stderr = "",
    exit_code = 0,
  })
  mock_no_unmerged_paths()
  mock_no_conflict_markers()
  return worktree
end

local function mock_write_env(value)
  t.mock_command('printf %s "$FKST_GITHUB_WRITE"', {
    stdout = value or "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command('printf %s "$FKST_GITHUB_WRITE"', {
    stdout = value or "",
    stderr = "",
    exit_code = 0,
  })
end

local function mock_bot_env(value)
  t.mock_command('printf %s "$FKST_GITHUB_BOT_LOGIN"', {
    stdout = value or "fkst-test-bot",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command('printf %s "$FKST_GITHUB_BOT_LOGIN"', {
    stdout = value or "fkst-test-bot",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command('printf %s "$FKST_DEVLOOP_UPSTREAM_BRANCH"', {
    stdout = "dev",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command('printf %s "$FKST_DEVLOOP_INTEGRATION_BRANCH"', {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command('printf %s "$FKST_DEVLOOP_ROLLUP_MERGE"', {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
end

local function mock_issue_view_failure(json_selector, stderr)
  t.mock_command(json_selector, {
    stdout = "",
    stderr = stderr or "forced issue view failure",
    exit_code = 1,
  })
end

local function count_calls(needle)
  local count = gh_argv.count_calls(t, needle)
  local alternate = nil
  if needle == "--json headRefName,headRefOid,baseRefName,state,comments" then
    alternate = "--json title,body,headRefName,headRefOid,baseRefName,state,updatedAt,mergedAt,comments,labels,mergeable,mergeStateStatus"
  end
  if alternate ~= nil then
    for _, call in ipairs(t.command_calls()) do
      if tostring(call.rendered or ""):find(alternate, 1, true) ~= nil then
        count = count + 1
      end
    end
  end
  return count
end

local function find_raise(raises, queue, predicate)
  for _, raised in ipairs(raises or {}) do
    if raised.queue == queue
      and (predicate == nil or predicate(raised.payload, raised)) then
      return raised
    end
  end
  if queue == "github-proxy.github_issue_comment_request" then
    for _, raised in ipairs(raises or {}) do
      if raised.queue == "github-proxy.github_pr_comment_request"
        and (predicate == nil or predicate(raised.payload, raised)) then
        return raised
      end
    end
  end
  return nil
end


return {
  mock_setup_worktree = mock_setup_worktree,
  deterministic_branch_for = deterministic_branch_for,
  mock_fresh_implement_worktree = mock_fresh_implement_worktree,
  mock_existing_empty_implement_worktree = mock_existing_empty_implement_worktree,
  mock_existing_empty_implement_worktree_reuse = mock_existing_empty_implement_worktree_reuse,
  mock_existing_dirty_implement_worktree_reuse = mock_existing_dirty_implement_worktree_reuse,
  mock_outside_runtime_implement_worktree_rebuild = mock_outside_runtime_implement_worktree_rebuild,
  mock_multiple_outside_runtime_implement_worktrees_rebuild = mock_multiple_outside_runtime_implement_worktrees_rebuild,
  mock_existing_implement_branch = mock_existing_implement_branch,
  mock_git_commit = mock_git_commit,
  mock_git_push = mock_git_push,
  mock_existing_devloop_worktree = mock_existing_devloop_worktree,
  mock_implement_codex = mock_implement_codex,
  mock_git_status = mock_git_status,
  mock_existing_fix_worktree = mock_existing_fix_worktree,
  mock_missing_fix_worktree = mock_missing_fix_worktree,
  mock_outside_runtime_fix_worktree = mock_outside_runtime_fix_worktree,
  mock_write_env = mock_write_env,
  mock_bot_env = mock_bot_env,
  mock_issue_view_failure = mock_issue_view_failure,
  count_calls = count_calls,
  find_raise = find_raise,
}
