local devloop_base = require("devloop.base")
local h = require("tests.devloop_helpers")
local payloads_builders = require("devloop.payloads.builders")
local m_facts = require("devloop.markers.facts")
local t = h.t
local core = h.core
local opts = h.opts
local issue = h.issue
local reached = h.reached
local run_observe = h.run_observe
local run_implement = h.run_implement
local mock_issue_state = h.mock_issue_state
local mock_issue_implement_raw = h.mock_issue_implement_raw
local mock_existing_empty_implement_worktree = h.mock_existing_empty_implement_worktree
local mock_implement_codex = h.mock_implement_codex
local mock_git_status = h.mock_git_status
local mock_git_commit = h.mock_git_commit
local render_comment = h.render_comment
local json_string = h.json_string
local find_raise = h.find_raise
local entity_read_mocks = require("tests.entity_read_mock_helpers")
local m_builders = require("devloop.markers.builders")

local function mock_linked_pr_state(comments, state)
  local rendered_comments = {}
  for _, comment in ipairs(comments or {}) do
    table.insert(rendered_comments, render_comment(comment))
  end
  entity_read_mocks.mock_pr_view_raw_selector(t, {}, entity_read_mocks.pr_origin_selector, {
    stdout = string.format(
      '{"headRefName":"devloop-owner-repo-42-01HY","headRefOid":"def456","baseRefName":"dev","state":"%s","updatedAt":"2026-06-03T02:03:04Z","comments":[%s]}\n',
      json_string(state or "OPEN"),
      table.concat(rendered_comments, ",")
    ),
    stderr = "",
    exit_code = 0,
  })
end

local function trusted_command(id)
  return {
    id = id or "IC_reimplement_1",
    body = "fkst: reimplement",
    author_login = "fkst-test-bot",
    created_at = "2026-06-04T03:00:00Z",
  }
end

local function forged_command()
  local command = trusted_command("IC_reimplement_forged")
  command.author_login = "mallory"
  return command
end

local function impl_failed_comments(event, reason, attempt, command)
  local version = payloads_builders.build_devloop_ready_payload(core, event).dedup_key
  local comments = {
    core.state_marker(event.proposal_id, "impl-failed", version),
    core.impl_failure_marker(event.proposal_id, version, reason or "codex-failed", attempt),
  }
  if command ~= nil then
    table.insert(comments, command)
  end
  return comments
end

local function find_worktree_ready_comment(raises)
  return find_raise(raises, "github-proxy.github_issue_comment_request", function(payload)
    return tostring(payload.body or ""):find("github-devloop implementation worktree ready", 1, true) ~= nil
  end)
end

