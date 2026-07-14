local M = {}
local core = require("core")

local function encode_json_string(value)
  return tostring(value or "")
    :gsub("\\", "\\\\")
    :gsub('"', '\\"')
    :gsub("\b", "\\b")
    :gsub("\f", "\\f")
    :gsub("\n", "\\n")
    :gsub("\r", "\\r")
    :gsub("\t", "\\t")
    :gsub("[%z\1-\31]", function(char)
      return string.format("\\u%04X", string.byte(char))
    end)
end

local function encode_json_value(value)
  if value == nil then
    return "null"
  end
  if type(value) == "boolean" then
    return value and "true" or "false"
  end
  if type(value) == "number" then
    return tostring(value)
  end
  return '"' .. encode_json_string(value) .. '"'
end

local function shell_quote(value)
  return "'" .. tostring(value):gsub("'", "'\\''") .. "'"
end

local function owner_login(repo)
  return tostring(repo or "owner/repo"):match("^([^/]+)/") or "owner"
end

local function encode_labels_json(labels)
  local rendered = {}
  for _, label in ipairs(labels or {}) do
    local name = type(label) == "table" and label.name or label
    table.insert(rendered, '{"name":' .. encode_json_value(name) .. "}")
  end
  return table.concat(rendered, ",")
end

local function encode_assignees_json(assignees)
  local rendered = {}
  for _, assignee in ipairs(assignees or {}) do
    local login = type(assignee) == "table" and assignee.login or assignee
    table.insert(rendered, '{"login":' .. encode_json_value(login) .. "}")
  end
  return table.concat(rendered, ",")
end

local function fixture_comment_body(comment)
  if type(comment) == "table" then
    return comment.body
  end
  return comment
end

local function comment_author(comment)
  if type(comment) == "table" then
    if comment.author_login ~= nil then return comment.author_login end
    if type(comment.author) == "table" then return comment.author.login end
    if type(comment.user) == "table" then return comment.user.login end
  end
  return "fkst-test-bot"
end

local function comment_id(comment)
  if type(comment) ~= "table" then
    return nil
  end
  return comment.id or comment.databaseId or comment.database_id
end

local function comment_created_at(comment)
  if type(comment) == "table" then
    return comment.created_at or comment.createdAt
  end
  return nil
end

function M.view_comment_json(comment)
  if type(comment) == "string" and comment:match("^%s*{") then
    return comment
  end
  local id = comment_id(comment)
  local id_field = id ~= nil and tostring(id) ~= "" and '"id":' .. encode_json_value(id) .. "," or ""
  return "{" .. id_field
    .. '"body":' .. encode_json_value(fixture_comment_body(comment))
    .. ',"author":{"login":' .. encode_json_value(comment_author(comment)) .. "}"
    .. ',"createdAt":' .. encode_json_value(comment_created_at(comment) or "2026-06-03T01:00:00Z")
    .. "}"
end

local function rest_comment_json(comment, index)
  if type(comment) == "string" and comment:match("^%s*{") then
    local body = comment:match('"body"%s*:%s*"([^"]*)"') or ""
    local author = comment:match('"author"%s*:%s*{%s*"login"%s*:%s*"([^"]*)"') or "fkst-test-bot"
    local created_at = comment:match('"createdAt"%s*:%s*"([^"]*)"') or "2026-06-03T01:00:00Z"
    return '{"id":' .. encode_json_value(index)
      .. ',"body":' .. encode_json_value(body)
      .. ',"user":{"login":' .. encode_json_value(author) .. "}"
      .. ',"created_at":' .. encode_json_value(created_at)
      .. "}"
  end
  return '{"id":' .. encode_json_value(comment_id(comment) or index)
    .. ',"body":' .. encode_json_value(fixture_comment_body(comment))
    .. ',"user":{"login":' .. encode_json_value(comment_author(comment)) .. "}"
    .. ',"created_at":' .. encode_json_value(comment_created_at(comment) or "2026-06-03T01:00:00Z")
    .. "}"
end

