local h = require("tests.devloop_ops_core_helpers")
local contract_time = require("contract.time")
local core = h.core
local t = h.t

local proposal_id = "github-devloop/issue/owner/repo/42"
local base_created_at = "2026-01-01T00:00:00Z"
local base_seconds = contract_time.iso_timestamp_epoch_seconds(base_created_at)

local function doctor_entity(state_name)
  return {
    kind = "issue",
    repo = "owner/repo",
    number = 42,
    proposal_id = proposal_id,
    labels = { "fkst-dev:enabled" },
    comments = {},
    open_state = "OPEN",
    current_state = {
      state = state_name,
      version = "github-devloop/issue/owner/repo/42/2026-01-01T00-00-00Z",
      marker_created_at = base_created_at,
    },
  }
end

local within_doctor_golden = {
  ["awaiting-pr"] = { "OK", "state age 0m is within liveness budget", "none" },
  blocked = { "OK", "state age 0m is within liveness budget", "none" },
  dependency_wait = { "OK", "state age 0m is within liveness budget", "none" },
  ["impl-failed"] = { "OK", "state age 0m is within liveness budget", "none" },
  implementing = { "OK", "state age 0m is within liveness budget", "none" },
  merged = { "OK", "terminal state from trusted marker", "none" },
  ready = { "OK", "state age 0m is within liveness budget", "none" },
  thinking = { "OK", "state age 0m is within liveness budget", "none" },
  reviewing = {
    "STUCK",
    "trusted marker state is not present in the lifecycle transition table",
    "update the package transition table or repair the marker",
  },
}

local over_doctor_golden = {
  ["awaiting-pr"] = {
    budget_minutes = 259200,
    reason = "state age 259201m exceeds 259200m liveness budget for devloop_observe_redrive",
    suggested = "inspect devloop_observe_redrive delivery and re-run observe/liveness",
  },
  blocked = {
    budget_minutes = 1440,
    reason = "state age 1441m exceeds 1440m liveness budget for github-devloop-decompose.devloop_decompose",
    suggested = "inspect github-devloop-decompose.devloop_decompose delivery and re-run observe/liveness",
  },
  dependency_wait = {
    budget_minutes = 525600,
    reason = "state age 525601m exceeds 525600m liveness budget for devloop_observe_redrive",
    suggested = "inspect devloop_observe_redrive delivery and re-run observe/liveness",
  },
  ["impl-failed"] = {
    budget_minutes = 1440,
    reason = "state age 1441m exceeds 1440m liveness budget for devloop_ready",
    suggested = "inspect devloop_ready delivery and re-run observe/liveness",
  },
  implementing = {
    budget_minutes = 120,
    reason = "state age 121m exceeds 120m liveness budget for devloop_ready",
    suggested = "inspect devloop_ready delivery and re-run observe/liveness",
  },
  ready = {
    budget_minutes = 120,
    reason = "state age 121m exceeds 120m liveness budget for devloop_ready",
    suggested = "inspect devloop_ready delivery and re-run observe/liveness",
  },
  thinking = {
    budget_minutes = 150,
    reason = "state age 151m exceeds 150m liveness budget for consensus.proposal",
    suggested = "inspect consensus.proposal delivery and re-run observe/liveness",
  },
}

