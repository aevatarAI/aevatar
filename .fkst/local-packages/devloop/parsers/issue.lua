local m_claims = require("devloop.claims")
local C = {}
local shared = require("devloop.parsers.shared")
local parsers_misc = require("devloop.parsers.misc")

function C.parse_issue_view_state(M, stdout)
  local decoded = json.decode(stdout or "{}")
  return C.issue_state_from_json(M, decoded)
end

function C.issue_state_from_json(M, decoded)
  local labels = {}
  for _, label in ipairs(decoded.labels or {}) do
    if type(label) == "table" and label.name ~= nil then
      table.insert(labels, tostring(label.name))
    elseif type(label) == "string" then
      table.insert(labels, label)
    end
  end

  return {
    title = decoded.title ~= nil and tostring(decoded.title) or nil,
    created_at = decoded.createdAt or decoded.created_at,
    updated_at = decoded.updatedAt or decoded.updated_at,
    labels = labels,
    comments = parsers_misc.comments_from_json(M, decoded.comments),
    state = decoded.state,
    assignees = m_claims.assignee_logins(M, decoded.assignees),
    author_login = m_claims.issue_author_login(M, decoded),
  }
end

function C.parse_issue_list_intake(M, stdout, limit)
  local decoded = json.decode(stdout or "[]")
  local issues = {}
  if type(decoded) ~= "table" then
    return issues
  end
  local max_items = math.floor(tonumber(limit or 2147483647) or 2147483647)
  if max_items < 1 then
    return issues
  end
  shared.each_paginated_item(M, decoded, function(issue)
    local number = type(issue) == "table" and tonumber(issue.number) or nil
    if number ~= nil and issue.pull_request == nil and #issues < max_items then
      table.insert(issues, {
        number = number,
        title = tostring(issue.title or ""),
        body = tostring(issue.body or ""),
        created_at = issue.createdAt or issue.created_at,
        updated_at = issue.updatedAt or issue.updated_at,
        labels = shared.label_names(M, issue.labels),
        assignees = m_claims.assignee_logins(M, issue.assignees),
        author_login = m_claims.issue_author_login(M, issue),
      })
    end
  end)
  return issues
end

function C.parse_issue_list_recent_closed(M, stdout)
  local decoded = json.decode(stdout or "[]")
  local issues = {}
  if type(decoded) ~= "table" then
    error("github-devloop: recent closed issue list decode failed")
  end
  shared.each_paginated_item(M, decoded, function(issue)
    local number = type(issue) == "table" and tonumber(issue.number) or nil
    local title = type(issue) == "table" and issue.title or nil
    local closed_at = type(issue) == "table" and (issue.closedAt or issue.closed_at) or nil
    if number == nil or title == nil or closed_at == nil or type(issue.labels) ~= "table" then
      error("github-devloop: recent closed issue list item missing required fields")
    end
    table.insert(issues, {
      number = number,
      title = tostring(title),
      closed_at = tostring(closed_at),
      closedAt = tostring(closed_at),
      labels = shared.label_names(M, issue.labels),
    })
  end)
  return issues
end

function C.parse_issue_number_list(M, stdout)
  local decoded = json.decode(stdout or "[]")
  local issues = {}
  if type(decoded) ~= "table" then
    return issues
  end
  shared.each_paginated_item(M, decoded, function(issue)
    local number = type(issue) == "table" and tonumber(issue.number) or nil
    if number ~= nil then
      table.insert(issues, {
        number = number,
      })
    end
  end)
  return issues
end

function C.parse_issue_list_observe(M, stdout)
  local issues = shared.parse_numbered_list(M, stdout)
  local decoded = json.decode(stdout or "[]")
  local by_number = {}
  shared.each_paginated_item(M, decoded, function(item)
    if type(item) == "table" and tonumber(item.number) ~= nil then
      by_number[tostring(tonumber(item.number))] = item.title
    end
  end)
  for _, issue in ipairs(issues) do
    issue.title = by_number[tostring(issue.number)]
  end
  return issues
end

