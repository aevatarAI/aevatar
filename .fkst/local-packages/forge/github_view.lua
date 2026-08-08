local M = {}

function M.parse_view_updated_at(stdout)
  local ok, decoded = pcall(json.decode, stdout or "")
  if not ok or type(decoded) ~= "table" then
    return nil
  end
  local updated_at = decoded.updatedAt or decoded.updated_at
  if updated_at == nil or tostring(updated_at) == "" then
    return nil
  end
  return tostring(updated_at)
end

function M.parse_updated_at_stdout(stdout)
  local text = tostring(stdout or "")
  text = text:gsub("^%s+", ""):gsub("%s+$", "")
  if text == "" then
    return nil
  end
  return text
end

function M.json_string(value)
  local text = tostring(value or "")
  text = text:gsub("\\", "\\\\")
  text = text:gsub('"', '\\"')
  text = text:gsub("\b", "\\b")
  text = text:gsub("\f", "\\f")
  text = text:gsub("\n", "\\n")
  text = text:gsub("\r", "\\r")
  text = text:gsub("\t", "\\t")
  text = text:gsub("[%z\1-\31]", function(char)
    return string.format("\\u%04X", string.byte(char))
  end)
  return '"' .. text .. '"'
end

function M.json_value(value)
  if value == nil then
    return "null"
  end
  if type(value) == "boolean" then
    return value and "true" or "false"
  end
  if type(value) == "number" then
    return tostring(value)
  end
  return M.json_string(value)
end

function M.rest_state(value)
  if value == nil then
    return nil
  end
  return tostring(value):upper()
end

function M.rest_pr_state(pr)
  if type(pr) ~= "table" then
    return nil
  end
  local merged_at = pr.merged_at
  if pr.merged == true or (type(merged_at) == "string" and merged_at ~= "") then
    return "MERGED"
  end
  return M.rest_state(pr.state)
end

function M.append_comments(target, value)
  if type(value) ~= "table" then
    return
  end
  if type(value.comments) == "table" then
    M.append_comments(target, value.comments)
    return
  end
  if value.id ~= nil or value.body ~= nil or value.user ~= nil or value.author ~= nil then
    table.insert(target, value)
    return
  end
  for _, item in ipairs(value) do
    M.append_comments(target, item)
  end
end

function M.decode_comments_json(stdout, error_context)
  local source = stdout
  if source == nil or source == "" then
    source = "[]"
  end
  local ok, decoded = pcall(json.decode, source)
  if ok and type(decoded) == "table" then
    return decoded
  end
  error(tostring(error_context or "github_view: REST") .. " response is not valid JSON")
end

function M.labels_json(labels)
  local parts = {}
  for _, label in ipairs(labels or {}) do
    if type(label) == "table" then
      table.insert(parts, '{"name":' .. M.json_value(label.name) .. "}")
    elseif label ~= nil then
      table.insert(parts, '{"name":' .. M.json_value(label) .. "}")
    end
  end
  return "[" .. table.concat(parts, ",") .. "]"
end

function M.label_names(labels_json)
  local labels = {}
  for _, label in ipairs(labels_json or {}) do
    if type(label) == "table" and label.name ~= nil then
      table.insert(labels, tostring(label.name))
    elseif type(label) == "string" then
      table.insert(labels, label)
    end
  end
  return labels
end

function M.assignees_json(assignees)
  local parts = {}
  for _, assignee in ipairs(assignees or {}) do
    if type(assignee) == "table" then
      table.insert(parts, '{"login":' .. M.json_value(assignee.login) .. "}")
    elseif assignee ~= nil then
      table.insert(parts, '{"login":' .. M.json_value(assignee) .. "}")
    end
  end
  return "[" .. table.concat(parts, ",") .. "]"
end

function M.repo_name_with_owner(repo)
  if type(repo) ~= "table" then
    return nil
  end
  if repo.full_name ~= nil and tostring(repo.full_name) ~= "" then
    return tostring(repo.full_name)
  end
  if repo.nameWithOwner ~= nil and tostring(repo.nameWithOwner) ~= "" then
    return tostring(repo.nameWithOwner)
  end
  if type(repo.owner) == "table" and repo.owner.login ~= nil and repo.name ~= nil then
    return tostring(repo.owner.login) .. "/" .. tostring(repo.name)
  end
  return nil
end

function M.repo_owner_login(repo)
  if type(repo) == "table" and type(repo.owner) == "table" and repo.owner.login ~= nil then
    return tostring(repo.owner.login)
  end
  local name_with_owner = M.repo_name_with_owner(repo)
  return name_with_owner and name_with_owner:match("^([^/]+)/") or nil
end

return M
