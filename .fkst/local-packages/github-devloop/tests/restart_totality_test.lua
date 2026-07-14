local h = require("tests.devloop_core_helpers")
local core = h.core
local t = h.t

local function copy_rows(rows)
  local copied = {}
  local function copy_value(value)
    if type(value) ~= "table" then
      return value
    end
    local nested = {}
    for nested_key, nested_value in pairs(value) do
      nested[nested_key] = copy_value(nested_value)
    end
    return nested
  end
  for index, row in ipairs(rows or {}) do
    copied[index] = copy_value(row)
  end
  return copied
end

local function by_state(rows)
  local indexed = {}
  for _, row in ipairs(rows or {}) do
    indexed[row.from_state] = row
  end
  return indexed
end

return {
  test_restart_totality_rejects_missing_reachable_state = function()
    local rows = copy_rows(core.restart_transition_table())
    local filtered = {}
    for _, row in ipairs(rows) do
      if row.from_state ~= "ready" then
        table.insert(filtered, row)
      end
    end
    local errors = core.liveness_contract_errors(filtered)
    t.eq(#errors, 1)
    t.is_true(errors[1]:find("ready", 1, true) ~= nil)
    t.is_true(errors[1]:find("missing", 1, true) ~= nil)
  end,

  test_restart_totality_rejects_unknown_or_duplicate_rows = function()
    local rows = copy_rows(core.restart_transition_table())
    table.insert(rows, copy_rows({ by_state(rows).ready })[1])
    local unknown = copy_rows({ by_state(rows).ready })[1]
    unknown.from_state = "new-state"
    table.insert(rows, unknown)
    local errors = core.liveness_contract_errors(rows)
    local joined = table.concat(errors, "\n")
    t.is_true(joined:find("ready", 1, true) ~= nil)
    t.is_true(joined:find("duplicate", 1, true) ~= nil)
    t.is_true(joined:find("new-state", 1, true) ~= nil)
    t.is_true(joined:find("not a reachable lifecycle state", 1, true) ~= nil)
  end,
}
