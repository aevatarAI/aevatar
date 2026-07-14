local S = {}

local queries = {
  dependency_blocked_by = '{repository(owner:"{{owner}}",name:"{{name}}"){issue(number:{{issue_number}}){blockedBy(first:50){totalCount pageInfo{hasNextPage} nodes{number state stateReason repository{nameWithOwner}}}}}}',
}

local function github_result(fn)
  local ok, result_or_error = pcall(fn)
  if ok then
    return result_or_error
  end
  if type(result_or_error) == "table" and result_or_error.result ~= nil then
    return result_or_error.result
  end
  error(result_or_error)
end

local function render_query(template, fields)
  return tostring(template or ""):gsub("{{([%w_]+)}}", function(name)
    local value = fields and fields[name]
    if value == nil then
      error("github-devloop: graphql-template-missing-field: " .. tostring(name))
    end
    return tostring(value)
  end)
end

function S.install(M)
  M.github_graphql_queries = queries

  function M.render_github_graphql_query(name, fields)
    local template = queries[name]
    if template == nil then
      error("github-devloop: graphql-template-unknown-query: " .. tostring(name))
    end
    return render_query(template, fields)
  end

  function M.github_graphql(name, fields, timeout, exec)
    local query = M.render_github_graphql_query(name, fields)
    local run = exec or exec_argv
    if type(run) ~= "function" then
      error("github-devloop: GitHub GraphQL adapter requires exec_argv")
    end
    return github_result(function()
      return require("forge.github").new(run).graphql(query, nil, timeout or 30)
    end)
  end
end

return S
