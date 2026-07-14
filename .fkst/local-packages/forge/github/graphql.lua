local M = {}

local function graphql_argv(query, fields)
  local argv = { "gh", "api", "graphql", "-f", "query=" .. tostring(query) }
  for key, value in pairs(fields or {}) do
    table.insert(argv, "-f")
    table.insert(argv, tostring(key) .. "=" .. tostring(value))
  end
  return argv
end

function M.install(handle)
  function handle.graphql(query, fields, timeout)
    return handle._exec(graphql_argv(query, fields), timeout, "gh GraphQL")
  end
end

return M
