local M = {}

local function copy(value)
  if type(value) ~= "table" then
    return value
  end
  local result = {}
  for key, field in pairs(value) do
    result[copy(key)] = copy(field)
  end
  return result
end

function M.model(seed)
  return {
    refs = seed and seed.refs or {},
    writes = seed and seed.writes or {},
  }
end

function M.new(model)
  assert(type(model) == "table", "forge.git_fake.new requires a model")
  local handle = { _model = model }
  function handle._exec(argv, timeout, context)
    table.insert(model.writes, {
      kind = "exec",
      argv = copy(argv),
      timeout = timeout,
      context = context,
    })
    return { stdout = "", stderr = "", exit_code = 0 }
  end
  require("forge.git.refs").install(handle)
  return handle
end

return M
