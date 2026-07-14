local S = {}
local github_view = require("forge.github_view")

function S.install(M)
local json_value = github_view.json_value
local rest_state = github_view.rest_state
local rest_pr_state = github_view.rest_pr_state
local append_comments = github_view.append_comments
local labels_json = github_view.labels_json
local assignees_json = github_view.assignees_json
local repo_name_with_owner = github_view.repo_name_with_owner
local repo_owner_login = github_view.repo_owner_login
local decode_comments_json = function(stdout) return github_view.decode_comments_json(stdout, "github-proxy: REST") end

local function decode_json(stdout)
  local ok, decoded = pcall(json.decode, stdout)
  if ok and type(decoded) == "table" then
    return decoded
  end
  error("github-proxy: REST response is not valid JSON")
end

local function decode_entity_json(stdout)
  if stdout == nil or stdout == "" then
    error("github-proxy: REST entity response is empty")
  end
  return decode_json(stdout)
end

local function comments_json(comments)
  local parts = {}
  for _, comment in ipairs(comments or {}) do
    if type(comment) == "table" then
      local author_login = nil
      if type(comment.author) == "table" then
        author_login = comment.author.login
      elseif type(comment.user) == "table" then
        author_login = comment.user.login
      end
      local id = comment.databaseId or comment.database_id or comment.id
      table.insert(parts, '{"id":' .. json_value(id)
        .. ',"body":' .. json_value(comment.body)
        .. ',"author":{"login":' .. json_value(author_login) .. "}}")
    end
  end
  return "[" .. table.concat(parts, ",") .. "]"
end

local function object_field(name, value)
  if value == nil then
    return ""
  end
  return ',"' .. tostring(name) .. '":' .. json_value(value)
end

function M.github_issue_rest_view(repo, issue_number, timeout)
  return M.github().issue_rest_view(repo, issue_number, timeout or 30)
end

function M.gh_issue_rest_view_cmd(repo, issue_number)
  return function(timeout)
    return M.github_issue_rest_view(repo, issue_number, timeout)
  end
end

function M.github_pr_rest_view(repo, pr_number, timeout)
  return M.github().pr_rest_view(repo, pr_number, timeout or 30)
end

function M.gh_pr_rest_view_cmd(repo, pr_number)
  return function(timeout)
    return M.github_pr_rest_view(repo, pr_number, timeout)
  end
end

function M.github_issue_comments_api(repo, issue_number, timeout)
  return M.github().issue_comments(repo, issue_number, timeout or 30)
end

function M.gh_issue_comments_api_cmd(repo, issue_number)
  return function(timeout)
    return M.github_issue_comments_api(repo, issue_number, timeout)
  end
end

function M.rest_comments_to_view_json(comments_stdout)
  local decoded = decode_comments_json(comments_stdout)
  local comments = {}
  append_comments(comments, decoded)
  return comments_json(comments)
end

function M.rest_issue_to_view_json(issue_stdout, comments_stdout)
  local issue = decode_entity_json(issue_stdout)
  local comment_source = comments_stdout
  if type(issue.comments) == "table" then
    comment_source = '{"comments":' .. comments_json(issue.comments) .. "}"
  end
  return '{"title":' .. json_value(issue.title)
    .. ',"body":' .. json_value(issue.body)
    .. ',"labels":' .. labels_json(issue.labels)
    .. ',"state":' .. json_value(rest_state(issue.state))
    .. ',"updatedAt":' .. json_value(issue.updated_at or issue.updatedAt)
    .. ',"assignees":' .. assignees_json(issue.assignees)
    .. ',"comments":' .. M.rest_comments_to_view_json(comment_source)
    .. "}"
end

function M.rest_pr_to_view_json(pr_stdout, comments_stdout)
  local pr = decode_entity_json(pr_stdout)
  local head = type(pr.head) == "table" and pr.head or {}
  local base = type(pr.base) == "table" and pr.base or {}
  local head_repo = type(head.repo) == "table" and head.repo or nil
  local base_repo = type(base.repo) == "table" and base.repo or nil
  local head_name_with_owner = repo_name_with_owner(head_repo)
  local base_name_with_owner = repo_name_with_owner(base_repo)
  if head_name_with_owner == nil or base_name_with_owner == nil then
    error("github-proxy: REST PR view missing repository facts")
  end
  local is_cross_repository = tostring(head_name_with_owner):lower() ~= tostring(base_name_with_owner):lower()
  local comment_source = comments_stdout
  if type(pr.comments) == "table" then
    comment_source = '{"comments":' .. comments_json(pr.comments) .. "}"
  end
  local head_repository = "{}"
  if head_name_with_owner ~= nil then
    head_repository = '{"nameWithOwner":' .. json_value(head_name_with_owner) .. "}"
  end
  local head_repository_owner = "{}"
  local owner_login = repo_owner_login(head_repo)
  if owner_login ~= nil then
    head_repository_owner = '{"login":' .. json_value(owner_login) .. "}"
  end
  return '{"headRefName":' .. json_value(head.ref)
    .. ',"headRefOid":' .. json_value(head.sha)
    .. ',"baseRefName":' .. json_value(base.ref)
    .. ',"state":' .. json_value(rest_pr_state(pr))
    .. ',"updatedAt":' .. json_value(pr.updated_at or pr.updatedAt)
    .. ',"headRepository":' .. head_repository
    .. ',"headRepositoryOwner":' .. head_repository_owner
    .. object_field("isCrossRepository", is_cross_repository)
    .. ',"labels":' .. labels_json(pr.labels)
    .. ',"comments":' .. M.rest_comments_to_view_json(comment_source)
    .. "}"
end

function M.fetch_rest_issue_view(repo, issue_number)
  local issue = M.gh_exec(function(timeout)
    return M.github_issue_rest_view(repo, issue_number, timeout)
  end, 30, "GitHub issue REST view")
  local comments = M.gh_exec(function(timeout)
    return M.github_issue_comments_api(repo, issue_number, timeout)
  end, 30, "GitHub issue comments")
  local ok, view_json = pcall(M.rest_issue_to_view_json, issue.stdout, comments.stdout)
  if not ok then
    return {
      stdout = "",
      stderr = tostring(view_json),
      exit_code = 1,
    }
  end
  return {
    stdout = view_json,
    stderr = "",
    exit_code = 0,
  }
end

function M.fetch_rest_pr_view(repo, pr_number)
  local pr = M.gh_exec(function(timeout)
    return M.github_pr_rest_view(repo, pr_number, timeout)
  end, 30, "GitHub PR REST view")
  local comments = M.gh_exec(function(timeout)
    return M.github_issue_comments_api(repo, pr_number, timeout)
  end, 30, "GitHub PR comments")
  local ok, view_json = pcall(M.rest_pr_to_view_json, pr.stdout, comments.stdout)
  if not ok then
    return {
      stdout = "",
      stderr = tostring(view_json),
      exit_code = 1,
    }
  end
  return {
    stdout = view_json,
    stderr = "",
    exit_code = 0,
  }
end

end

return S
