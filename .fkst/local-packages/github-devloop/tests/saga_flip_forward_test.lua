local h = require("tests.devloop_helpers")
local t = h.t
local core = h.core
local opts = h.opts
local ready = h.ready
local run_implement = h.run_implement
local mock_issue_implement = h.mock_issue_implement
local mock_existing_empty_implement_worktree = h.mock_existing_empty_implement_worktree
local mock_existing_implement_branch = h.mock_existing_implement_branch
local mock_git_push = h.mock_git_push
local mock_implement_codex = h.mock_implement_codex
local mock_git_status = h.mock_git_status
local mock_git_commit = h.mock_git_commit
local mock_bot_env = h.mock_bot_env
local mock_write_env = h.mock_write_env
local deterministic_branch_for = h.deterministic_branch_for
local find_raise = h.find_raise
local entity_read_mocks = require("tests.entity_read_mock_helpers")
local m_builders = require("devloop.markers.builders")

local proposal_id = "github-devloop/issue/owner/repo/42"
local pr_proposal_id = "github-devloop/pr/owner/repo/7"
local head_sha = "def456"
local base_sha = "abc123"

local function pr_list_json(branch)
  return '[{"number":7,"head":{"ref":"' .. branch .. '","sha":"' .. head_sha .. '"},"base":{"ref":"dev"},"state":"open"}]\n'
end

local function mock_pr_child_adoptable(branch)
  t.mock_command(core.gh_pr_list_head_base_cmd("owner/repo", branch, "dev"), {
    stdout = pr_list_json(branch),
    stderr = "",
    exit_code = 0,
  })
end

