local exec_wrap = require("forge.git.exec")

local M = {}
local production_handles = {}

function M.new(exec)
  assert(type(exec) == "function", "forge.git.new requires an exec function")
  local handle = {}
  function handle._exec(argv, timeout, context)
    return exec_wrap.run(exec, argv, timeout, context)
  end
  require("forge.git.refs").install(handle)
  return handle
end

function M.production_handle(owner)
  local key = tostring(owner or "forge.git")
  local handle = production_handles[key]
  if handle == nil then
    if type(exec_argv) ~= "function" then
      error(key .. ": git adapter requires exec_argv")
    end
    handle = M.new(exec_argv)
    production_handles[key] = handle
  end
  return handle
end

return M
