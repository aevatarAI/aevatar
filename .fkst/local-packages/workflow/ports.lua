local M = {}

M.names = {
  dependency_release_marker = "dependency_release_marker",
  restart_transition_table = "restart_transition_table",
  trusted_bot_login = "trusted_bot_login",
}

M.types = {
  dependency_release_marker = "function",
  restart_transition_table = "function",
  trusted_bot_login = "function",
}

local groups = {
  restart_liveness_contract = {
    M.names.dependency_release_marker,
    M.names.restart_transition_table,
    M.names.trusted_bot_login,
  },
}

local function required_names(group_name, names)
  if names ~= nil then
    return names
  end
  local group = groups[group_name]
  if group == nil then
    error("workflow.ports: unknown port group " .. tostring(group_name))
  end
  return group
end

function M.require_ports(resolved, owner, names)
  local group_owner = owner or "workflow"
  if type(resolved) ~= "table" then
    error("workflow.ports: missing resolved for " .. tostring(group_owner))
  end
  local source = resolved.workflow_ports
  if type(source) ~= "table" then
    error("workflow.ports: missing resolved.workflow_ports for " .. tostring(group_owner))
  end

  local ports = {}
  for _, name in ipairs(required_names(group_owner, names)) do
    local value = source[name]
    local expected_type = M.types[name]
    if expected_type == nil then
      error("workflow.ports: unknown port " .. tostring(name) .. " for " .. tostring(group_owner))
    end
    if type(value) ~= expected_type then
      error("workflow.ports: missing " .. expected_type .. " port " .. tostring(name) .. " for " .. tostring(group_owner))
    end
    ports[name] = value
  end
  return { ports = ports }
end

function M.restart_liveness_contract(resolved)
  return M.require_ports(resolved, "restart_liveness_contract")
end

return M