function M.view_comments_json(comments)
  local rendered = {}
  for _, comment in ipairs(comments or {}) do
    table.insert(rendered, M.view_comment_json(comment))
  end
  return table.concat(rendered, ",")
end

local function rest_comments_json(comments)
  local rendered = {}
  for index, comment in ipairs(comments or {}) do
    table.insert(rendered, rest_comment_json(comment, index))
  end
  return table.concat(rendered, ",")
end

function M.issue_view_stdout(fields)
  local f = fields or {}
  return string.format(
    '{"number":%d,"title":"%s","body":"%s","createdAt":"%s","updatedAt":"%s","state":"%s","labels":[%s],"comments":[%s],"assignees":[%s],"author":{"login":"%s"}}\n',
    tonumber(f.number) or 42,
    encode_json_string(f.title or "Implement decision recorder"),
    encode_json_string(f.body or ""),
    encode_json_string(f.created_at or "2026-06-03T01:00:00Z"),
    encode_json_string(f.updated_at or "2026-06-03T01:02:03Z"),
    encode_json_string(f.state or "OPEN"),
    encode_labels_json(f.labels),
    M.view_comments_json(f.comments),
    encode_assignees_json(f.assignees or { "fkst-test-bot" }),
    encode_json_string(f.author_login or "fkst-test-bot")
  )
end

function M.pr_view_stdout(fields)
  local f = fields or {}
  local owner = f.head_repository_owner or owner_login(f.head_repo or f.repo or "owner/repo")
  local state = tostring(f.state or "OPEN")
  local merged_at = f.merged_at or (state == "MERGED" and "2026-06-03T02:05:04Z" or "")
  local merge_commit_sha = f.merge_commit_sha or (state == "MERGED" and (f.head_sha or "def456") or nil)
  return string.format(
    '{"number":%d,"headRefName":"%s","headRefOid":"%s","baseRefName":"%s","baseRefOid":"%s","state":"%s","updatedAt":"%s","isDraft":%s,"merged":%s,"mergedAt":"%s","mergeCommit":{"oid":%s},"comments":[%s],"labels":[%s],"headRepository":{"nameWithOwner":"%s","owner":{"login":"%s"}},"headRepositoryOwner":{"login":"%s"},"isCrossRepository":%s,"mergeable":"%s","mergeStateStatus":"%s"%s}\n',
    tonumber(f.number) or 7,
    encode_json_string(f.head or "devloop-owner-repo-42-01HY"),
    encode_json_string(f.head_sha or "def456"),
    encode_json_string(f.base_branch or "dev"),
    encode_json_string(f.base_sha or "abc123"),
    encode_json_string(state),
    encode_json_string(f.updated_at or "2026-06-03T02:03:04Z"),
    f.is_draft == true and "true" or "false",
    state == "MERGED" and "true" or "false",
    encode_json_string(merged_at),
    encode_json_value(merge_commit_sha),
    M.view_comments_json(f.comments),
    encode_labels_json(f.labels),
    encode_json_string(f.head_repo or f.repo or "owner/repo"),
    encode_json_string(owner),
    encode_json_string(owner),
    f.cross_repo == true and "true" or "false",
    encode_json_string(f.mergeable or "MERGEABLE"),
    encode_json_string(f.merge_state or "CLEAN"),
    f.status_check_rollup_json ~= nil and ',"statusCheckRollup":' .. f.status_check_rollup_json or ""
  )
end

local function issue_rest_stdout(fields)
  local f = fields or {}
  return string.format(
    '{"number":%d,"title":"%s","body":"%s","state":"%s","created_at":"%s","updated_at":"%s","labels":[%s],"user":{"login":"%s"},"assignees":[%s]}\n',
    tonumber(f.number) or 42,
    encode_json_string(f.title or "Implement decision recorder"),
    encode_json_string(f.body or ""),
    encode_json_string(tostring(f.state or "OPEN"):lower()),
    encode_json_string(f.created_at or "2026-06-03T01:00:00Z"),
    encode_json_string(f.updated_at or "2026-06-03T01:02:03Z"),
    encode_labels_json(f.labels),
    encode_json_string(f.author_login or "fkst-test-bot"),
    encode_assignees_json(f.assignees or { "fkst-test-bot" })
  )
