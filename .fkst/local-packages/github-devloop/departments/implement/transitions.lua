local core = require("core")

local M = {}

function M.expected_state_matches(state, expected)
  local expected_state = expected
  local version = nil
  if type(expected) == "table" then
    expected_state = expected.state
    version = expected.version
    if version == nil then
      version = expected.target_version
    end
  end
  if version == nil then
    return tostring(state.state or "") == tostring(expected_state)
  end
  return tostring(state.state or "") == tostring(expected_state)
    and tostring(state.version or "") == tostring(version or "")
end

local function expected_state_names(expected_states)
  local names = {}
  for _, expected in ipairs(expected_states or {}) do
    if type(expected) == "table" then
      table.insert(names, expected.state)
    else
      table.insert(names, expected)
    end
  end
  return names
end

local function expected_transition_versions(expected_states, default_version)
  local source_version = default_version
  local target_version = nil
  for _, expected in ipairs(expected_states or {}) do
    if type(expected) == "table" then
      if expected.version ~= nil then
        source_version = expected.version
      end
      if expected.target_version ~= nil then
        target_version = expected.target_version
      end
    end
  end
  return source_version, target_version
end

function M.implementation_transition_status(state, expected_states, marker_version)
  local source_version, target_version = expected_transition_versions(expected_states, marker_version)
  if target_version ~= nil then
    return core.cyclic_transition_status(state, expected_state_names(expected_states), "implementing", source_version, target_version)
  end
  return core.versioned_transition_status(state, expected_state_names(expected_states or { "ready" }), "implementing", marker_version)
end

function M.expected_states_include(expected_states, state_name)
  for _, expected in ipairs(expected_states or {}) do
    local expected_state = type(expected) == "table" and expected.state or expected
    if tostring(expected_state or "") == tostring(state_name or "") then
      return true
    end
  end
  return false
end

return M
