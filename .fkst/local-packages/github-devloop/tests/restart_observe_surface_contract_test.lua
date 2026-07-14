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
  test_restart_rows_declare_authoritative_observe_surfaces = function()
    local by_state = table_by_state()
    local expected = {
      thinking = { issue = true, liveness_scan = true },
      ready = { issue = true, liveness_scan = true },
      implementing = { issue = true, liveness_scan = true },
      ["awaiting-pr"] = { issue = true, liveness_scan = true },
      ["impl-failed"] = { issue = true, liveness_scan = true },
      blocked = { issue = true, pr = true, liveness_scan = true },
    }
    for state, surfaces in pairs(expected) do
      local row = by_state[state]
      t.is_true(row ~= nil, state)
      for surface, enabled in pairs(surfaces) do
        t.eq(core.restart_row_observable_on(row, surface), enabled)
      end
    end
    t.eq(core.restart_row_observable_on(by_state.merged, "issue"), false)
    t.eq(#core.liveness_contract_errors(), 0)
  end,

  test_timeout_surfaces_are_declared_separately_from_replay_surfaces = function()
    local by_state = table_by_state()
    t.eq(by_state.thinking.timeout_surfaces.issue, true)
    t.eq(by_state.thinking.timeout_surfaces.liveness_scan, true)
    t.eq(by_state["awaiting-pr"].timeout_surfaces.issue, true)
    t.eq(by_state["awaiting-pr"].timeout_surfaces.issue_liveness_scan, true)
    t.eq(by_state.ready.timeout_surfaces, nil)
    t.eq(core.restart_observe_timeout_due(by_state.ready, "issue", {
      state = "ready",
      version = "ready/old",
      marker_created_at = "2026-06-03T00:00:00Z",
    }, {}, contract_time.iso_timestamp_epoch_seconds("2026-06-04T01:00:00Z")), false)
  end,
}
