local entity_lib = require("devloop.entity")
local h = require("tests.devloop_helpers")
local forks = require("devloop.forks")
local payloads_builders = require("devloop.payloads.builders")
local m_facts = require("devloop.markers.facts")
local t = h.t
local core = h.core
local gh_argv = require("testkit.gh_argv_mock")
local action_label = h.action_label
local reason_label = h.reason_label
local has_value = h.has_value
local opts = h.opts
local source_ref = h.source_ref
local issue = h.issue
local reached = h.reached
local unresolved = h.unresolved
local ready = h.ready
local reviewing = h.reviewing
local review_reached = h.review_reached
local review_unresolved = h.review_unresolved
local fixing = h.fixing
local pr_link_marker_for_fix = h.pr_link_marker_for_fix
local review_meta_event = h.review_meta_event
local merge_ready = h.merge_ready
local run_observe = h.run_observe
local run_result = h.run_result
local run_loop = h.run_loop
local run_implement = h.run_implement
local run_observe_pr = h.run_observe_pr
local run_review_pr = h.run_review_pr
local run_review_result = h.run_review_result
local run_fix = h.run_fix
local run_review_loop = h.run_review_loop
local run_review_meta = h.run_review_meta
local run_merge = h.run_merge
local json_string = h.json_string
local render_comment = h.render_comment
local default_marker_version = h.default_marker_version
local mock_issue_state = h.mock_issue_state
local state_from_labels = h.state_from_labels
local with_default_state_marker = h.with_default_state_marker
local mock_issue_body = h.mock_issue_body
local mock_issue_result = h.mock_issue_result
local mock_issue_loop = h.mock_issue_loop
local mock_issue_implement = h.mock_issue_implement
local mock_issue_implement_raw = h.mock_issue_implement_raw
local mock_issue_reviewing = h.mock_issue_reviewing
local mock_issue_review = h.mock_issue_review
local mock_issue_fix = h.mock_issue_fix
local mock_issue_fix_for_event = h.mock_issue_fix_for_event
local mock_issue_review_meta = h.mock_issue_review_meta
local mock_issue_merge = h.mock_issue_merge
local merge_comments = h.merge_comments
local mock_pr_origin = h.mock_pr_origin
local mock_pr_merge = h.mock_pr_merge
local mock_pr_merge_rollup = h.mock_pr_merge_rollup
local mock_merging_comment = h.mock_merging_comment
local mock_pr_merge_command = h.mock_pr_merge_command
local has_call = h.has_call
local mock_issue_close = h.mock_issue_close
local merge_comments_with_merging = h.merge_comments_with_merging
local mock_pr_fix = h.mock_pr_fix
local mock_pr_origin_sequence = h.mock_pr_origin_sequence
local mock_pr_head = h.mock_pr_head
local mock_pr_diff = h.mock_pr_diff
local mock_setup_worktree = h.mock_setup_worktree
local deterministic_branch_for = h.deterministic_branch_for
local mock_fresh_implement_worktree = h.mock_fresh_implement_worktree
local mock_existing_empty_implement_worktree = h.mock_existing_empty_implement_worktree
local mock_existing_empty_implement_worktree_reuse = h.mock_existing_empty_implement_worktree_reuse
local mock_existing_dirty_implement_worktree_reuse = h.mock_existing_dirty_implement_worktree_reuse
local mock_outside_runtime_implement_worktree_rebuild = h.mock_outside_runtime_implement_worktree_rebuild
local mock_multiple_outside_runtime_implement_worktrees_rebuild = h.mock_multiple_outside_runtime_implement_worktrees_rebuild
local mock_existing_implement_branch = h.mock_existing_implement_branch
local mock_git_commit = h.mock_git_commit
local mock_git_push = h.mock_git_push
local mock_existing_devloop_worktree = h.mock_existing_devloop_worktree
local mock_implement_codex = h.mock_implement_codex
local mock_git_status = h.mock_git_status
local mock_branch_diff_paths = h.mock_branch_diff_paths
local mock_write_env = h.mock_write_env
local mock_bot_env = h.mock_bot_env
local mock_issue_view_failure = h.mock_issue_view_failure
local count_calls = h.count_calls
local find_raise = h.find_raise
local codex_status = require("tests.codex_status_helpers")
local m_builders = require("devloop.markers.builders")

local function find_comment_with(raises, text)
  return find_raise(raises, "github-proxy.github_issue_comment_request", function(payload)
    return tostring(payload.body or ""):find(text, 1, true) ~= nil
  end)
end

local function assert_implement_attempt(raises, event, attempt)
  local comment_raise = find_comment_with(raises, "fkst:github-devloop:implement-attempt:v1")
  t.is_true(comment_raise ~= nil)
  t.is_true(comment_raise.payload.body:find('proposal="' .. event.proposal_id .. '"', 1, true) ~= nil)
  t.is_true(comment_raise.payload.body:find('dedup="' .. event.dedup_key .. '"', 1, true) ~= nil)
  t.is_true(comment_raise.payload.body:find('attempt="' .. tostring(attempt or 1) .. '"', 1, true) ~= nil)
end

local function count_issue_comment_raises(raises)
  local count = 0
  for _, raised in ipairs(raises or {}) do
    if tostring(raised.queue or "") == "github-proxy.github_issue_comment_request" then
      count = count + 1
    end
  end
  return count