end

local function pr_rest_stdout(fields)
  local f = fields or {}
  local repo = f.repo or "owner/repo"
  local head_repo = f.head_repo or repo
  local state = tostring(f.state or "OPEN")
  local merged_at = f.merged_at or (state == "MERGED" and "2026-06-03T02:05:04Z" or "")
  local merge_commit_sha = f.merge_commit_sha or (state == "MERGED" and (f.head_sha or "def456") or nil)
  local mergeable = f.rest_mergeable
  if mergeable == nil then
    mergeable = f.mergeable
  end
  if mergeable == nil or mergeable == "MERGEABLE" then
    mergeable = true
  elseif mergeable == "CONFLICTING" then
    mergeable = false
  elseif mergeable == "UNKNOWN" then
    mergeable = nil
  end
  local mergeable_state = f.rest_mergeable_state or f.mergeable_state or f.merge_state or "clean"
  return string.format(
    '{"number":%d,"state":"%s","updated_at":"%s","merged_at":%s,"merge_commit_sha":%s,"draft":%s,"labels":[%s],"user":{"login":"%s"},"mergeable":%s,"mergeable_state":%s,"head":{"ref":"%s","sha":"%s","repo":{"full_name":"%s","owner":{"login":"%s"}}},"base":{"ref":"%s","sha":"%s","repo":{"full_name":"%s","owner":{"login":"%s"}}}}\n',
    tonumber(f.number) or 7,
    encode_json_string(state == "MERGED" and "closed" or state:lower()),
    encode_json_string(f.updated_at or "2026-06-03T02:03:04Z"),
    merged_at ~= "" and encode_json_value(merged_at) or "null",
    encode_json_value(merge_commit_sha),
    f.is_draft == true and "true" or "false",
    encode_labels_json(f.labels),
    encode_json_string(f.author_login or "fkst-test-bot"),
    mergeable == nil and "null" or (mergeable == true and "true" or "false"),
    encode_json_value(mergeable_state),
    encode_json_string(f.head or "devloop-owner-repo-42-01HY"),
    encode_json_string(f.head_sha or "def456"),
    encode_json_string(head_repo),
    encode_json_string(owner_login(head_repo)),
    encode_json_string(f.base_branch or "dev"),
    encode_json_string(f.base_sha or "abc123"),
    encode_json_string(repo),
    encode_json_string(owner_login(repo))
  )
end

local function mock_probe(t, path, updated_at)
  for _, jq in ipairs({ ".updated_at", ".updated_at // .updatedAt // \"\"" }) do
    t.mock_command("gh api " .. shell_quote(path) .. " --jq " .. shell_quote(jq), {
      stdout = tostring(updated_at or "") .. "\n",
      stderr = "",
      exit_code = 0,
    })
  end
end

local function mock_comments(t, repo, issue_number, comments, times)
  for _ = 1, times or 1 do
    t.mock_command("gh api --paginate --slurp " .. shell_quote("repos/" .. repo .. "/issues/" .. tostring(issue_number) .. "/comments?per_page=100"), {
      stdout = "[" .. rest_comments_json(comments) .. "]\n",
      stderr = "",
      exit_code = 0,
    })
  end
end

local issue_view_selectors = {
  "number,title",
  "title,body,comments,labels,state,updatedAt,assignees",
  "title,body,comments,labels,state,createdAt,updatedAt,assignees,author",
  "title,body,updatedAt,labels,comments,state",
  "title,body,createdAt,updatedAt,labels,comments,state,assignees,author",
  "title,comments,state",
  "title,labels,state,comments,assignees,author",
  "title,comments,state,stateReason,assignees,author",
  "assignees,author",
  "labels,comments",
  "title,updatedAt,labels,comments,state",
  "title,labels,comments",
  "title,labels,comments,assignees,author",
  "title,body,labels,comments",
  "title,labels,comments,state,assignees",
}

