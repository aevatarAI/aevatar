local h = require("tests.devloop_core_helpers")
local m_builders = require("devloop.markers.builders")
local core = h.core
local t = h.t

local contract = core.pr_partition_contract
local proposal_id = "github-devloop/issue/owner/repo/42"

local function marker(state, version)
  return core.state_marker(proposal_id, state, version or ("2026-06-03T01-02-03Z/" .. state))
end

local function has_value(values, expected)
  for _, value in ipairs(values or {}) do
    if value == expected then
      return true
    end
  end
  return false
end

return {
  test_issue_partition_rejects_pr_phase_states = function()
    for _, state in ipairs(contract.pr_phase_states()) do
      t.eq(contract.state_allowed_for_saga("issue", state), false, state)
    end
    t.eq(contract.state_allowed_for_saga("issue", "ready"), true)
  end,

  test_pr_partition_accepts_pr_phases_and_terminals = function()
    for _, state in ipairs(contract.pr_phase_states()) do
      t.eq(contract.state_allowed_for_saga("pr", state), true, state)
    end
    for _, state in ipairs(contract.pr_terminal_states()) do
      t.eq(contract.state_allowed_for_saga("pr", state), true, state)
    end
    t.eq(contract.state_allowed_for_saga("pr", "ready"), false)
    t.eq(contract.state_allowed_for_saga("pr", "closed-unmerged"), true)
  end,

  test_partition_contract_declares_poll_boundary_and_child_liveness_shape = function()
    local issue_states = contract.issue_states()
    local pr_states = contract.pr_phase_states()
    local pr_terminals = contract.pr_terminal_states()
    t.eq(has_value(issue_states, "implementing"), true)
    t.eq(has_value(issue_states, "awaiting-pr"), true)
    t.eq(has_value(pr_states, "merge-ready"), true)
    t.eq(has_value(pr_terminals, "closed-unmerged"), true)
    for _, issue_state in ipairs(issue_states) do
      for _, pr_state in ipairs(pr_states) do
        t.is_true(issue_state ~= pr_state)
      end
    end

    local awaiting = contract.awaiting_pr_contract()
    t.eq(awaiting.state, "awaiting-pr")
    t.eq(awaiting.liveness_class, "child_workflow_wait")
    t.eq(awaiting.queue_out, nil)
    t.eq(awaiting.queue_in, nil)
    t.eq(has_value(awaiting.child_terminal_states, "merged"), true)
    t.eq(has_value(awaiting.child_terminal_states, "closed-unmerged"), true)
    t.eq(has_value(awaiting.child_terminal_states, "blocked"), true)
  end,

  test_pr_phase_is_not_a_legal_issue_state = function()
    t.eq(contract.state_allowed_for_saga("issue", "merge-ready"), false)
    t.eq(contract.state_allowed_for_saga("issue", "reviewing"), false)
    t.eq(contract.state_allowed_for_saga("issue", "pr-open"), false)
  end,

  test_core_current_state_behavior_is_unchanged_and_still_sees_pr_phase = function()
    local comments = {
      {
        body = marker("ready", "2026-06-03T01-02-03Z"),
        author_login = core._test_bot_login,
        created_at = "2026-06-03T01:02:03Z",
      },
      {
        body = marker("merge-ready", "2026-06-03T01-02-03Z/review/1"),
        author_login = core._test_bot_login,
        created_at = "2026-06-03T01:03:03Z",
      },
    }
    local current = core.current_state(comments, proposal_id)
    t.eq(current.state, "merge-ready")
  end,

  test_step1_target_fixture_documents_current_parent_projection_behavior = function()
    local comments = {
      {
        body = marker("pr-open", "2026-06-03T01-02-03Z"),
        author_login = core._test_bot_login,
        created_at = "2026-06-03T01:02:03Z",
      },
      {
        body = marker("merge-ready", "2026-06-03T01-02-03Z/review/1"),
        author_login = core._test_bot_login,
        created_at = "2026-06-03T01:03:03Z",
      },
    }

    -- Step 1 target: legacy issue pr-open plus PR merge-ready should project to
    -- parent awaiting-pr while automation continues from PR authority. The Lua
    -- runner has no pending/xfail support, so Step 0 asserts the current behavior.
    local current = core.current_state(comments, proposal_id)
    t.eq(current.state, "merge-ready")
  end,

  test_pr_delegation_marker_shape_is_declared = function()
    local marker_text = m_builders.pr_delegation_marker(core, 
      proposal_id,
      "github-devloop/pr/owner/repo/7",
      7,
      "2026-06-03T01-02-03Z",
      "delegate-owner-repo-7"
    )
    t.is_true(marker_text:find("fkst:github-devloop:pr-delegation:v1", 1, true) ~= nil)
    t.is_true(marker_text:find('proposal="' .. proposal_id .. '"', 1, true) ~= nil)
    t.is_true(marker_text:find('pr_proposal="github-devloop/pr/owner/repo/7"', 1, true) ~= nil)
    t.is_true(marker_text:find('pr="7"', 1, true) ~= nil)
    t.eq(core.restart_durable_marker_fields()["pr-delegation"].pr_proposal, true)
  end,
}
