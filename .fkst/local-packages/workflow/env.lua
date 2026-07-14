local M = {}

local function read_env_value(name, exec, command_builder, opts)
  local run = exec or exec_sync
  if type(run) ~= "function" or type(command_builder) ~= "function" then
    if opts and opts.missing_exec_error then
      error(opts.missing_exec_error)
    end
    return nil
  end
  if opts and opts.propagate_exec_errors then
    local out = run(command_builder(name))
    if type(out) ~= "table" or out.exit_code ~= 0 or out.stdout == "" then
      return nil
    end
    return out.stdout
  end
  local ok, out = pcall(run, command_builder(name))
  if not ok or type(out) ~= "table" or out.exit_code ~= 0 or out.stdout == "" then
    return nil
  end
  return out.stdout
end

function M.read_env(name, exec, command_builder)
  if type(name) == "function" then
    local bound_command_builder = name
    local opts = exec
    return function(bound_name, bound_exec)
      return read_env_value(bound_name, bound_exec, bound_command_builder, opts)
    end
  end
  return read_env_value(name, exec, command_builder)
end

return M