local pr_origin_selector = "title,body,headRefName,headRefOid,baseRefName,state,updatedAt,mergedAt,comments,labels,mergeable,mergeStateStatus"
local pr_origin_legacy_selector = "headRefName,headRefOid,baseRefName,state,updatedAt,comments,labels,mergeable,mergeStateStatus"
local pr_head_selector = "headRefName"
local pr_fix_selector = "headRefName,headRefOid,baseRefName,state,comments,headRepository,headRepositoryOwner,isCrossRepository"
local pr_fix_precheck_selector = "headRefName,headRefOid,baseRefName,state,updatedAt,comments,headRepository,headRepositoryOwner,isCrossRepository"
local pr_merge_selector = "headRefName,headRefOid,baseRefName,baseRefOid,state,updatedAt,isDraft,mergedAt,comments,headRepository,headRepositoryOwner,isCrossRepository,mergeable,mergeStateStatus,statusCheckRollup"
local pr_merge_without_rollup_selector = "headRefName,headRefOid,baseRefName,baseRefOid,state,updatedAt,isDraft,mergedAt,comments,headRepository,headRepositoryOwner,isCrossRepository,mergeable,mergeStateStatus"
local pr_freshness_selector = "headRefName,headRefOid,baseRefName,state,updatedAt,isDraft,comments,labels,headRepository,headRepositoryOwner,isCrossRepository,mergeable,mergeStateStatus,statusCheckRollup"
local pr_context_selector = "title,body,headRefName,headRefOid,baseRefName,state,updatedAt,comments,labels"
M.pr_origin_selector = pr_origin_selector
M.pr_origin_legacy_selector = pr_origin_legacy_selector
M.pr_head_selector = pr_head_selector
M.pr_fix_selector = pr_fix_selector
M.pr_fix_precheck_selector = pr_fix_precheck_selector
M.pr_merge_selector = pr_merge_selector
M.pr_merge_without_rollup_selector = pr_merge_without_rollup_selector
M.pr_freshness_selector = pr_freshness_selector
M.pr_context_selector = pr_context_selector

local function issue_view_command(repo, number, fields)
  return "gh issue view " .. shell_quote(number)
    .. " --repo " .. shell_quote(repo)
    .. " --json " .. fields
end

local function pr_view_command(repo, number, fields)
  return "gh pr view " .. shell_quote(number)
    .. " --repo " .. shell_quote(repo)
    .. " --json " .. fields
end

local function issue_rest_command(repo, number)
  return "gh api " .. shell_quote("repos/" .. tostring(repo) .. "/issues/" .. tostring(number))
end

local function pr_rest_command(repo, number)
  return "gh api " .. shell_quote("repos/" .. tostring(repo) .. "/pulls/" .. tostring(number))
end

local function list_labels_json(labels)
  return encode_labels_json(labels)
end

local function issue_list_item_json(issue)
  local item = type(issue) == "table" and issue or { number = issue }
  return string.format(
    '{"number":%d,"title":%s,"body":%s,"createdAt":%s,"updatedAt":%s,"closedAt":%s,"state":%s,"labels":[%s],"assignees":[%s],"author":{"login":%s},"url":%s}',
    tonumber(item.number) or 42,
    encode_json_value(item.title or "Issue"),
    encode_json_value(item.body or ""),
    encode_json_value(item.created_at or "2026-06-03T01:00:00Z"),
    encode_json_value(item.updated_at or "2026-06-03T01:02:03Z"),
    encode_json_value(item.closed_at or item.closedAt or "2026-06-04T01:02:03Z"),
    encode_json_value(item.state or "OPEN"),
    list_labels_json(item.labels),
    encode_assignees_json(item.assignees or { "fkst-test-bot" }),
    encode_json_value(item.author_login or "fkst-test-bot"),
    encode_json_value(item.url or ("https://github.example/owner/repo/issues/" .. tostring(item.number or 42)))
  )
end

