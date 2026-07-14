local M = {}

local function require_devloop_function(devloop, name)
  local value = devloop[name]
  if type(value) ~= "function" then
    error("devloop.adapters.workflow_ports: missing " .. tostring(name))
  end
  return value
end

function M.from_devloop(devloop)
  if type(devloop) ~= "table" then
    error("devloop.adapters.workflow_ports: missing devloop table")
  end
  local trusted_bot_login = require("devloop.base").trusted_bot_login
  return {
    dependency_release_marker = function(...)
      return require_devloop_function(devloop, "dependency_release_marker")(...)
    end,
    restart_transition_table = function(...)
      return require_devloop_function(devloop, "restart_transition_table")(...)
    end,
    trusted_bot_login = function(...)
      return trusted_bot_login(...)
    end,
  }
end

return M