local state_gap_golden = {
  { state = "awaiting-pr", to_state = "thinking", to_created_at = "2026-05-24T23:59:59Z", status = "within-budget", budget_seconds = 15552000 },
  { state = "awaiting-pr", to_state = "thinking", to_created_at = "2026-05-25T00:00:00Z", status = "near-budget", budget_seconds = 15552000 },
  { state = "awaiting-pr", to_state = "thinking", to_created_at = "2026-06-30T00:00:01Z", status = "over-budget", budget_seconds = 15552000 },
  { state = "blocked", to_state = "thinking", to_created_at = "2026-01-01T19:11:59Z", status = "within-budget", budget_seconds = 86400 },
  { state = "blocked", to_state = "thinking", to_created_at = "2026-01-01T19:12:00Z", status = "near-budget", budget_seconds = 86400 },
  { state = "blocked", to_state = "thinking", to_created_at = "2026-01-02T00:00:01Z", status = "over-budget", budget_seconds = 86400 },
  { state = "dependency_wait", to_state = "thinking", to_created_at = "2026-10-19T23:59:59Z", status = "within-budget", budget_seconds = 31536000 },
  { state = "dependency_wait", to_state = "thinking", to_created_at = "2026-10-20T00:00:00Z", status = "near-budget", budget_seconds = 31536000 },
  { state = "dependency_wait", to_state = "thinking", to_created_at = "2027-01-01T00:00:01Z", status = "over-budget", budget_seconds = 31536000 },
  { state = "impl-failed", to_state = "thinking", to_created_at = "2026-01-01T19:11:59Z", status = "within-budget", budget_seconds = 86400 },
  { state = "impl-failed", to_state = "thinking", to_created_at = "2026-01-01T19:12:00Z", status = "near-budget", budget_seconds = 86400 },
  { state = "impl-failed", to_state = "thinking", to_created_at = "2026-01-02T00:00:01Z", status = "over-budget", budget_seconds = 86400 },
  { state = "implementing", to_state = "thinking", to_created_at = "2026-01-01T01:35:59Z", status = "within-budget", budget_seconds = 7200 },
  { state = "implementing", to_state = "thinking", to_created_at = "2026-01-01T01:36:00Z", status = "near-budget", budget_seconds = 7200 },
  { state = "implementing", to_state = "thinking", to_created_at = "2026-01-01T02:00:01Z", status = "over-budget", budget_seconds = 7200 },
  { state = "ready", to_state = "thinking", to_created_at = "2026-01-01T01:35:59Z", status = "within-budget", budget_seconds = 7200 },
  { state = "ready", to_state = "thinking", to_created_at = "2026-01-01T01:36:00Z", status = "near-budget", budget_seconds = 7200 },
  { state = "ready", to_state = "thinking", to_created_at = "2026-01-01T02:00:01Z", status = "over-budget", budget_seconds = 7200 },
  { state = "thinking", to_state = "ready", to_created_at = "2026-01-01T01:59:59Z", status = "within-budget", budget_seconds = 9000 },
  { state = "thinking", to_state = "ready", to_created_at = "2026-01-01T02:00:00Z", status = "near-budget", budget_seconds = 9000 },
  { state = "thinking", to_state = "ready", to_created_at = "2026-01-01T02:30:01Z", status = "over-budget", budget_seconds = 9000 },
  { state = "merged", to_state = "thinking", to_created_at = "2026-01-01T00:01:00Z", status = "no-budget", budget_seconds = nil },
  { state = "reviewing", to_state = "thinking", to_created_at = "2026-01-01T00:01:00Z", status = "no-budget", budget_seconds = nil },
}

local function state_comment(state_name, created_at)
  return {
    body = core.state_marker(proposal_id, state_name, "v1"),
    author_login = "fkst-test-bot",
    created_at = created_at,
  }
end

local function state_gap_entity(case)
  return {
    proposal_id = proposal_id,
    issue_number = 42,
    parent_issue = {
      comments = {
        state_comment(case.state, base_created_at),
        state_comment(case.to_state, case.to_created_at),
      },
    },
    pr = { comments = {} },
  }
end

return {
  test_doctor_observation_behavior_matches_frozen_golden = function()
    for state_name, expected in pairs(within_doctor_golden) do
      local actual = core.saga_doctor_classify_entity(doctor_entity(state_name), {
        now_seconds = base_seconds,
      })
      t.eq(actual.verdict, expected[1], state_name .. " within verdict")
      t.eq(actual.reason, expected[2], state_name .. " within reason")
      t.eq(actual.suggested, expected[3], state_name .. " within suggestion")
    end

    for state_name, expected in pairs(over_doctor_golden) do
      local actual = core.saga_doctor_classify_entity(doctor_entity(state_name), {
        now_seconds = base_seconds + (expected.budget_minutes + 1) * 60,
      })
      t.eq(actual.verdict, "STUCK", state_name .. " over verdict")
      t.eq(actual.reason, expected.reason, state_name .. " over reason")
      t.eq(actual.suggested, expected.suggested, state_name .. " over suggestion")
    end
  end,

  test_state_gap_budget_behavior_matches_frozen_golden = function()
    for index, case in ipairs(state_gap_golden) do
      local edges = core.state_gap_edges_for_entity(state_gap_entity(case))
      t.eq(#edges, 1, "case " .. tostring(index) .. " edge count")
      t.eq(edges[1].from_state, case.state, "case " .. tostring(index) .. " from state")
      t.eq(edges[1].budget_status, case.status, "case " .. tostring(index) .. " budget status")
      t.eq(edges[1].budget_seconds, case.budget_seconds, "case " .. tostring(index) .. " budget seconds")
    end
  end,
}