function C.parse_issue_view_result(M, stdout)
  local decoded = json.decode(stdout or "{}")
  local state = C.issue_state_from_json(M, decoded)

  return {
    labels = state.labels,
    comments = state.comments,
    assignees = m_claims.assignee_logins(M, decoded.assignees),
    author_login = state.author_login,
  }
end

function C.parse_issue_view_loop(M, stdout)
  local decoded = json.decode(stdout or "{}")
  local result = C.parse_issue_view_result(M, stdout)
  return {
    title = tostring(decoded.title or ""),
    created_at = decoded.createdAt or decoded.created_at,
    updated_at = decoded.updatedAt or decoded.updated_at,
    state = decoded.state,
    labels = result.labels,
    comments = result.comments,
    assignees = result.assignees,
    author_login = result.author_login,
  }
end

function C.parse_issue_view_intake_judge(M, stdout)
  local decoded = json.decode(stdout or "{}")
  local result = C.parse_issue_view_result(M, stdout)
  return {
    title = tostring(decoded.title or ""),
    body = tostring(decoded.body or ""),
    created_at = decoded.createdAt or decoded.created_at,
    updated_at = decoded.updatedAt or decoded.updated_at,
    state = decoded.state,
    labels = result.labels,
    comments = result.comments,
    assignees = result.assignees,
    author_login = result.author_login,
  }
end

function C.parse_issue_view_meta(M, stdout)
  local decoded = json.decode(stdout or "{}")
  local result = C.parse_issue_view_result(M, stdout)
  return {
    title = tostring(decoded.title or ""),
    labels = result.labels,
    comments = result.comments,
  }
end

function C.parse_issue_view_implement(M, stdout)
  local decoded = json.decode(stdout or "{}")
  local result = C.parse_issue_view_meta(M, stdout)
  result.body = tostring(decoded.body or "")
  result.state = decoded.state
  result.author_login = m_claims.issue_author_login(M, decoded)
  return result
end

function C.parse_issue_view_open_pr(M, stdout)
  local decoded = json.decode(stdout or "{}")
  local result = C.parse_issue_view_result(M, stdout)
  return {
    title = tostring(decoded.title or ""),
    labels = result.labels,
    comments = result.comments,
    assignees = result.assignees,
    author_login = result.author_login,
  }
end

function C.parse_issue_view_reviewing(M, stdout)
  return C.parse_issue_view_result(M, stdout)
end

function C.parse_issue_view_review(M, stdout)
  local decoded = json.decode(stdout or "{}")
  local result = C.parse_issue_view_meta(M, stdout)
  result.assignees = m_claims.assignee_logins(M, decoded.assignees)
  result.author_login = m_claims.issue_author_login(M, decoded)
  return result
end

function C.parse_issue_view_decompose(M, stdout)
  local decoded = json.decode(stdout or "{}")
  local result = C.parse_issue_view_result(M, stdout)
  return {
    title = tostring(decoded.title or ""),
    body = tostring(decoded.body or ""),
    labels = result.labels,
    comments = result.comments,
    assignees = result.assignees,
    author_login = result.author_login,
  }
end

function C.parse_issue_view_fix(M, stdout)
  return C.parse_issue_view_meta(M, stdout)
end

function C.parse_issue_view_review_loop(M, stdout)
  local decoded = json.decode(stdout or "{}")
  local result = C.parse_issue_view_meta(M, stdout)
  result.assignees = m_claims.assignee_logins(M, decoded.assignees)
  result.author_login = m_claims.issue_author_login(M, decoded)
  return result
end

function C.parse_issue_view_merge(M, stdout)
  local decoded = json.decode(stdout or "{}")
  local result = C.parse_issue_view_result(M, stdout)
  result.title = tostring(decoded.title or "")
  result.state = decoded.state
  return result
end

function C.parse_issue_view_observe(M, stdout)
  local decoded = json.decode(stdout or "{}")
  return {
    title = tostring(decoded.title or ""),
    created_at = decoded.createdAt or decoded.created_at,
    state = decoded.state,
    state_reason = decoded.stateReason or decoded.state_reason,
    comments = parsers_misc.comments_from_json(M, decoded.comments),
    assignees = m_claims.assignee_logins(M, decoded.assignees),
    author_login = m_claims.issue_author_login(M, decoded),
  }
end

return C