local function pr_list_item_json(pr)
  local item = type(pr) == "table" and pr or { number = pr }
  return string.format(
    '{"number":%d,"title":%s,"state":%s,"labels":[%s],"base":{"ref":%s},"head":{"ref":%s,"sha":%s}}',
    tonumber(item.number) or 7,
    encode_json_value(item.title or "PR"),
    encode_json_value(item.state or "open"),
    list_labels_json(item.labels),
    encode_json_value(item.base_branch or "dev"),
    encode_json_value(item.head or "devloop-owner-repo-42-01HY"),
    encode_json_value(item.head_sha or "def456")
  )
end

local function list_stdout(items, render_item)
  local rendered = {}
  for _, item in ipairs(items or {}) do
    table.insert(rendered, render_item(item))
  end
  return "[" .. table.concat(rendered, ",") .. "]\n"
end

local function register_view_commands(t, commands, stdout, times)
  local seen = {}
  for _, command in ipairs(commands or {}) do
    if command ~= nil and not seen[command] then
      seen[command] = true
      for _ = 1, times or 1 do
        t.mock_command(command, {
          stdout = stdout,
          stderr = "",
          exit_code = 0,
        })
      end
    end
  end
end

local function register_command_result(t, command, result, times)
  for _ = 1, times or 1 do
    t.mock_command(command, {
      stdout = result.stdout or "",
      stderr = result.stderr or "",
      exit_code = result.exit_code or 0,
    })
  end
end

function M.mock_issue_list_command(t, command, issues, times)
  register_view_commands(t, { command }, list_stdout(issues, issue_list_item_json), times or 1)
end

function M.mock_pr_list_command(t, command, prs, times)
  register_view_commands(t, { command }, list_stdout(prs, pr_list_item_json), times or 1)
end

function M.mock_issue_list_raw_command(t, command, result, times)
  register_command_result(t, command, result or {}, times or 1)
end

function M.mock_pr_list_raw_command(t, command, result, times)
  register_command_result(t, command, result or {}, times or 1)
end

function M.mock_issue_board_digest_list_raw(t, repo, result, times)
  M.mock_issue_list_raw_command(t, core.gh_issue_list_board_digest_cmd(repo), result, times)
end

function M.mock_pr_board_digest_list_raw(t, repo, result, times)
  M.mock_pr_list_raw_command(t, core.gh_pr_list_board_digest_cmd(repo), result, times)
end

function M.mock_issue_board_digest_list(t, repo, issues, times)
  M.mock_issue_list_command(t, core.gh_issue_list_board_digest_cmd(repo), issues, times)
end

function M.mock_pr_board_digest_list(t, repo, prs, times)
  M.mock_pr_list_command(t, core.gh_pr_list_board_digest_cmd(repo), prs, times)
end

function M.mock_issue_view_selector(t, fields, selector, times)
  local f = fields or {}
  local repo = f.repo or "owner/repo"
  local number = f.number or 42
  register_view_commands(t, {
    issue_view_command(repo, number, selector),
  }, M.issue_view_stdout(f), times or 1)
  if selector == "title,body,comments,labels,state,createdAt,updatedAt,assignees,author" then
    register_view_commands(t, {
      issue_rest_command(repo, number),
    }, issue_rest_stdout(f), times or 1)
    mock_comments(t, repo, number, f.comments, times or 1)
  end
end

function M.mock_issue_view_raw_selector(t, fields, selector, result, times)
  local f = fields or {}
  register_command_result(t, issue_view_command(f.repo or "owner/repo", f.number or 42, selector), result or {}, times or 1)
end

function M.mock_pr_view_selector(t, fields, selector, times)
  local f = fields or {}
  local repo = f.repo or "owner/repo"
  local number = f.number or 7
  register_view_commands(t, {
    pr_view_command(repo, number, selector),
  }, M.pr_view_stdout(f), times or 1)
  if selector == pr_origin_selector or selector == pr_origin_legacy_selector then
    register_view_commands(t, {
      pr_rest_command(repo, number),
    }, pr_rest_stdout(f), times or 1)
    mock_comments(t, repo, number, f.comments, times or 1)
  end
end