return {
  test_observe_autoretries_codex_failed_once = function()
    local event = reached()
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:impl-failed" }, "OPEN", impl_failed_comments(event, "codex-failed", 1))

    local result = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:impl-failed" } }), opts("observe-impl-failed-retry"))
    t.eq(result.exit_code, 0)
    local ready = find_raise(result.raises, "devloop_ready")
    t.is_true(ready ~= nil)
    t.eq(ready.payload.dedup_key, payloads_builders.build_devloop_ready_payload(core, event).dedup_key)
    t.eq(ready.payload.impl_retry_attempt, 2)
  end,

  test_observe_autoretries_non_descendant_head_once = function()
    local event = reached()
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:impl-failed" }, "OPEN", impl_failed_comments(event, "non-descendant-head", 1))

    local result = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:impl-failed" } }), opts("observe-non-descendant-head-retry"))
    t.eq(result.exit_code, 0)
    local ready = find_raise(result.raises, "devloop_ready")
    t.is_true(ready ~= nil)
    t.eq(ready.payload.dedup_key, payloads_builders.build_devloop_ready_payload(core, event).dedup_key)
    t.eq(ready.payload.impl_retry_attempt, 2)
    t.eq(find_raise(result.raises, "github-proxy.github_issue_comment_request"), nil)
  end,

  test_observe_stops_after_bounded_codex_failed_retry = function()
    local event = reached()
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:impl-failed" }, "OPEN", impl_failed_comments(event, "codex-failed", 2))

    local result = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:impl-failed" } }), opts("observe-impl-failed-limit"))
    t.eq(result.exit_code, 0)
    t.eq(find_raise(result.raises, "devloop_ready"), nil)
  end,

  test_observe_stops_after_bounded_non_descendant_head_retry = function()
    local event = reached()
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:impl-failed" }, "OPEN", impl_failed_comments(event, "non-descendant-head", 2))

    local result = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:impl-failed" } }), opts("observe-non-descendant-head-limit"))
    t.eq(result.exit_code, 0)
    t.eq(find_raise(result.raises, "devloop_ready"), nil)
  end,

  test_reimplement_command_reenters_after_retry_limit = function()
    local event = reached()
    local command = trusted_command()
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:impl-failed" }, "OPEN", impl_failed_comments(event, "codex-failed", 2, command))

    local result = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:impl-failed" } }), opts("operator-reimplement"))
    t.eq(result.exit_code, 0)
    local ready = find_raise(result.raises, "devloop_ready")
    t.is_true(ready ~= nil)
    t.eq(ready.payload.dedup_key, payloads_builders.build_devloop_ready_payload(core, event).dedup_key)
    t.eq(ready.payload.impl_retry_attempt, 3)
    local response = find_raise(result.raises, "github-proxy.github_issue_comment_request")
    t.is_true(response.payload.body:find("operator command accepted: reimplement", 1, true) ~= nil)
    t.is_true(response.payload.body:find('command="reimplement"', 1, true) ~= nil)
  end,

  test_forged_reimplement_command_is_ignored = function()
    local event = reached()
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:impl-failed" }, "OPEN", impl_failed_comments(event, "codex-failed", 2, forged_command()))

    local result = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:impl-failed" } }), opts("operator-reimplement-forged"))
    t.eq(result.exit_code, 0)
    t.eq(find_raise(result.raises, "devloop_ready"), nil)
  end,

  test_reimplement_command_reenters_blocked_open_pr_from_issue = function()
    local event = reached()
    local ready_version = payloads_builders.build_devloop_ready_payload(core, event).dedup_key
    local blocked_version = ready_version .. "/review-loop/3"
    local command = trusted_command("IC_reimplement_blocked")
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:blocked" }, "OPEN", {
      m_builders.pr_link_marker(core, event.proposal_id, 7, "devloop-owner-repo-42-01HY", ready_version, "dev"),
      core.state_marker(event.proposal_id, "blocked", blocked_version),
      command,
    })
    mock_linked_pr_state({}, "OPEN")

    local result = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:blocked" } }), opts("operator-reimplement-blocked-open-pr"))
    t.eq(result.exit_code, 0)
    local ready = find_raise(result.raises, "devloop_ready")
    t.is_true(ready ~= nil)
    t.eq(ready.payload.proposal_id, event.proposal_id)
    t.eq(ready.payload.dedup_key, ready_version)
    t.eq(ready.payload.impl_retry_attempt, 2)
    t.eq(ready.payload.operator_reentry.command, "reimplement")
    t.eq(ready.payload.operator_reentry.from_state, "blocked")
    t.eq(ready.payload.operator_reentry.state_version, blocked_version)
    t.eq(ready.payload.operator_reentry.impl_version, ready_version)
    t.eq(ready.payload.operator_reentry.pr_number, 7)
    local response = find_raise(result.raises, "github-proxy.github_issue_comment_request")
    t.is_true(response.payload.body:find("operator command accepted: reimplement", 1, true) ~= nil)
  end,

  test_reimplement_command_refuses_blocked_without_open_linked_pr = function()
    local event = reached()
    local ready_version = payloads_builders.build_devloop_ready_payload(core, event).dedup_key
    local command = trusted_command("IC_reimplement_blocked_unlinked")
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:blocked" }, "OPEN", {
      core.state_marker(event.proposal_id, "blocked", ready_version .. "/review-loop/3"),
      command,
    })

    local result = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:blocked" } }), opts("operator-reimplement-blocked-unlinked"))
    t.eq(result.exit_code, 0)
    t.eq(find_raise(result.raises, "devloop_ready"), nil)
    local response = find_raise(result.raises, "github-proxy.github_issue_comment_request")
    t.is_true(response.payload.body:find("operator command refused", 1, true) ~= nil)
    t.is_true(response.payload.body:find("reimplement requires impl-failed or blocked state with an open linked PR", 1, true) ~= nil)
  end,

  test_retry_implementation_writes_attempt_version = function()
    local event = reached()
    local ready = payloads_builders.build_devloop_ready_payload(core, event)
    ready.impl_retry_attempt = 2
    mock_issue_implement_raw({ "fkst-dev:impl-failed" }, {
      core.state_marker(event.proposal_id, "impl-failed", ready.dedup_key),
      core.impl_failure_marker(event.proposal_id, ready.dedup_key, "codex-failed", 1),
    })
    mock_existing_empty_implement_worktree({
      impl_version = ready.dedup_key .. "/reimplement/2",
    })
    mock_implement_codex(0, "implemented")
    mock_git_status(" M packages/github-devloop/core.lua\n")
    mock_git_commit(nil, devloop_base.implement_branch("owner/repo", "42", ready.dedup_key))
    mock_issue_implement_raw({ "fkst-dev:impl-failed" }, {
      core.state_marker(event.proposal_id, "impl-failed", ready.dedup_key),
      core.impl_failure_marker(event.proposal_id, ready.dedup_key, "codex-failed", 1),
    })
    mock_issue_implement_raw({ "fkst-dev:impl-failed" }, {
      core.state_marker(event.proposal_id, "impl-failed", ready.dedup_key),
      core.impl_failure_marker(event.proposal_id, ready.dedup_key, "codex-failed", 1),
    })

    local result = run_implement(ready, opts("implement-retry-success"))
    t.eq(result.exit_code, 0)
    local comment = find_worktree_ready_comment(result.raises)
    t.is_true(comment ~= nil)
    t.is_true(comment.payload.body:find(core.state_marker(event.proposal_id, "implementing", ready.dedup_key .. "/reimplement/2"), 1, true) ~= nil)
    t.eq(m_facts.implementing_fact(core, { comment.payload.body }, event.proposal_id, ready.dedup_key .. "/reimplement/2"), nil)
  end,

  test_blocked_reimplement_receiver_writes_fresh_attempt_version = function()
    local event = reached()
    local ready = payloads_builders.build_devloop_ready_payload(core, event)
    local blocked_version = ready.dedup_key .. "/review-loop/3"
    ready.impl_retry_attempt = 2
    ready.operator_reentry = {
      command = "reimplement",
      from_state = "blocked",
      pr_number = 7,
      state_version = blocked_version,
      impl_version = ready.dedup_key,
    }
    mock_issue_implement_raw({ "fkst-dev:blocked" }, {
      m_builders.pr_link_marker(core, event.proposal_id, 7, "devloop-owner-repo-42-01HY", ready.dedup_key, "dev"),
      core.state_marker(event.proposal_id, "blocked", blocked_version),
    })
    mock_existing_empty_implement_worktree({
      impl_version = ready.dedup_key .. "/reimplement/2",
    })
    mock_implement_codex(0, "implemented")
    mock_git_status(" M packages/github-devloop/core.lua\n")
    mock_git_commit(nil, devloop_base.implement_branch("owner/repo", "42", ready.dedup_key))
    mock_issue_implement_raw({ "fkst-dev:blocked" }, {
      m_builders.pr_link_marker(core, event.proposal_id, 7, "devloop-owner-repo-42-01HY", ready.dedup_key, "dev"),
      core.state_marker(event.proposal_id, "blocked", blocked_version),
    })
    mock_issue_implement_raw({ "fkst-dev:blocked" }, {
      m_builders.pr_link_marker(core, event.proposal_id, 7, "devloop-owner-repo-42-01HY", ready.dedup_key, "dev"),
      core.state_marker(event.proposal_id, "blocked", blocked_version),
    })

    local result = run_implement(ready, opts("implement-blocked-reimplement-success"))
    t.eq(result.exit_code, 0)
    local comment = find_worktree_ready_comment(result.raises)
    t.is_true(comment ~= nil)
    t.is_true(comment.payload.body:find(core.state_marker(event.proposal_id, "implementing", ready.dedup_key .. "/reimplement/2"), 1, true) ~= nil)
    t.eq(m_facts.implementing_fact(core, { comment.payload.body }, event.proposal_id, ready.dedup_key .. "/reimplement/2"), nil)
  end,
}
