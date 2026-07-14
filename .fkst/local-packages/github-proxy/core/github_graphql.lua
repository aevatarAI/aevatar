local S = {}

local command_templates = {
  graphql_query = "GitHub GraphQL query",
}

local queries = {
  blocked_by = '{repository(owner:"{{owner}}",name:"{{name}}"){issue(number:{{issue_number}}){blockedBy(first:50){totalCount pageInfo{hasNextPage} nodes{number repository{nameWithOwner}}}}}}',
  add_blocked_by = "mutation($b:ID!,$g:ID!){addBlockedBy(input:{issueId:$b,blockingIssueId:$g}){clientMutationId}}",
}

local function render_query(template, fields)
  return tostring(template or ""):gsub("{{([%w_]+)}}", function(name)
    local value = fields and fields[name]
    if value == nil then
      error("github-proxy: graphql-template-missing-field: " .. tostring(name))
    end
    return tostring(value)
  end)
end

function S.install(M)
  M.github_graphql_command_templates = command_templates
  M.github_graphql_queries = queries

  function M.github_graphql(query, fields, timeout)
    return M.github().graphql(query, fields, timeout or 30)
  end

  function M.render_github_graphql_query(name, fields)
    local template = queries[name]
    if template == nil then
      error("github-proxy: graphql-template-unknown-query: " .. tostring(name))
    end
    return render_query(template, fields)
  end
end

return S
