local h = require("tests.devloop_helpers")
local m_builders = require("devloop.markers.builders")
local t = h.t
local core = h.core
local opts = h.opts
local reached = h.reached
local run_result = h.run_result
local mock_issue_result = h.mock_issue_result
local find_raise = h.find_raise

return {
  test_consensus_result_ready_marker_heals_missing_declared_effects = function()
    local current = reached()
    mock_issue_result({ "fkst-dev:thinking" }, {
      core.state_marker(current.proposal_id, "ready", current.dedup_key),
    })

    local result = run_result(current, opts("result-outbox-ready-marker-missing-effects"))

    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 2)
    local comment_raise = find_raise(result.raises, "github-proxy.github_issue_comment_request")
    local label_raise = find_raise(result.raises, "github-proxy.github_issue_label_request")
    t.is_true(comment_raise.payload.body:find(m_builders.result_marker(core, current.proposal_id, current.decision, current.dedup_key), 1, true) ~= nil)
    t.eq(comment_raise.payload.handoff.kind, "github-devloop.ready")
    t.eq(comment_raise.payload.handoff.version, current.dedup_key)
    t.eq(label_raise.payload.add_labels[1], "fkst-dev:ready")
    t.eq(find_raise(result.raises, "devloop_ready"), nil)
  end,

  test_consensus_result_ready_marker_skips_when_declared_effects_are_complete = function()
    local current = reached()
    mock_issue_result({ "fkst-dev:ready" }, {
      core.state_marker(current.proposal_id, "ready", current.dedup_key),
      m_builders.result_marker(core, current.proposal_id, current.decision, current.dedup_key),
    })

    local result = run_result(current, opts("result-outbox-complete"))

    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 0)
  end,

  test_consensus_result_result_marker_heals_only_missing_label_and_ready_replay = function()
    local current = reached()
    mock_issue_result({ "fkst-dev:thinking" }, {
      m_builders.result_marker(core, current.proposal_id, current.decision, current.dedup_key),
    })

    local result = run_result(current, opts("result-outbox-result-marker-no-label"))

    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 1)
    t.eq(find_raise(result.raises, "github-proxy.github_issue_comment_request"), nil)
    t.eq(find_raise(result.raises, "github-proxy.github_issue_label_request").payload.add_labels[1], "fkst-dev:ready")
    t.eq(find_raise(result.raises, "devloop_ready"), nil)
  end,

  test_consensus_result_from_loop_advances_answered_intake_marker = function()
    local intake_version = "github-devloop/issue/owner/repo/42/intake/2485289059"
    local consensus_version = "consensus:" .. intake_version .. "/loop/5"
    local current = reached({
      dedup_key = consensus_version,
    })
    mock_issue_result({ "fkst-dev:thinking" }, {
      core.state_marker(current.proposal_id, "thinking", intake_version),
    })

    local result = run_result(current, opts("result-loop-answers-intake-marker"))

    t.eq(result.exit_code, 0)
    local comment_raise = find_raise(result.raises, "github-proxy.github_issue_comment_request")
    local label_raise = find_raise(result.raises, "github-proxy.github_issue_label_request")
    t.is_true(comment_raise ~= nil)
    t.eq(label_raise.payload.add_labels[1], "fkst-dev:ready")
    t.eq(find_raise(result.raises, "devloop_ready"), nil)
    t.is_true(comment_raise.payload.body:find(core.state_marker(current.proposal_id, "ready", consensus_version, "result-marker,ready-label,devloop-ready"), 1, true) ~= nil)
  end,
}
