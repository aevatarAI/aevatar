local entity_lib = require("devloop.entity")
local h = require("tests.devloop_core_helpers")
local core = h.core
local contract_time = require("contract.time")
local t = h.t

local function has_value(values, expected)
  for _, value in ipairs(values or {}) do
    if value == expected then
      return true
    end
  end
  return false
end

local function table_by_state()
  local by_state = {}
  for _, row in ipairs(core.restart_transition_table()) do
    by_state[row.from_state] = row
  end
  return by_state
end

return {
  test_awaiting_pr_restart_row_declares_child_workflow_boundary = function()
    local row = table_by_state()["awaiting-pr"]
    t.is_true(row ~= nil)
    t.eq(row.driving_queue, "devloop_observe_redrive")
    t.eq(row.on_timeout.queue, "devloop_observe_redrive")
    t.eq(row.liveness_class_id, "child_workflow_wait")
    t.eq(row.watchdog.mode, "live-defer")
    t.eq(row.defer.kind, "child_workflow_wait")
    t.eq(row.defer.live_marker, "state:v1")
    t.eq(row.defer.delegation_marker, "pr-delegation:v1")
    t.eq(row.actionable_epoch.source, "child_workflow_wait:v1")
    t.eq(row.liveness_contract.signal.family, "state")
    t.eq(row.liveness_contract.signal.resolver, "child-state")
    t.eq(row.liveness_contract.signal.surface, "pr-comment-stream")
    t.eq(row.payload_builder, nil)
    t.eq(row.responsibility_signature.receiver_kind, "pr-child-workflow")
    t.eq(row.responsibility_signature.state_kind, "gate")
    t.eq(row.responsibility_signature.output_postcondition_family, "parent_resume_from_child_state_terminal")
    t.eq(row.to_states[1], "merged")
    t.eq(row.to_states[2], "ready")
    t.eq(row.to_states[3], "blocked")
    t.eq(row.dedup_shape, "child-state-terminal/<proposal>/<version>/<pr>")
  end,

  test_forward_flip_wires_implementing_success_to_awaiting_pr = function()
    for _, row in ipairs(core.restart_transition_table()) do
      if row.from_state ~= "awaiting-pr" then
        t.eq(has_value(row.to_states, "awaiting-pr"), row.from_state == "implementing", row.from_state)
      end
    end
    t.eq(has_value(core.state_successors("implementing"), "awaiting-pr"), true)
    t.eq(has_value(core.state_successors("implementing"), "pr-open"), false)
  end,

  test_awaiting_pr_timeout_escalates_through_timeout_reconcile = function()
    local row = table_by_state()["awaiting-pr"]
    local state = {
      state = "awaiting-pr",
      version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z/timeout/awaiting-pr/3",
      proposal_id = "github-devloop/issue/owner/repo/42",
      marker_created_at = "2026-06-03T01:02:03Z",
    }
    local raised = {}
    local original_log_raise = core.log_raise
    core.log_raise = function(_, _, queue, payload)
      table.insert(raised, { queue = queue, payload = payload })
    end
    local ok, err = pcall(function()
      local applied = core.maybe_timeout_redrive_from_table("observe_issue", {
        repo = "owner/repo",
        number = 42,
        source_ref = entity_lib.issue_source_ref("owner/repo", 42),
      }, state, row, {
        proposal_id = state.proposal_id,
        source_ref = entity_lib.issue_source_ref("owner/repo", 42),
        current = { comments = {} },
        current_pr = { comments = {} },
        now_seconds = contract_time.iso_timestamp_epoch_seconds("2026-12-01T01:02:03Z"),
      })
      t.eq(applied, true)
    end)
    core.log_raise = original_log_raise
    if not ok then
      error(err)
    end
    t.eq(raised[#raised].queue, "devloop_timeout_reconcile")
    t.eq(raised[#raised].payload.state, "awaiting-pr")
    t.eq(raised[#raised].queue == row.driving_queue, false)
  end,
}