function M.mock_pr_view_raw_selector(t, fields, selector, result, times)
  local f = fields or {}
  register_command_result(t, pr_view_command(f.repo or "owner/repo", f.number or 7, selector), result or {}, times or 1)
end

function M.mock_issue_read_forms(t, fields)
  local f = fields or {}
  local repo = f.repo or "owner/repo"
  local number = f.number or 42
  local updated_at = f.updated_at or "2026-06-03T01:02:03Z"
  local path = "repos/" .. repo .. "/issues/" .. tostring(number)
  if f.register_all_views == true then
    local stdout = M.issue_view_stdout(f)
    local times = f.times or 30
    local commands = {}
    for _, selector in ipairs(issue_view_selectors) do
      table.insert(commands, issue_view_command(repo, number, selector))
    end
    register_view_commands(t, commands, stdout, times)
  end
  mock_probe(t, path, updated_at)
  register_view_commands(t, {
    "gh api " .. shell_quote(path),
  }, issue_rest_stdout(f), f.times or 30)
  mock_comments(t, repo, number, f.comments, f.times or 30)
end

function M.mock_pr_read_forms(t, fields)
  local f = fields or {}
  local repo = f.repo or "owner/repo"
  local number = f.number or 7
  local updated_at = f.updated_at or "2026-06-03T02:03:04Z"
  local path = "repos/" .. repo .. "/pulls/" .. tostring(number)
  local times = f.times or 1
  if f.register_all_views == true then
    local stdout = M.pr_view_stdout(f)
    local commands = {
      pr_view_command(repo, number, pr_origin_selector),
      pr_view_command(repo, number, pr_origin_legacy_selector),
      pr_view_command(repo, number, "headRefName,headRefOid,baseRefName,state,comments"),
      pr_view_command(repo, number, pr_fix_selector),
      pr_view_command(repo, number, pr_fix_precheck_selector),
      pr_view_command(repo, number, "headRefName"),
      pr_view_command(repo, number, pr_context_selector),
      pr_view_command(repo, number, pr_freshness_selector),
    }
    if f.register_origin_view == false then
      table.remove(commands, 1)
      table.remove(commands, 1)
    end
    register_view_commands(t, commands, stdout, times)
  end
  if f.register_merge_views ~= false and (f.status_check_rollup_json ~= nil or f.merge_view == true or f.register_all_views == true) then
    register_view_commands(t, {
      pr_view_command(repo, number, pr_merge_selector),
      pr_view_command(repo, number, pr_merge_without_rollup_selector),
    }, M.pr_view_stdout(f), times)
  end
  mock_probe(t, path, updated_at)
  register_view_commands(t, {
    "gh api " .. shell_quote(path),
  }, pr_rest_stdout(f), f.times or 30)
  mock_comments(t, repo, number, f.comments, f.times or 30)
end

function M.mock_pr_merge_view(t, fields, times)
  local f = fields or {}
  f.merge_view = true
  f.register_merge_views = false
  M.mock_pr_read_forms(t, f)
  M.mock_pr_view_selector(t, f, pr_merge_selector, times or f.times or 1)
end

function M.mock_default_pr_read(t, comments)
  local fields = {
    repo = "owner/repo",
    number = 7,
    comments = comments,
    head = "devloop-owner-repo-42-01HY",
    head_sha = "def456",
    state = "OPEN",
    base_branch = "dev",
    labels = {},
    times = 30,
  }
  M.mock_pr_read_forms(t, fields)
  M.mock_pr_view_selector(t, fields, pr_origin_selector, fields.times)
end

function M.mock_issue_read_with_defaults(t, labels, comments, extra)
  local fields = extra or {}
  M.mock_issue_read_forms(t, {
    repo = fields.repo,
    number = fields.number,
    title = fields.title,
    body = fields.body,
    created_at = fields.created_at,
    updated_at = fields.updated_at,
    state = fields.state,
    labels = labels,
    comments = comments,
    assignees = fields.assignees,
    author_login = fields.author_login,
    times = fields.times,
    register_all_views = fields.register_all_views == true,
  })
end

return M