local function mock_remote_implementation_branch(branch)
  t.mock_command("git fetch origin " .. tostring(branch), {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("refs/remotes/origin/" .. tostring(branch) .. "^{commit}", {
    stdout = head_sha .. "\n",
    stderr = "",
    exit_code = 0,
  })
end

local function mock_real_write_mode()
  for _ = 1, 6 do
    mock_write_env("1")
  end
end

local function command_index(needle)
  for index, call in ipairs(t.command_calls()) do
    if tostring(call.rendered or ""):find(needle, 1, true) ~= nil then
      return index
    end
  end
  return nil
end

local function visible_child_comments(event, branch)
  return {
    m_builders.pr_origin_marker(core, event.proposal_id, 42, branch, event.dedup_key, "dev")
      .. "\n" .. core.state_marker(event.proposal_id, "pr-open", event.dedup_key),
  }
end

local function visible_issue_comments(event, branch)
  return {
    core.state_marker(event.proposal_id, "implementing", event.dedup_key),
    m_builders.implementing_marker(core, event.proposal_id, event.dedup_key, branch, head_sha, "dev", base_sha),
    m_builders.pr_delegation_marker(core, event.proposal_id, pr_proposal_id, 7, event.dedup_key, "g1"),
  }
end

local function mock_linked_pr_view(comments, branch)
  entity_read_mocks.mock_pr_view_selector(t, {
    repo = "owner/repo",
    number = 7,
    comments = comments,
    head = branch,
    head_sha = head_sha,
    base_branch = "dev",
    state = "OPEN",
  }, entity_read_mocks.pr_origin_selector)
end

local function find_body_raise(raises, queue, text)
  return find_raise(raises, queue, function(payload)
    return tostring(payload.body or ""):find(text, 1, true) ~= nil
  end)
end

return {
  test_implement_success_visible_child_flips_parent_to_awaiting_pr = function()
    local event = ready()
    local branch = deterministic_branch_for(event)
    mock_issue_implement({ "fkst-dev:implementing" }, visible_issue_comments(event, branch), {
      title = "Implement decision recorder",
    })
    mock_linked_pr_view(visible_child_comments(event, branch), branch)
    mock_pr_child_adoptable(branch)
    mock_remote_implementation_branch(branch)
    mock_existing_implement_branch(head_sha)
    mock_bot_env()
    mock_real_write_mode()

    local result = run_implement(event, opts("saga-flip-visible-child", { FKST_GITHUB_WRITE = "1" }))

    t.eq(result.exit_code, 0)
    local comment = find_body_raise(result.raises, "github-proxy.github_issue_comment_request", 'state="awaiting-pr"')
    t.is_true(comment ~= nil)
    t.is_true(tostring(comment.payload.body):find("fkst:github-devloop:pr-delegation:v1", 1, true) ~= nil)
    t.is_true(tostring(comment.payload.body):find('pr="7"', 1, true) ~= nil)
    local label = find_raise(result.raises, "github-proxy.github_issue_label_request")
    t.is_true(label ~= nil)
    t.eq(label.payload.add_labels[1], "fkst-dev:awaiting-pr")
  end,

  test_implement_success_pre_await_missing_child_start_acks_without_parent_awaiting_pr = function()
    local event = ready()
    local branch = deterministic_branch_for(event)
    mock_issue_implement({ "fkst-dev:ready" }, {
      core.state_marker(event.proposal_id, "ready", event.dedup_key),
    })
    mock_existing_empty_implement_worktree()
    mock_implement_codex(0, "implemented")
    mock_git_status(" M packages/github-devloop/core.lua\n")
    mock_git_commit(head_sha, branch)
    mock_git_push(branch)
    t.mock_command(core.gh_pr_list_head_base_cmd("owner/repo", branch, "dev"), {
      stdout = "[]\n",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command("gh pr create", { stdout = "https://github.example/owner/repo/pull/7\n", stderr = "", exit_code = 0 })
    mock_pr_child_adoptable(branch)
    mock_bot_env()
    mock_real_write_mode()

    local result = run_implement(event, opts("saga-flip-child-start-not-visible", { FKST_GITHUB_WRITE = "1" }))

    t.eq(result.exit_code, 0)
    t.is_true(find_body_raise(result.raises, "github-proxy.github_pr_comment_request", 'state="pr-open"') ~= nil)
    t.is_true(find_body_raise(result.raises, "github-proxy.github_issue_comment_request", "fkst:github-devloop:pr-delegation:v1") ~= nil)
    t.eq(find_body_raise(result.raises, "github-proxy.github_issue_comment_request", 'state="awaiting-pr"'), nil)
    t.eq(find_raise(result.raises, "github-proxy.github_issue_label_request", function(payload)
      return payload.add_labels[1] == "fkst-dev:awaiting-pr"
    end), nil)
    local push_index = command_index("push origin HEAD:refs/heads/" .. branch)
    local create_index = command_index("gh pr create")
    t.is_true(push_index ~= nil)
    t.is_true(create_index ~= nil)
    t.is_true(push_index < create_index)
  end,

  test_implement_liveness_redrive_reaches_awaiting_pr = function()
    local event = ready()
    local branch = deterministic_branch_for(event)
    mock_issue_implement({ "fkst-dev:implementing" }, visible_issue_comments(event, branch), {
      title = "Implement decision recorder",
    })
    mock_linked_pr_view(visible_child_comments(event, branch), branch)
    mock_pr_child_adoptable(branch)
    mock_remote_implementation_branch(branch)
    mock_existing_implement_branch(head_sha)
    mock_bot_env()
    mock_real_write_mode()

    local redriven = run_implement(event, opts("saga-flip-liveness-redrive-to-awaiting-pr", { FKST_GITHUB_WRITE = "1" }))

    t.eq(redriven.exit_code, 0)
    local comment = find_body_raise(redriven.raises, "github-proxy.github_issue_comment_request", 'state="awaiting-pr"')
    t.is_true(comment ~= nil)
    t.is_true(tostring(comment.payload.body):find("fkst:github-devloop:pr-delegation:v1", 1, true) ~= nil)
    t.is_true(find_raise(redriven.raises, "github-proxy.github_issue_label_request", function(payload)
      return payload.add_labels[1] == "fkst-dev:awaiting-pr"
    end) ~= nil)
  end,
}
