local devloop_base = require("devloop.base")
local entity_lib = require("devloop.entity")
local base_ids = require("devloop.base_ids")
local h = require("tests.devloop_core_helpers")
local core = h.core
local t = h.t
local gh_argv = require("testkit.gh_argv_mock")
local m_builders = require("devloop.markers.builders")

local repo = "owner/repo"
local issue_number = 42
local issue_proposal = "github-devloop/issue/owner/repo/42"
local impl_version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"
local branch = devloop_base.implement_branch(repo, issue_number, core.implementation_base_version(impl_version))
local base_branch = "dev"
local head_sha = "abc123def456"

local function source_ref()
  return entity_lib.issue_source_ref(repo, issue_number)
end

local function pr_source_ref(pr_number)
  return entity_lib.pr_source_ref(repo, pr_number)
end

local function pr_proposal(pr_number)
  return entity_lib.pr_proposal_id(repo, pr_number)
end

local function issue(extra)
  local value = {
    repo = repo,
    number = issue_number,
    title = "Implement decision recorder",
    proposal_id = issue_proposal,
    source_ref = source_ref(),
    branch = branch,
    base_branch = base_branch,
    head_sha = head_sha,
    comments = {},
    pr_comments = {},
  }
  for key, field in pairs(extra or {}) do
    value[key] = field
  end
  return value
end

local function pr_list(pr_number)
  if pr_number == nil then
    return "[[]]\n"
  end
  return '[[{"number":' .. tostring(pr_number)
    .. ',"head":{"ref":"' .. branch .. '","sha":"' .. head_sha .. '"},"base":{"ref":"' .. base_branch .. '"},"state":"open"}]]\n'
end

local function mock_branch_list(...)
  local count = select("#", ...)
  for index = 1, count do
    local pr_number = select(index, ...)
    t.mock_command(core.gh_pr_list_head_base_cmd(repo, branch, base_branch), {
      stdout = pr_list(pr_number),
      stderr = "",
      exit_code = 0,
    })
  end
end

local function count_gh_pr_create()
  return gh_argv.count_calls(t, "gh pr create")
end

local function count_effects(effects, queue)
  local count = 0
  for _, effect in ipairs(effects or {}) do
    if effect.queue == queue then
      count = count + 1
    end
  end
  return count
end

local function find_effect(effects, queue)
  for _, effect in ipairs(effects or {}) do
    if effect.queue == queue then
      return effect
    end
  end
  return nil
end

local function render_comment(body)
  return {
    body = body,
    author_login = "fkst-test-bot",
    created_at = "2026-06-03T01:02:03Z",
  }
end

