local C = {}

local restart = require("devloop.restart")

local transitions_base = "devloop.restart.issue.transitions"

local function index_module(base)
  return base .. ".index"
end

local function entry_name(index_entry)
  if type(index_entry) == "string" then
    return index_entry
  end
  return index_entry.module
end

local function load_entries(base, index)
  local entries = {}
  for _, index_entry in ipairs(index) do
    table.insert(entries, require(base .. "." .. entry_name(index_entry)))
  end
  return entries
end

function C.transition_sources()
  local transitions_index = require(index_module(transitions_base))
  return {
    transitions_index = transitions_index,
    transitions = load_entries(transitions_base, transitions_index),
    transitions_label = index_module(transitions_base),
  }
end

function C.transition_table(M)
  return restart.transition_table(M, C.transition_sources())
end

local function lifecycle_row(row)
  return {
    from_state = row.from_state,
    terminal = row.terminal,
    driving_queue = row.driving_queue or "none",
    budget = row.budget,
  }
end

function C.lifecycle_rows(M)
  local rows = {}
  for _, row in ipairs(C.transition_table(M)) do
    table.insert(rows, lifecycle_row(row))
  end
  return rows
end

-- Memoize the by-state index per composed M, weak-keyed so it is built once
-- per M -- matching the old install-time build-once-and-close-over behavior
-- (stable returned-row identity + O(1) lookups, not an O(n) rebuild per call).
local lifecycle_by_state_cache = setmetatable({}, { __mode = "k" })

local function lifecycle_by_state(M)
  local cached = lifecycle_by_state_cache[M]
  if cached then
    return cached
  end
  local by_state = {}
  for _, row in ipairs(C.lifecycle_rows(M)) do
    by_state[row.from_state] = row
  end
  lifecycle_by_state_cache[M] = by_state
  return by_state
end

function C.lifecycle_transition_row(M, state_name)
  return lifecycle_by_state(M)[state_name]
end

function C.liveness_budget_minutes(M, state_name)
  local row = C.lifecycle_transition_row(M, state_name)
  return row and row.budget and tonumber(row.budget.minutes) or nil
end

return C
