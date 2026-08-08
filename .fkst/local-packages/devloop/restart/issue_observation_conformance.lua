local C = {}
local published_facts = require("devloop.restart.issue_observation_facts")

local expected_schema = "restart-owner-observation-facts.v1"
local expected_owner = "github-devloop"
local compared_fields = {
  "from_state",
  "terminal",
  "driving_queue",
  "budget_minutes",
}
local compared_field_set = {}
for _, field in ipairs(compared_fields) do
  compared_field_set[field] = true
end

local function sorted_keys(map)
  local keys = {}
  for key, _ in pairs(map or {}) do
    table.insert(keys, key)
  end
  table.sort(keys)
  return keys
end

local function canonical_scalar(value)
  if value == nil then
    return "nil"
  end
  local text = tostring(value)
  return type(value) .. ":" .. tostring(#text) .. ":" .. text
end

local function canonical_states(states)
  local rows = {}
  for _, state_name in ipairs(sorted_keys(states)) do
    local state = states[state_name]
    table.insert(rows, table.concat({
      canonical_scalar(state_name),
      canonical_scalar(state and state.from_state),
      canonical_scalar(state and state.terminal),
      canonical_scalar(state and state.driving_queue),
      canonical_scalar(state and state.budget_minutes),
    }, "|"))
  end
  return table.concat(rows, "\n")
end

function C.source_rows_fingerprint(states)
  local canonical = canonical_states(states)
  local hash = 5381
  for index = 1, #canonical do
    hash = (hash * 33 + canonical:byte(index)) % 2147483647
  end
  return string.format("%08x", hash)
end

function C.normalize_row(row)
  return {
    from_state = row.from_state,
    terminal = row.terminal,
    driving_queue = row.driving_queue or "none",
    budget_minutes = row.budget and tonumber(row.budget.minutes) or nil,
  }
end

local function project_rows(rows)
  local states = {}
  for _, row in ipairs(rows or {}) do
    local normalized = C.normalize_row(row)
    states[normalized.from_state] = normalized
  end
  return states
end

local function state_shape_errors(states)
  local errors = {}
  for _, state_name in ipairs(sorted_keys(states)) do
    local state = states[state_name]
    if type(state) ~= "table" then
      table.insert(errors, "issue observation facts state " .. tostring(state_name) .. " must be a table")
    else
      for field, _ in pairs(state) do
        if compared_field_set[field] ~= true then
          table.insert(errors, "issue observation facts state " .. tostring(state_name)
            .. " unexpected field " .. tostring(field))
        end
      end
      if type(state.from_state) ~= "string" then
        table.insert(errors, "issue observation facts state " .. tostring(state_name)
          .. " field from_state must be string")
      end
      if type(state.terminal) ~= "boolean" then
        table.insert(errors, "issue observation facts state " .. tostring(state_name)
          .. " field terminal must be boolean")
      end
      if type(state.driving_queue) ~= "string" then
        table.insert(errors, "issue observation facts state " .. tostring(state_name)
          .. " field driving_queue must be string")
      end
      if state.budget_minutes ~= nil and type(state.budget_minutes) ~= "number" then
        table.insert(errors, "issue observation facts state " .. tostring(state_name)
          .. " field budget_minutes must be number or nil")
      end
    end
  end
  return errors
end

function C.errors(rows, facts_override)
  local facts = facts_override
  if facts == nil then
    facts = published_facts
  end
  local errors = {}
  local states = type(facts) == "table" and facts.states or nil

  if type(facts) ~= "table" then
    return { "issue observation facts must be a table" }
  end
  if facts.schema ~= expected_schema then
    table.insert(errors, "issue observation facts schema must be " .. expected_schema)
  end
  if facts.owner ~= expected_owner then
    table.insert(errors, "issue observation facts owner must be " .. expected_owner)
  end
  if type(states) ~= "table" then
    table.insert(errors, "issue observation facts states must be a table")
    return errors
  end

  local shape_errors = state_shape_errors(states)
  for _, message in ipairs(shape_errors) do
    table.insert(errors, message)
  end
  if #shape_errors == 0 then
    local fingerprint = C.source_rows_fingerprint(states)
    if facts.source_rows_fingerprint ~= fingerprint then
      table.insert(errors, "issue observation facts source_rows_fingerprint mismatch: expected "
        .. fingerprint .. ", got " .. tostring(facts.source_rows_fingerprint))
    end
  end

  local actual_states = project_rows(rows)
  for _, state_name in ipairs(sorted_keys(actual_states)) do
    local actual = actual_states[state_name]
    local published = states[state_name]
    if published == nil then
      table.insert(errors, "issue observation facts missing state " .. tostring(state_name))
    elseif type(published) == "table" then
      for _, field in ipairs(compared_fields) do
        if actual[field] ~= published[field] then
          table.insert(errors, "issue observation facts state " .. tostring(state_name)
            .. " field " .. field .. " mismatch: expected " .. tostring(actual[field])
            .. ", got " .. tostring(published[field]))
        end
      end
    end
  end
  for _, state_name in ipairs(sorted_keys(states)) do
    if actual_states[state_name] == nil then
      table.insert(errors, "issue observation facts extra state " .. tostring(state_name))
    end
  end
  return errors
end

return C