return {
  test_ensure_pr_child_creates_then_adopts_by_branch_and_writes_split_facts = function()
    mock_branch_list(nil, 7)
    t.mock_command("gh pr create", { stdout = "https://github.example/owner/repo/pull/7\n", stderr = "", exit_code = 0 })

    local result = core.ensure_pr_child(issue(), impl_version, 1)

    t.eq(result.pr_number, 7)
    t.eq(result.child_start_visible, false)
    t.eq(result.issue_delegation_visible, false)
    t.eq(result.ready_for_parent_awaiting_pr, false)
    t.eq(result.pr_proposal_id, "github-devloop/pr/owner/repo/7")
    t.eq(result.branch, branch)
    t.eq(result.base_branch, base_branch)
    t.eq(count_gh_pr_create(), 1)
    local pr_effect = find_effect(result.effects, "github-proxy.github_pr_comment_request")
    local issue_effect = find_effect(result.effects, "github-proxy.github_issue_comment_request")
    t.is_true(pr_effect ~= nil)
    t.is_true(issue_effect ~= nil)
    t.is_true(pr_effect.payload.body:find('fkst:github-devloop:pr-origin:v1', 1, true) ~= nil)
    t.is_true(pr_effect.payload.body:find('proposal="' .. issue_proposal .. '"', 1, true) ~= nil)
    t.is_true(pr_effect.payload.body:find('issue="' .. tostring(issue_number) .. '"', 1, true) ~= nil)
    t.is_true(pr_effect.payload.body:find('state="pr-open"', 1, true) ~= nil)
    t.eq(pr_effect.payload.handoff.kind, "github-devloop.pr_open")
    t.eq(pr_effect.payload.handoff.proposal_id, issue_proposal)
    t.eq(pr_effect.payload.handoff.pr_number, 7)
    t.eq(pr_effect.payload.handoff.version, impl_version)
    t.is_true(issue_effect.payload.body:find('fkst:github-devloop:pr-delegation:v1', 1, true) ~= nil)
    t.is_true(issue_effect.payload.body:find('state="pr-open"', 1, true) == nil)
  end,

  test_ensure_pr_child_rerun_with_visible_facts_is_idempotent = function()
    local pr_proposal = "github-devloop/pr/owner/repo/7"
    local delegated = m_builders.pr_delegation_marker(core, issue_proposal, pr_proposal, 7, impl_version, "g1")
    local visible_pr_open = m_builders.pr_origin_marker(core, issue_proposal, issue_number, branch, impl_version, base_branch)
      .. "\n" .. core.state_marker(issue_proposal, "pr-open", impl_version)
    mock_branch_list(7)

    local result = core.ensure_pr_child(issue({
      comments = { render_comment(delegated) },
      pr_comments = { render_comment(visible_pr_open) },
    }), impl_version, 1)

    t.eq(result.pr_number, 7)
    t.eq(result.child_start_visible, true)
    t.eq(result.issue_delegation_visible, true)
    t.eq(result.ready_for_parent_awaiting_pr, true)
    t.eq(#result.effects, 0)
    t.eq(count_gh_pr_create(), 0)
  end,

  test_child_start_stays_visible_after_child_pr_advances_past_pr_open = function()
    local pr_proposal = "github-devloop/pr/owner/repo/7"
    local delegated = m_builders.pr_delegation_marker(core, issue_proposal, pr_proposal, 7, impl_version, "g1")
    local advanced_child = m_builders.pr_origin_marker(core, issue_proposal, issue_number, branch, impl_version, base_branch)
      .. "\n" .. core.state_marker(issue_proposal, "reviewing", impl_version .. "/review/1")
    mock_branch_list(7)

    local result = core.ensure_pr_child(issue({
      comments = { render_comment(delegated) },
      pr_comments = { render_comment(advanced_child) },
    }), impl_version, 1)

    t.eq(result.pr_number, 7)
    t.eq(result.child_start_visible, true)
    t.eq(result.issue_delegation_visible, true)
    t.eq(result.ready_for_parent_awaiting_pr, true)
    t.eq(count_effects(result.effects, "github-proxy.github_pr_comment_request"), 0)
    t.eq(#result.effects, 0)
    t.eq(count_gh_pr_create(), 0)
  end,

  test_child_start_dsl_gate_matches_visible_child_start_markers = function()
    local delegated = m_builders.pr_delegation_marker(core, issue_proposal, pr_proposal(7), 7, impl_version, "g1")
    local matching_origin = m_builders.pr_origin_marker(core, issue_proposal, issue_number, branch, impl_version, base_branch)
    local mismatched_origin = m_builders.pr_origin_marker(core, issue_proposal, issue_number, branch .. "-old", impl_version, base_branch)
    local advanced_child = matching_origin .. "\n" .. core.state_marker(issue_proposal, "reviewing", impl_version .. "/review/1")
    mock_branch_list(7, 7, 7)

    t.eq(core.ensure_pr_child(issue({
      comments = { render_comment(delegated) },
      pr_comments = {},
    }), impl_version, 1).child_start_visible, false)
    t.eq(core.ensure_pr_child(issue({
      comments = { render_comment(delegated) },
      pr_comments = { render_comment(mismatched_origin) },
    }), impl_version, 1).child_start_visible, false)
    t.eq(core.ensure_pr_child(issue({
      comments = { render_comment(delegated) },
      pr_comments = { render_comment(advanced_child) },
    }), impl_version, 1).child_start_visible, true)
  end,

  test_ensure_pr_child_twice_same_generation_is_idempotent_and_keys_open_by_issue_generation = function()
    mock_branch_list(nil, 7, 7)
    t.mock_command("gh pr create", { stdout = "https://github.example/owner/repo/pull/7\n", stderr = "", exit_code = 0 })

    local first = core.ensure_pr_child(issue(), impl_version, 1)
    t.eq(first.ready_for_parent_awaiting_pr, false)
    local pr_effect = find_effect(first.effects, "github-proxy.github_pr_comment_request")
    local issue_effect = find_effect(first.effects, "github-proxy.github_issue_comment_request")
    t.is_true(pr_effect ~= nil)
    t.is_true(issue_effect ~= nil)
    t.eq(pr_effect.payload.dedup_key, base_ids.dedup_key({ "pr-delegation", "pr-open", issue_proposal, "g1" }))
    t.eq(pr_effect.payload.handoff.kind, "github-devloop.pr_open")
    t.eq(pr_effect.payload.handoff.proposal_id, issue_proposal)
    t.eq(pr_effect.payload.handoff.pr_number, 7)
    t.eq(pr_effect.payload.handoff.version, impl_version)

    local visible_pr_open = m_builders.pr_origin_marker(core, issue_proposal, issue_number, branch, impl_version, base_branch)
      .. "\n" .. core.state_marker(issue_proposal, "pr-open", impl_version)
    local second = core.ensure_pr_child(issue({
      comments = { render_comment(issue_effect.payload.body) },
      pr_comments = { render_comment(visible_pr_open) },
    }), impl_version, 1)

    t.eq(first.pr_number, 7)
    t.eq(second.pr_number, 7)
    t.eq(second.ready_for_parent_awaiting_pr, true)
    t.eq(count_gh_pr_create(), 1)
    t.eq(count_effects(first.effects, "github-proxy.github_pr_comment_request"), 1)
    t.eq(count_effects(first.effects, "github-proxy.github_issue_comment_request"), 1)
    t.eq(#second.effects, 0)
  end,

  test_existing_delegation_for_different_generation_is_not_current = function()
    local old_delegation = m_builders.pr_delegation_marker(core, issue_proposal, pr_proposal(7), 7, impl_version, "g1")
    mock_branch_list(nil, 8)
    t.mock_command("gh pr create", { stdout = "https://github.example/owner/repo/pull/8\n", stderr = "", exit_code = 0 })

    local result = core.ensure_pr_child(issue({
      comments = { render_comment(old_delegation) },
    }), impl_version, 2)

    t.eq(result.pr_number, 8)
    t.eq(result.delegation_generation, "g2")
    t.eq(count_gh_pr_create(), 1)
    local issue_effect = find_effect(result.effects, "github-proxy.github_issue_comment_request")
    t.is_true(issue_effect ~= nil)
    t.is_true(issue_effect.payload.body:find('pr="8"', 1, true) ~= nil)
    t.is_true(issue_effect.payload.body:find('delegation="g2"', 1, true) ~= nil)
    t.eq(count_effects(result.effects, "devloop_pr_open"), 0)
  end,

  test_existing_delegation_scan_finds_requested_generation_after_prior_attempt = function()
    local old_delegation = m_builders.pr_delegation_marker(core, issue_proposal, pr_proposal(7), 7, impl_version, "g1")
    local current_delegation = m_builders.pr_delegation_marker(core, issue_proposal, pr_proposal(8), 8, impl_version, "g2")
	    local visible_pr_open = m_builders.pr_origin_marker(core, issue_proposal, issue_number, branch, impl_version, base_branch)
	      .. "\n" .. core.state_marker(issue_proposal, "pr-open", impl_version)
    mock_branch_list(8)

    local result = core.ensure_pr_child(issue({
      comments = {
        render_comment(old_delegation),
        render_comment(current_delegation),
      },
      pr_comments = { render_comment(visible_pr_open) },
    }), impl_version, 2)

    t.eq(result.pr_number, 8)
    t.eq(result.delegation_generation, "g2")
    t.eq(result.ready_for_parent_awaiting_pr, true)
    t.eq(#result.effects, 0)
    t.eq(count_gh_pr_create(), 0)
  end,

  test_existing_delegation_same_generation_adopts_pr_but_rewrites_current_version = function()
    local prior_version = impl_version .. "/prior"
    local delegated = m_builders.pr_delegation_marker(core, issue_proposal, pr_proposal(7), 7, prior_version, "g1")
	    local visible_pr_open = m_builders.pr_origin_marker(core, issue_proposal, issue_number, branch, impl_version, base_branch)
	      .. "\n" .. core.state_marker(issue_proposal, "pr-open", impl_version)

    local result = core.ensure_pr_child(issue({
      comments = { render_comment(delegated) },
      pr_comments = { render_comment(visible_pr_open) },
    }), impl_version, 1)

    t.eq(result.pr_number, 7)
    t.eq(result.delegation_generation, "g1")
    t.eq(result.child_start_visible, true)
    t.eq(result.issue_delegation_visible, false)
    t.eq(result.ready_for_parent_awaiting_pr, false)
    t.eq(count_effects(result.effects, "github-proxy.github_issue_comment_request"), 1)
    t.eq(count_effects(result.effects, "github-proxy.github_pr_comment_request"), 0)
    t.eq(count_gh_pr_create(), 0)
  end,

  test_ensure_pr_child_adopts_created_pr_after_local_fact_loss = function()
    mock_branch_list(7)

    local result = core.ensure_pr_child(issue(), impl_version, 1)

    t.eq(result.pr_number, 7)
    t.eq(count_gh_pr_create(), 0)
    t.is_true(find_effect(result.effects, "github-proxy.github_pr_comment_request") ~= nil)
    t.is_true(find_effect(result.effects, "github-proxy.github_issue_comment_request") ~= nil)
  end,

  test_pr_open_comment_request_handoffs_to_pr_observer = function()
    local request = core.build_pr_delegation_open_comment_request(
      repo,
      7,
      issue_proposal,
      "github-devloop/pr/owner/repo/7",
      issue_number,
      impl_version,
      branch,
      base_branch,
      head_sha,
      pr_source_ref(7),
      "g1"
    )

    t.eq(request.handoff.kind, "github-devloop.pr_open")
    t.eq(request.handoff.proposal_id, issue_proposal)
    t.eq(request.handoff.pr_number, 7)
    t.eq(request.handoff.version, impl_version)
    t.is_true(request.body:find("fkst:github-devloop:pr-origin:v1", 1, true) ~= nil)
    t.is_true(request.body:find('state="pr-open"', 1, true) ~= nil)
  end,
}