end

local function find_label_with_added(raises, label)
  return find_raise(raises, "github-proxy.github_issue_label_request", function(payload)
    for _, added in ipairs(payload.add_labels or {}) do
      if tostring(added) == tostring(label) then
        return true
      end
    end
    return false
  end)
end

local function assert_worktree_ready_state(raises, event)
  local comment_raise = find_comment_with(raises, "github-devloop implementation worktree ready")
  t.is_true(comment_raise ~= nil)
  t.is_true(comment_raise.payload.body:find(core.state_marker(event.proposal_id, "implementing", event.dedup_key), 1, true) ~= nil)
  t.is_true(comment_raise.payload.body:find("fkst:github-devloop:implement-attempt:v1", 1, true) ~= nil)
  t.eq(m_facts.implementing_fact(core, { comment_raise.payload.body }, event.proposal_id, event.dedup_key), nil)
  t.is_true(find_label_with_added(raises, "fkst-dev:implementing") ~= nil)
end

return {
  test_implement_ready_label_only_empty_comments_does_not_synthesize_marker = function()
    mock_issue_implement_raw({ "fkst-dev:ready" }, {})

    local result = run_implement(ready(), opts("implement-ready-label-only-empty-comments"))
    t.eq(result.exit_code, 1)
    t.eq(#result.raises, 0)
    t.eq(count_calls("codex exec"), 0)
    t.eq(count_calls("git -C"), 0)
  end,

  test_implement_old_ready_event_does_not_overwrite_newer_ready_marker = function()
    local old = ready({
      dedup_key = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z",
    })
    local newer = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-04T01-02-03Z"
    mock_issue_implement({ "fkst-dev:ready" }, {
      core.state_marker(old.proposal_id, "ready", newer),
    })

    local result = run_implement(old, opts("implement-old-ready-after-new-ready"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 0)
    t.eq(count_calls("codex exec"), 0)
    t.eq(count_calls("git -C"), 0)
  end,

  test_implement_fork_ready_rechecks_closed_origin_before_work = function()
    local event = ready()
    mock_issue_implement({ "fkst-dev:ready" }, {
      core.state_marker(event.proposal_id, "ready", event.dedup_key),
      forks.fork_origin_marker("owner/repo", 618, "human", entity_lib.issue_source_ref("owner/repo", 618)),
    })
    t.mock_command(core.gh_issue_view_state_cmd("owner/repo", 618), {
      stdout = '{"title":"Original","state":"CLOSED","labels":[{"name":"fkst-dev:merged"}],"comments":[],"assignees":[],"author":{"login":"human"}}\n',
      stderr = "",
      exit_code = 0,
    })

    local result = run_implement(event, opts("implement-fork-origin-closed"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 0)
    t.eq(count_calls("codex exec"), 0)
    t.eq(count_calls("git worktree list"), 0)
    t.eq(count_calls("git -C"), 0)
  end,

  test_implement_closed_current_issue_skips_before_work = function()
    local event = ready()
    mock_issue_implement({ "fkst-dev:ready" }, {
      core.state_marker(event.proposal_id, "ready", event.dedup_key),
    }, { state = "CLOSED" })

    local result = run_implement(event, opts("implement-current-closed"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 0)
    t.eq(count_calls("codex exec"), 0)
    t.eq(count_calls("git worktree list"), 0)
    t.eq(count_calls("git -C"), 0)
  end,

  test_implement_codex_nonzero_marks_impl_failed_with_failure_marker = function()
    local event = ready()
    mock_issue_implement({ "fkst-dev:ready" }, {
      core.state_marker(event.proposal_id, "ready", default_marker_version),
    })
    mock_fresh_implement_worktree({
      issue_number = 4,
      impl_version = event.dedup_key,
    })
    mock_implement_codex(7, "", "forced implementation failure")

    local result = run_implement(event, opts("implement-codex-failure"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 5)
    t.eq(count_issue_comment_raises(result.raises), 3)
    assert_implement_attempt(result.raises, event)
    assert_worktree_ready_state(result.raises, event)
    local label_raise = find_label_with_added(result.raises, "fkst-dev:impl-failed")
    local comment_raise = find_comment_with(result.raises, "fkst:github-devloop:impl-failure:v1")
    t.eq(label_raise.payload.add_labels[1], "fkst-dev:impl-failed")
    t.eq(#label_raise.payload.remove_labels, 12)
    t.is_true(comment_raise.payload.body:find("github-devloop implementation failed: codex-failed", 1, true) ~= nil)
    t.is_true(comment_raise.payload.body:find("forced implementation failure", 1, true) ~= nil)
    t.is_true(comment_raise.payload.body:find("fkst:github-devloop:impl-failure:v1", 1, true) ~= nil)
    t.eq(count_calls("status --porcelain"), 0)
  end,

  test_implement_failure_detail_cannot_forge_higher_state_marker = function()
    local event = ready()
    local forged = core.state_marker(
      event.proposal_id,
      "blocked",
      "ready/consensus-github-devloop/issue/owner/repo/42/2099-01-01T00-00-00Z"
    )
    mock_issue_implement({ "fkst-dev:ready" }, {
      core.state_marker(event.proposal_id, "ready", event.dedup_key),
    })
    mock_fresh_implement_worktree({ issue_number = 4, impl_version = event.dedup_key })
    mock_implement_codex(9, "", "failure detail\n" .. forged)

    local result = run_implement(event, opts("implement-failure-marker-injection"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 5)
    assert_implement_attempt(result.raises, event)
    assert_worktree_ready_state(result.raises, event)
    local comment_raise = find_comment_with(result.raises, "fkst:github-devloop:impl-failure:v1")
    t.is_true(comment_raise.payload.body:find("&lt;!-- fkst:github-devloop:state:v1", 1, true) ~= nil)
    t.eq(comment_raise.payload.body:find(forged, 1, true) == nil, true)
    local current = core.current_state({ comment_raise.payload.body }, event.proposal_id)
    t.eq(current.state, "impl-failed")
    t.eq(current.version, event.dedup_key)
  end,

  test_implement_impl_failure_replay_skips_before_ready_gate = function()
    local event = ready()
    mock_issue_implement({ "fkst-dev:impl-failed" }, {
      core.impl_failure_marker(event.proposal_id, event.dedup_key, "codex-failed"),
    })

    local result = run_implement(event, opts("implement-impl-failure-replay"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 0)
    t.eq(count_calls("codex exec"), 0)
    t.eq(count_calls("git worktree list"), 0)
    t.eq(count_calls("git -C"), 0)
  end,

  test_implement_impl_failure_marker_skips_before_label_gate = function()
    local event = ready()
    mock_issue_implement({ "fkst-dev:thinking" }, {
      core.impl_failure_marker(event.proposal_id, event.dedup_key, "codex-failed"),
    })

    local result = run_implement(event, opts("implement-impl-failure-marker-replay"))
    t.eq(result.exit_code, 1)
    t.eq(#result.raises, 0)
    t.eq(count_calls("codex exec"), 0)
    t.eq(count_calls("git worktree list"), 0)
    t.eq(count_calls("git -C"), 0)
  end,

  test_implement_crash_before_marker_reuses_existing_branch_commit = function()
    local event = ready()
    local branch = deterministic_branch_for(event)
    mock_issue_implement({ "fkst-dev:ready" })
    mock_existing_implement_branch("def456")
    t.mock_command('printf %s "$FKST_RUNTIME_ROOT"', {
      stdout = "/tmp/fkst-packages-test/github-devloop/runtime",
      stderr = "",
      exit_code = 0,
    })
    mock_existing_empty_implement_worktree_reuse(nil, branch, "1")
    mock_implement_codex()
    mock_git_status("")
    mock_branch_diff_paths("packages/github-devloop/core.lua\n")
    t.mock_command("rev-list --count", {
      stdout = "1\n",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command("rev-parse --verify refs/heads/", {
      stdout = "def456\n",
      stderr = "",
      exit_code = 0,
    })

    local result = run_implement(event, opts("implement-existing-branch-reuse"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 4)
    t.eq(count_issue_comment_raises(result.raises), 3)
    assert_implement_attempt(result.raises, event)
    assert_worktree_ready_state(result.raises, event)
    t.eq(find_label_with_added(result.raises, "fkst-dev:implementing").payload.add_labels[1], "fkst-dev:implementing")
    local comment = find_comment_with(result.raises, "fkst:github-devloop:implementing:v1").payload.body
    local fact = m_facts.implementing_fact(core, { comment }, event.proposal_id, event.dedup_key)
    t.eq(fact.branch, branch)
    t.eq(fact.head_sha, "def456")
    t.eq(count_calls("git worktree add"), 0)
    t.eq(count_calls("codex exec"), 1)
    t.eq(count_calls("merge --no-edit 'abc123'"), 1)
    t.eq(count_calls("status --porcelain"), 1)
    t.eq(count_calls("impl-failed"), 0)
  end,

  test_implement_existing_worktree_for_other_issue_does_not_affect_fresh_attempt = function()
    local event = ready({
      proposal_id = "github-devloop/issue/owner/repo/4",
      dedup_key = "ready/consensus-github-devloop/issue/owner/repo/4/2026-06-03T01-02-03Z",
      source_ref = {
        kind = "external",
        ref = "owner/repo#issue/4",
      },
    })
    local branch = deterministic_branch_for(event)
    mock_issue_implement({ "fkst-dev:ready" }, {
      core.state_marker(event.proposal_id, "ready", default_marker_version),
    }, { number = 4 })
    mock_existing_devloop_worktree("owner-repo-42")
    mock_fresh_implement_worktree({ issue_number = 4, impl_version = event.dedup_key })
    mock_implement_codex()
    mock_git_status(" M packages/github-devloop/departments/implement/main.lua\n")
    mock_git_commit("def456", branch)

    local result = run_implement(event, opts("implement-boundary-worktree"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 4)
    assert_implement_attempt(result.raises, event)
    assert_worktree_ready_state(result.raises, event)
    t.eq(count_calls("git worktree list"), 0)
    t.eq(count_calls("codex exec"), 1)
  end,

  test_implement_empty_git_status_marks_impl_failed_with_failure_marker = function()
    local event = ready()
    mock_issue_implement({ "fkst-dev:ready" })
    mock_fresh_implement_worktree()
    mock_implement_codex(0, "No files needed changes.")
    mock_git_status("")
    t.mock_command("rev-list --count", {
      stdout = "0\n",
      stderr = "",
      exit_code = 0,
    })

    local result = run_implement(event, opts("implement-no-changes"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 5)
    assert_implement_attempt(result.raises, event)
    assert_worktree_ready_state(result.raises, event)
    t.eq(find_label_with_added(result.raises, "fkst-dev:impl-failed").payload.add_labels[1], "fkst-dev:impl-failed")
    local comment_raise = find_comment_with(result.raises, "fkst:github-devloop:impl-failure:v1")
    t.is_true(comment_raise.payload.body:find("github-devloop implementation failed: no-changes", 1, true) ~= nil)
    t.is_true(comment_raise.payload.body:find("No files needed changes.", 1, true) ~= nil)
  end,

  test_implement_clean_worktree_with_branch_ahead_marks_implementing = function()
    local event = ready()
    local branch = deterministic_branch_for(event)
    mock_issue_implement({ "fkst-dev:ready" })
    mock_fresh_implement_worktree()
    mock_implement_codex(0, "Committed implementation directly.")
    mock_git_status("")
    mock_branch_diff_paths("packages/github-devloop/core.lua\n")
    t.mock_command("rev-list --count", {
      stdout = "1\n",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command("rev-parse --verify refs/heads/", {
      stdout = "def456\n",
      stderr = "",
      exit_code = 0,
    })

    local result = run_implement(event, opts("implement-clean-ahead"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 4)
    assert_implement_attempt(result.raises, event)
    assert_worktree_ready_state(result.raises, event)
    t.eq(find_label_with_added(result.raises, "fkst-dev:implementing").payload.add_labels[1], "fkst-dev:implementing")
    local comment = find_comment_with(result.raises, "fkst:github-devloop:implementing:v1").payload.body
    local fact = m_facts.implementing_fact(core, { comment }, event.proposal_id, event.dedup_key)
    t.eq(fact.branch, branch)
    t.eq(fact.head_sha, "def456")
    t.eq(count_calls("impl-failed"), 0)
  end,

  test_implement_existing_empty_branch_still_marks_no_changes_failed = function()
    local event = ready()
    mock_issue_implement({ "fkst-dev:ready" })
    mock_existing_empty_implement_worktree()
    mock_implement_codex(0, "No files needed changes.")
    mock_git_status("")
    t.mock_command("rev-list --count", {
      stdout = "0\n",
      stderr = "",
      exit_code = 0,
    })

    local result = run_implement(event, opts("implement-existing-empty-branch-no-changes"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 5)
    assert_implement_attempt(result.raises, event)
    assert_worktree_ready_state(result.raises, event)
    t.eq(find_label_with_added(result.raises, "fkst-dev:impl-failed").payload.add_labels[1], "fkst-dev:impl-failed")
    local comment_raise = find_comment_with(result.raises, "fkst:github-devloop:impl-failure:v1")
    t.is_true(comment_raise.payload.body:find("github-devloop implementation failed: no-changes", 1, true) ~= nil)
    t.eq(count_calls("git worktree add"), 1)
    t.eq(count_calls("codex exec"), 1)
  end,

  test_implement_existing_empty_worktree_reuses_and_converges_when_codex_commits = function()
    local event = ready()
    local branch = deterministic_branch_for(event)
    mock_issue_implement({ "fkst-dev:ready" })
    local worktree = mock_existing_empty_implement_worktree_reuse(nil, branch, "1")
    mock_implement_codex(0, "Committed implementation directly.")
    mock_git_status("")
    mock_branch_diff_paths("packages/github-devloop/core.lua\n")
    t.mock_command("rev-list --count", {
      stdout = "1\n",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command("rev-parse --verify refs/heads/", {
      stdout = "def456\n",
      stderr = "",
      exit_code = 0,
    })

    local result = run_implement(event, opts("implement-existing-worktree-reuse"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 4)
    assert_implement_attempt(result.raises, event)
    assert_worktree_ready_state(result.raises, event)
    t.eq(find_label_with_added(result.raises, "fkst-dev:implementing").payload.add_labels[1], "fkst-dev:implementing")
    local comment = find_comment_with(result.raises, "fkst:github-devloop:implementing:v1").payload.body
    local fact = m_facts.implementing_fact(core, { comment }, event.proposal_id, event.dedup_key)
    t.eq(fact.branch, branch)
    t.eq(fact.head_sha, "def456")
    t.is_true(comment:find(worktree, 1, true) ~= nil)
    t.eq(count_calls("git worktree list --porcelain"), 1)
    t.eq(count_calls("git worktree add"), 0)
    t.eq(count_calls("codex exec"), 1)
  end,

  test_implement_reused_worktree_is_reset_and_cleaned_before_merge = function()
    local event = ready()
    local branch = deterministic_branch_for(event)
    mock_issue_implement({ "fkst-dev:ready" })
    local worktree = mock_existing_dirty_implement_worktree_reuse(nil, branch, "1")
    mock_implement_codex(0, "Committed implementation directly.")
    mock_git_status("")
    mock_branch_diff_paths("packages/github-devloop/core.lua\n")
    t.mock_command("rev-list --count", {
      stdout = "1\n",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command("rev-parse --verify refs/heads/", {
      stdout = "def456\n",
      stderr = "",
      exit_code = 0,
    })

    local result = run_implement(event, opts("implement-dirty-worktree-reuse"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 4)
    assert_implement_attempt(result.raises, event)
    assert_worktree_ready_state(result.raises, event)
    t.eq(count_calls("reset --hard"), 1)
    t.eq(count_calls("clean -fd"), 1)
    t.eq(count_calls("merge --no-edit 'abc123'"), 1)

    local reset_before_merge = false
    local reset_seen = false
    for _, call in ipairs(t.command_calls()) do
      if gh_argv.argv_contains(call, { "git", "-C", worktree, "reset", "--hard" }) then
        reset_seen = true
      elseif gh_argv.argv_contains(call, { "git", "-C", worktree, "merge", "--no-edit", "abc123" }) then
        reset_before_merge = reset_seen
      end
    end
    t.eq(reset_before_merge, true)
  end,

  test_implement_ignores_existing_worktree_outside_current_runtime_root = function()
    local event = ready()
    local branch = deterministic_branch_for(event)
    local runtime = "/tmp/fkst-packages-test/github-devloop/runtime"
    mock_issue_implement({ "fkst-dev:ready" })
    mock_outside_runtime_implement_worktree_rebuild(runtime, branch)
    mock_implement_codex(0, "Committed implementation directly.")
    mock_git_status("")
    mock_branch_diff_paths("packages/github-devloop/core.lua\n")
    t.mock_command("rev-list --count", {
      stdout = "1\n",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command("rev-parse --verify refs/heads/", {
      stdout = "def456\n",
      stderr = "",
      exit_code = 0,
    })

    local result = run_implement(event, opts("implement-ignore-outside-runtime-worktree"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 4)
    assert_implement_attempt(result.raises, event)
    assert_worktree_ready_state(result.raises, event)
    t.eq(count_calls("git worktree add"), 1)
    -- 2 = removing the one non-current-runtime stale worktree, plus the idempotent
    -- force-clean of the target path before `git worktree add` (#677).
    t.eq(count_calls("git worktree remove --force"), 2)
    t.eq(count_calls("reset --hard"), 1)
    t.eq(count_calls("clean -fd"), 1)

    local codex_used_current_runtime = false
    for _, call in ipairs(t.command_calls()) do
      if call.rendered:find("codex exec", 1, true) ~= nil
        and call.rendered:find(runtime .. "/worktrees/devloop-owner-repo-42-", 1, true) ~= nil then
        codex_used_current_runtime = true
      end
    end
    t.eq(codex_used_current_runtime, true)
  end,

  test_implement_removes_all_existing_worktrees_outside_current_runtime_root = function()
    local event = ready()
    local branch = deterministic_branch_for(event)
    mock_issue_implement({ "fkst-dev:ready" })
    mock_multiple_outside_runtime_implement_worktrees_rebuild("/tmp/fkst-packages-test/github-devloop/runtime", branch)
    mock_implement_codex(0, "Committed implementation directly.")
    mock_git_status("")
    mock_branch_diff_paths("packages/github-devloop/core.lua\n")
    t.mock_command("rev-list --count", {
      stdout = "1\n",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command("rev-parse --verify refs/heads/", {
      stdout = "def456\n",
      stderr = "",
      exit_code = 0,
    })

    local result = run_implement(event, opts("implement-remove-all-outside-runtime-worktrees"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 4)
    assert_implement_attempt(result.raises, event)
    assert_worktree_ready_state(result.raises, event)
    -- 3 = removing the two non-current-runtime stale worktrees, plus the idempotent
    -- force-clean of the target path before `git worktree add` (#677).
    t.eq(count_calls("git worktree remove --force"), 3)
    t.eq(count_calls("git worktree add"), 1)
    t.eq(count_calls("reset --hard"), 1)
    t.eq(count_calls("clean -fd"), 1)
  end,

  test_implement_marker_present_skips_idempotently = function()
    local event = ready()
    local branch = deterministic_branch_for(event)
    mock_issue_implement({ "fkst-dev:implementing" }, {
      core.state_marker(event.proposal_id, "implementing", event.dedup_key),
      m_builders.pr_link_marker(core, event.proposal_id, 7, branch, event.dedup_key, "dev"),
    })

    local result = run_implement(event, opts("implement-idempotent"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 0)
    t.eq(count_calls("codex exec"), 0)
    t.eq(count_calls("git -C"), 0)
  end,

  test_implement_implementing_marker_skips_before_ready_gate = function()
    local event = ready()
    local branch = deterministic_branch_for(event)
    mock_issue_implement({ "fkst-dev:implementing" }, {
      core.state_marker(event.proposal_id, "implementing", event.dedup_key),
      m_builders.pr_link_marker(core, event.proposal_id, 7, branch, event.dedup_key, "dev"),
    })

    local result = run_implement(event, opts("implement-implementing-marker-replay"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 0)
    t.eq(count_calls("codex exec"), 0)
    t.eq(count_calls("git worktree list"), 0)
    t.eq(count_calls("git -C"), 0)
  end,

  test_implementing_state_with_live_attempt_skips_redelivery = function()
    local event = ready()
    local run_opts = opts("implement-combined-marker-redelivery")
    local exec_ref = core.implement_exec_ref(event.proposal_id, event.dedup_key)
    codex_status.seed_implement_codex_run(run_opts, event.proposal_id, event.dedup_key)
    mock_issue_implement({ "fkst-dev:implementing" }, {
      core.state_marker(event.proposal_id, "implementing", event.dedup_key),
      core.implement_attempt_marker(event.proposal_id, event.dedup_key, 1, now(), exec_ref),
    })

    local result = run_implement(event, run_opts)
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 0)
    t.eq(count_calls("codex exec"), 0)
    t.eq(count_calls("git worktree list"), 0)
    t.eq(count_calls("git -C"), 0)
  end,

  test_implement_ready_with_live_attempt_without_visible_markers_skips_redelivery = function()
    local event = ready()
    local run_opts = opts("implement-live-run-no-marker-redelivery")
    local branch = deterministic_branch_for(event)
    codex_status.seed_implement_codex_run(run_opts, event.proposal_id, event.dedup_key)
    mock_issue_implement({ "fkst-dev:ready" }, {
      core.state_marker(event.proposal_id, "ready", event.dedup_key),
    })
    mock_existing_dirty_implement_worktree_reuse(nil, branch, "1")
    mock_implement_codex(0, "duplicate implementation should not spawn")

    local result = run_implement(event, run_opts)
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 0)
    t.eq(count_calls("codex exec"), 0)
    t.eq(count_calls("reset --hard"), 0)
    t.eq(count_calls("clean -fd"), 0)
  end,

  test_implement_skips_foreign_proposal_before_gh_view = function()
    local result = run_implement(ready({
      proposal_id = "autochrono/issue/owner/repo/42",
      dedup_key = "ready/autochrono/issue/owner/repo/42",
    }), opts("implement-foreign"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 0)
  end,

  test_implement_retries_until_ready_label_is_visible = function()
    mock_issue_implement({ "fkst-dev:thinking" })

    local pending = run_implement(ready(), opts("implement-ready-pending"))
    t.eq(pending.exit_code, 1)
    t.eq(#pending.raises, 0)
    t.eq(count_calls("codex exec"), 0)
    t.eq(count_calls("git -C"), 0)

    mock_issue_implement({ "fkst-dev:ready" })
    local branch = deterministic_branch_for(ready())
    mock_fresh_implement_worktree("/tmp/fkst-packages-test/github-devloop/runtime")
    mock_implement_codex(0, "implemented")
    mock_git_status(" M packages/github-devloop/core.lua\n")
    mock_git_commit("def456", branch)
    mock_issue_implement({ "fkst-dev:ready" })

    local visible = run_implement(ready(), opts("implement-ready-visible"))
    t.eq(visible.exit_code, 0)
    t.eq(#visible.raises, 4)
    assert_implement_attempt(visible.raises, ready())
    assert_worktree_ready_state(visible.raises, ready())
    t.eq(find_label_with_added(visible.raises, "fkst-dev:implementing").payload.add_labels[1], "fkst-dev:implementing")
    t.eq(count_calls("codex exec"), 1)
  end,

  test_implement_rejects_unverified_ready_hand_off_before_marker_visibility = function()
    local event = ready()
    event.ready_hand_off = {
      kind = "own-state-marker",
      proposal_id = event.proposal_id,
      state = "ready",
      marker_version = event.dedup_key,
      event_version = event.dedup_key,
      stage_rank = core.stage_rank("ready"),
      effects = "result-marker,ready-label,devloop-ready",
    }
    mock_issue_implement_raw({ "fkst-dev:ready" }, {})

    local result = run_implement(event, opts("implement-ready-hand-off-marker-pending"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 0)
    t.eq(count_calls("codex exec"), 0)
    t.eq(count_calls("git -C"), 0)
  end,

  test_implement_ready_hand_off_rechecks_state_before_publish = function()
    local event = ready()
    event.ready_hand_off = {
      kind = "own-state-marker",
      proposal_id = event.proposal_id,
      state = "ready",
      marker_version = event.dedup_key,
      event_version = event.dedup_key,
      stage_rank = core.stage_rank("ready"),
      effects = "result-marker,ready-label,devloop-ready",
      comment_id = "IC_ready_stale",
    }
    mock_issue_implement_raw({ "fkst-dev:ready" }, {})
    t.mock_command("gh api --method GET 'repos/owner/repo/issues/comments/IC_ready_stale'", {
      stdout = '{"body":"' .. json_string(core.state_marker(event.proposal_id, "ready", event.ready_hand_off.marker_version, "result-marker,ready-label,devloop-ready")) .. '","user":{"login":"fkst-test-bot"}}\n',
      stderr = "",
      exit_code = 0,
    })
    mock_issue_implement_raw({ "fkst-dev:fixing" }, {
      core.state_marker(event.proposal_id, "fixing", event.dedup_key),
    })

    local result = run_implement(event, opts("implement-ready-hand-off-stale-at-write"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 0)
    t.eq(find_raise(result.raises, "github-proxy.github_issue_label_request"), nil)
    t.eq(count_calls("codex exec"), 0)
    t.eq(count_calls("git worktree list"), 0)
    t.eq(count_calls("git -C"), 0)
  end,

  test_implement_replay_with_ready_hand_off_requires_visible_marker = function()
    local event = ready()
    event.ready_hand_off = {
      kind = "own-state-marker",
      proposal_id = event.proposal_id,
      state = "ready",
      marker_version = event.dedup_key,
      event_version = event.dedup_key,
      stage_rank = core.stage_rank("ready"),
      effects = "result-marker,ready-label,devloop-ready",
      comment_id = "IC_ready_missing",
    }
    mock_issue_implement_raw({ "fkst-dev:ready" }, {})
    t.mock_command("gh api --method GET 'repos/owner/repo/issues/comments/IC_ready_missing'", {
      stdout = "",
      stderr = "not found",
      exit_code = 1,
    })

    local result = run_implement(event, opts("implement-replay-hand-off-marker-pending"))
    t.eq(result.exit_code, 1)
    t.eq(#result.raises, 0)
    t.eq(count_calls("codex exec"), 0)
    t.eq(count_calls("git -C"), 0)
  end,

  test_implement_replay_without_ready_hand_off_requires_visible_marker = function()
    local event = ready()
    mock_issue_implement_raw({ "fkst-dev:ready" }, {})

    local result = run_implement(event, opts("implement-replay-no-hand-off-marker-pending"))
    t.eq(result.exit_code, 1)
    t.eq(#result.raises, 0)
    t.eq(count_calls("codex exec"), 0)
    t.eq(count_calls("git -C"), 0)
  end,

  test_implement_accepts_verified_durable_ready_hand_off_before_marker_visibility = function()
    local event = ready()
    local branch = deterministic_branch_for(event)
    event.ready_hand_off = {
      kind = "own-state-marker",
      proposal_id = event.proposal_id,
      state = "ready",
      marker_version = "consensus:github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z",
      event_version = event.dedup_key,
      stage_rank = core.stage_rank("ready"),
      effects = "result-marker,ready-label,devloop-ready",
      comment_id = "IC_ready_1",
    }
    mock_issue_implement_raw({ "fkst-dev:ready" }, {})
    for _ = 1, 2 do
      t.mock_command("gh api --method GET 'repos/owner/repo/issues/comments/IC_ready_1'", {
        stdout = '{"body":"' .. json_string(core.state_marker(event.proposal_id, "ready", event.ready_hand_off.marker_version, "result-marker,ready-label,devloop-ready")) .. '","user":{"login":"fkst-test-bot"}}\n',
        stderr = "",
        exit_code = 0,
      })
    end
    mock_fresh_implement_worktree("/tmp/fkst-packages-test/github-devloop/runtime")
    mock_implement_codex(0, "implemented")
    mock_git_status(" M packages/github-devloop/core.lua\n")
    mock_git_commit("def456", branch)
    mock_issue_implement_raw({ "fkst-dev:ready" }, {})
    mock_issue_implement_raw({ "fkst-dev:ready" }, {})

    local result = run_implement(event, opts("implement-durable-ready-hand-off-marker-pending"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 4)
    assert_worktree_ready_state(result.raises, event)
    t.eq(find_label_with_added(result.raises, "fkst-dev:implementing").payload.add_labels[1], "fkst-dev:implementing")
    t.eq(count_calls("repos/owner/repo/issues/comments/IC_ready_1"), 1)
    t.eq(count_calls("codex exec"), 1)
  end,

  test_implement_accepts_ready_hand_off_with_alternate_effects_before_marker_visibility = function()
    local event = ready()
    local branch = deterministic_branch_for(event)
    event.ready_hand_off = {
      kind = "own-state-marker",
      proposal_id = event.proposal_id,
      state = "ready",
      marker_version = "consensus:github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z",
      event_version = event.dedup_key,
      stage_rank = core.stage_rank("ready"),
      effects = "alternate-ready-producer",
      comment_id = "IC_ready_alternate_effects",
    }
    mock_issue_implement_raw({ "fkst-dev:ready" }, {})
    for _ = 1, 2 do
      t.mock_command("gh api --method GET 'repos/owner/repo/issues/comments/IC_ready_alternate_effects'", {
        stdout = '{"body":"' .. json_string(core.state_marker(event.proposal_id, "ready", event.ready_hand_off.marker_version, "alternate-ready-producer")) .. '","user":{"login":"fkst-test-bot"}}\n',
        stderr = "",
        exit_code = 0,
      })
    end
    mock_fresh_implement_worktree("/tmp/fkst-packages-test/github-devloop/runtime")
    mock_implement_codex(0, "implemented")
    mock_git_status(" M packages/github-devloop/core.lua\n")
    mock_git_commit("def456", branch)
    mock_issue_implement_raw({ "fkst-dev:ready" }, {})
    mock_issue_implement_raw({ "fkst-dev:ready" }, {})

    local result = run_implement(event, opts("implement-ready-hand-off-alternate-effects"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 4)
    assert_worktree_ready_state(result.raises, event)
    t.eq(find_label_with_added(result.raises, "fkst-dev:implementing").payload.add_labels[1], "fkst-dev:implementing")
    t.eq(count_calls("repos/owner/repo/issues/comments/IC_ready_alternate_effects"), 1)
    t.eq(count_calls("codex exec"), 1)
  end,

  test_implement_rejects_ready_hand_off_when_comment_marker_state_is_not_ready = function()
    local event = ready()
    event.ready_hand_off = {
      kind = "own-state-marker",
      proposal_id = event.proposal_id,
      state = "ready",
      marker_version = event.dedup_key,
      event_version = event.dedup_key,
      stage_rank = core.stage_rank("ready"),
      effects = "alternate-ready-producer",
      comment_id = "IC_ready_wrong_state",
    }
    mock_issue_implement_raw({ "fkst-dev:ready" }, {})
    t.mock_command("gh api --method GET 'repos/owner/repo/issues/comments/IC_ready_wrong_state'", {
      stdout = '{"body":"' .. json_string(core.state_marker(event.proposal_id, "reviewing", event.ready_hand_off.marker_version, "alternate-ready-producer")) .. '","user":{"login":"fkst-test-bot"}}\n',
      stderr = "",
      exit_code = 0,
    })

    local result = run_implement(event, opts("implement-ready-hand-off-wrong-state"))
    t.eq(result.exit_code, 1)
    t.eq(#result.raises, 0)
    t.eq(count_calls("codex exec"), 0)
    t.eq(count_calls("git -C"), 0)
  end,

  test_implement_redrive_hand_off_uses_original_ready_marker_version = function()
    local original_version = "consensus:github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"
    local redrive = payloads_builders.build_devloop_ready_payload(core, {
      proposal_id = ready().proposal_id,
      dedup_key = original_version .. "/redrive/ready/2",
      source_ref = source_ref(),
      effect_version = original_version,
      include_ready_hand_off = true,
      ready_comment_id = "IC_ready_original",
    })
    local branch = deterministic_branch_for(redrive)
    local marker = core.state_marker(redrive.proposal_id, "ready", original_version, "result-marker,ready-label,devloop-ready")
    mock_issue_implement_raw({ "fkst-dev:ready" }, {})
    t.mock_command("gh api --method GET 'repos/owner/repo/issues/comments/IC_ready_original'", {
      stdout = '{"body":"' .. json_string(marker) .. '","user":{"login":"fkst-test-bot"}}\n',
      stderr = "",
      exit_code = 0,
    })
    mock_fresh_implement_worktree({ impl_version = redrive.dedup_key })
    mock_implement_codex(0, "implemented")
    mock_git_status(" M packages/github-devloop/core.lua\n")
    mock_git_commit("def456", branch)
    mock_issue_implement_raw({ "fkst-dev:ready" }, {})
    mock_issue_implement_raw({ "fkst-dev:ready" }, {})

    local result = run_implement(redrive, opts("implement-ready-redrive-original-hand-off"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 4)
    assert_implement_attempt(result.raises, redrive)
    assert_worktree_ready_state(result.raises, redrive)
    t.eq(find_label_with_added(result.raises, "fkst-dev:implementing").payload.add_labels[1], "fkst-dev:implementing")
    t.eq(count_calls("repos/owner/repo/issues/comments/IC_ready_original"), 1)
    t.eq(count_calls("codex exec"), 1)
  end,

  test_implement_retry_ignores_ready_hand_off_before_marker_visibility = function()
    local event = ready({
      impl_retry_attempt = 2,
      ready_hand_off = {
        kind = "own-state-marker",
        proposal_id = ready().proposal_id,
        state = "ready",
        marker_version = ready().dedup_key,
        event_version = ready().dedup_key,
        stage_rank = core.stage_rank("ready"),
        effects = "result-marker,ready-label,devloop-ready",
      },
    })
    mock_issue_implement_raw({ "fkst-dev:ready" }, {})

    local result = run_implement(event, opts("implement-retry-hand-off-marker-pending"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 0)
    t.eq(count_calls("codex exec"), 0)
    t.eq(count_calls("git -C"), 0)
  end,

  test_implement_implementing_label_without_marker_reruns = function()
    mock_issue_implement({ "fkst-dev:implementing" })

    local result = run_implement(ready(), opts("implement-label-without-marker"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 0)
    t.eq(count_calls("codex exec"), 0)
  end,

  test_implement_impl_failed_label_without_marker_reruns_and_records_marker = function()
    local event = ready()
    mock_issue_implement({ "fkst-dev:impl-failed" })

    local result = run_implement(event, opts("implement-impl-failed-label-without-marker"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 0)
    t.eq(count_calls("codex exec"), 0)
    t.eq(count_calls("status --porcelain"), 0)
  end,

  test_implement_skips_visible_terminal_states = function()
    local event = ready()
    mock_issue_implement({ "fkst-dev:impl-failed" }, {
      core.state_marker(event.proposal_id, "impl-failed", event.dedup_key),
    })
    local failed_recorded = run_implement(event, opts("implement-already-impl-failed-recorded"))
    t.eq(failed_recorded.exit_code, 0)
    t.eq(#failed_recorded.raises, 0)

    mock_issue_implement({ "fkst-dev:blocked" }, { core.state_marker(event.proposal_id, "blocked", default_marker_version) })
    local blocked = run_implement(event, opts("implement-already-blocked"))
    t.eq(blocked.exit_code, 0)
    t.eq(#blocked.raises, 0)

    t.eq(count_calls("codex exec"), 0)
    t.eq(count_calls("git -C"), 0)
  end,

  test_implement_issue_view_failure_errors_for_retry = function()
    mock_issue_view_failure("--json title,body,labels,comments,state,author", "forced implement failure")

    local result = run_implement(ready(), opts("implement-view-failure"))
    t.eq(result.exit_code, 1)
    t.eq(#result.raises, 0)
    t.eq(count_calls("codex exec"), 0)
  end
}
