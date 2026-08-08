local M = {}
local argv_render = require("forge.argv")
local github_view = require("forge.github_view")
local stdout_policy = require("forge.github.stdout_policy")
local append_comments = github_view.append_comments
local rest_state = github_view.rest_state
local json_string = github_view.json_string
local json_value = github_view.json_value
local labels_json = github_view.labels_json
local label_names = github_view.label_names
local assignees_json = github_view.assignees_json
local parse_view_updated_at = github_view.parse_view_updated_at
local parse_updated_at_stdout = github_view.parse_updated_at_stdout
-- The neutral Issue shape this op returns is everything `gh issue view --json` provides.
-- Two documented exclusions: `blocked_by` needs GraphQL (a separate read op, not gh issue
-- view), and comment `updated_at` is not exposed by gh issue view (only createdAt).
local issue_view_fields = "number,title,body,url,updatedAt,state,labels,comments,assignees,author"
local max_cache_key_segment_len = 120

local function sanitize_cache_segment(value, allow_slash)
  local pattern = allow_slash and "[^%w%._%-%/]" or "[^%w%._%-]"
  local safe = tostring(value or ""):gsub(pattern, "-")
  safe = safe:gsub("-+", "-")
  if allow_slash then
    safe = safe:gsub("/+", "/"):gsub("^/+", ""):gsub("/+$", "")
  else
    safe = safe:gsub("^-+", ""):gsub("-+$", "")
  end
  local segments = {}
  for segment in safe:gmatch("[^/]+") do
    if segment == "." or segment == ".." then
      segment = "-"
    end
    table.insert(segments, segment)
  end
  safe = table.concat(segments, allow_slash and "/" or "-")
  if #safe > max_cache_key_segment_len then
    safe = safe:sub(1, max_cache_key_segment_len):gsub("/+$", ""):gsub("-+$", "")
  end
  if safe == "" then
    return "empty"
  end
  return safe
end

local function issue_view_cache_key(repo, number)
  return "github-proxy/view-v2/"
    .. sanitize_cache_segment(repo, true)
    .. "/issue/"
    .. sanitize_cache_segment(number, false)
end

local function gh_issue_view_argv(repo, issue_number, fields)
  local selected_fields = tostring(fields or "")
  if selected_fields == "" or selected_fields:match("[^%w_,]") or selected_fields:match("^,") or selected_fields:match(",$") or selected_fields:match(",,") then
    error("forge.github: invalid issue view fields")
  end
  return { "gh", "issue", "view", tostring(issue_number), "--repo", tostring(repo), "--json", selected_fields }
end

local function gh_issue_view_full_argv(repo, issue_number)
  return gh_issue_view_argv(repo, issue_number, issue_view_fields)
end

local function render_issue_view_argv(argv)
  return table.concat({
    tostring(argv[1]),
    tostring(argv[2]),
    tostring(argv[3]),
    argv_render.shell_single_quote(argv[4]),
    tostring(argv[5]),
    argv_render.shell_single_quote(argv[6]),
    tostring(argv[7]),
    tostring(argv[8]),
  }, " ")
end

local function gh_issue_rest_argv(repo, issue_number)
  return { "gh", "api", "repos/" .. tostring(repo) .. "/issues/" .. tostring(issue_number) }
end

local function gh_issue_comments_rest_argv(repo, issue_number)
  return {
    "gh",
    "api",
    "--paginate",
    "--slurp",
    "repos/" .. tostring(repo) .. "/issues/" .. tostring(issue_number) .. "/comments?per_page=100",
  }
end

local function gh_issue_edit_assignee_argv(repo, issue_number, flag, login)
  return {
    "gh",
    "issue",
    "edit",
    tostring(issue_number),
    "--repo",
    tostring(repo),
    flag,
    tostring(login),
  }
end

local function gh_issue_edit_label_argv(repo, issue_number, flag, label)
  return {
    "gh",
    "issue",
    "edit",
    tostring(issue_number),
    "--repo",
    tostring(repo),
    flag,
    tostring(label),
  }
end

local function gh_issue_updated_at_argv(repo, issue_number)
  return {
    "gh",
    "api",
    "repos/" .. tostring(repo) .. "/issues/" .. tostring(issue_number),
    "--jq",
    ".updated_at // .updatedAt // \"\"",
  }
end

local function gh_issue_add_sub_issue_argv(repo, parent_issue_number, sub_issue_id)
  return {
    "gh",
    "api",
    "--method",
    "POST",
    "repos/" .. tostring(repo) .. "/issues/" .. tostring(parent_issue_number) .. "/sub_issues",
    "-F",
    "sub_issue_id=" .. tostring(sub_issue_id),
  }
end

local function gh_issue_sub_issues_argv(repo, parent_issue_number)
  return {
    "gh",
    "api",
    "--paginate",
    "--slurp",
    "repos/" .. tostring(repo) .. "/issues/" .. tostring(parent_issue_number) .. "/sub_issues?per_page=100",
  }
end

local function assignee_logins(assignees)
  local logins = {}
  for _, assignee in ipairs(assignees or {}) do
    if type(assignee) == "table" and assignee.login ~= nil then
      table.insert(logins, tostring(assignee.login))
    elseif type(assignee) == "string" then
      table.insert(logins, assignee)
    end
  end
  return logins
end

local function issue_author_login(decoded)
  if type(decoded.author) == "table" and decoded.author.login ~= nil then
    return tostring(decoded.author.login)
  end
  if type(decoded.user) == "table" and decoded.user.login ~= nil then
    return tostring(decoded.user.login)
  end
  if decoded.author_login ~= nil then
    return tostring(decoded.author_login)
  end
  return nil
end

local function comments_from_json(comments_json)
  local comments = {}
  for _, comment in ipairs(comments_json or {}) do
    if type(comment) == "table" and comment.body ~= nil then
      local author_login = nil
      if type(comment.author) == "table" and comment.author.login ~= nil then
        author_login = tostring(comment.author.login)
      elseif type(comment.user) == "table" and comment.user.login ~= nil then
        author_login = tostring(comment.user.login)
      elseif comment.author_login ~= nil then
        author_login = tostring(comment.author_login)
      end
      table.insert(comments, {
        id = comment.id,
        body = tostring(comment.body),
        author_login = author_login,
        created_at = comment.createdAt or comment.created_at,
      })
    elseif type(comment) == "string" then
      error("forge.github: issue comments must be gh-shaped objects")
    end
  end
  return comments
end

local function repo_and_number(source_ref)
  assert(type(source_ref) == "table", "read_issue requires a source_ref")
  assert(source_ref.kind == "external", "read_issue requires an external source_ref")
  local repo, number = tostring(source_ref.ref or ""):match("^([^#]+)#issue/(%d+)$")
  assert(repo ~= nil and number ~= nil, "read_issue requires an issue source_ref")
  return repo, tonumber(number)
end

local function parse_json_object(stdout, context)
  local ok, decoded = pcall(json.decode, stdout or "")
  if ok and type(decoded) == "table" then
    return decoded
  end
  error("forge.github: " .. tostring(context) .. " response is not valid JSON")
end

local function issue_database_id(stdout, context)
  local decoded = parse_json_object(stdout, context)
  local id = tonumber(decoded.id)
  if id == nil then
    error("forge.github: " .. tostring(context) .. " response is missing issue id")
  end
  return id
end

local function maybe_duplicate_sub_issue_error(err)
  local result = type(err) == "table" and err.result or nil
  local stderr = type(result) == "table" and tostring(result.stderr or ""):lower() or ""
  if stderr == "" then
    return false
  end
  local mentions_sub_issue = stderr:find("sub%-issue", 1, false) ~= nil
    or stderr:find("sub issue", 1, true) ~= nil
    or stderr:find("sub_issue", 1, true) ~= nil
  if not mentions_sub_issue then
    return false
  end
  if stderr:find("422", 1, true) == nil and stderr:find("validation failed", 1, true) == nil then
    return false
  end
  if stderr:find("another parent", 1, true) ~= nil
    or stderr:find("different parent", 1, true) ~= nil
    or stderr:find("another object", 1, true) ~= nil then
    return false
  end
  return stderr:find("already linked", 1, true) ~= nil
    or stderr:find("already a sub%-issue", 1, false) ~= nil
    or stderr:find("already a sub issue", 1, true) ~= nil
    or stderr:find("already exists", 1, true) ~= nil
    or stderr:find("duplicate", 1, true) ~= nil
end

local function append_sub_issue_nodes(nodes, value)
  if type(value) ~= "table" then
    return
  end
  if value.id ~= nil or value.node_id ~= nil or value.number ~= nil then
    table.insert(nodes, value)
  end
  for _, child in ipairs(value) do
    append_sub_issue_nodes(nodes, child)
  end
end

local function parent_has_sub_issue_id(stdout, child_id)
  local ok, decoded = pcall(json.decode, stdout or "")
  if not ok then
    return false
  end
  local nodes = {}
  append_sub_issue_nodes(nodes, decoded)
  local expected = tostring(child_id)
  for _, node in ipairs(nodes) do
    if tostring(node.id or "") == expected then
      return true
    end
  end
  return false
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
      table.insert(parts, '{"id":' .. json_value(comment.databaseId or comment.database_id or comment.id)
        .. ',"body":' .. json_value(comment.body)
        .. ',"author":{"login":' .. json_value(author_login) .. "}"
        .. ',"createdAt":' .. json_value(comment.createdAt or comment.created_at)
        .. "}")
    end
  end
  return "[" .. table.concat(parts, ",") .. "]"
end

local function rest_issue_to_view_stdout(issue_stdout, comments_stdout)
  local issue = parse_json_object(issue_stdout, "issue")
  local comments = {}
  append_comments(comments, parse_json_object(comments_stdout ~= "" and comments_stdout or "[]", "issue comments"))
  local author_login = nil
  if type(issue.user) == "table" then
    author_login = issue.user.login
  end
  return '{"number":' .. json_value(issue.number)
    .. ',"title":' .. json_value(issue.title)
    .. ',"body":' .. json_value(issue.body)
    .. ',"url":' .. json_value(issue.html_url or issue.url)
    .. ',"updatedAt":' .. json_value(issue.updated_at or issue.updatedAt)
    .. ',"state":' .. json_value(rest_state(issue.state))
    .. ',"labels":' .. labels_json(issue.labels)
    .. ',"comments":' .. comments_json(comments)
    .. ',"assignees":' .. assignees_json(issue.assignees)
    .. ',"author":{"login":' .. json_value(author_login) .. "}"
    .. "}"
end

local function decode_cached_view(encoded)
  local ok, decoded = pcall(json.decode, encoded or "")
  if not ok or type(decoded) ~= "table" or type(decoded.stdout) ~= "string" then
    return nil
  end
  if decoded.updated_at == nil or tostring(decoded.updated_at) == "" then
    return nil
  end
  decoded.updated_at = tostring(decoded.updated_at)
  return decoded
end

local function encode_cached_view(stdout, updated_at, producer)
  return '{"updated_at":' .. json_string(updated_at)
    .. ',"producer":' .. json_string(producer)
    .. ',"stdout":' .. json_string(stdout or "")
    .. "}"
end

local function cache_available()
  return type(cache_get) == "function" and type(cache_set) == "function"
end

local function cache_successful_issue_view(key, stdout, producer)
  if not cache_available() then
    return
  end
  local updated_at = parse_view_updated_at(stdout)
  if updated_at ~= nil then
    cache_set(key, encode_cached_view(stdout or "", updated_at, producer))
  end
end

function M.normalize_issue(gh_json_decoded_or_stdout, source_ref)
  local _repo, source_number = repo_and_number(source_ref)
  local decoded = gh_json_decoded_or_stdout
  if type(decoded) == "string" then
    decoded = json.decode(decoded or "{}")
  end
  assert(type(decoded) == "table", "normalize_issue requires a decoded issue object")
  return {
    number = tonumber(decoded.number) or source_number,
    source_ref = { kind = source_ref.kind, ref = source_ref.ref },
    title = tostring(decoded.title or ""),
    body = decoded.body ~= nil and tostring(decoded.body) or nil,
    url = decoded.url or decoded.html_url,
    updated_at = decoded.updatedAt or decoded.updated_at,
    state = decoded.state,
    labels = label_names(decoded.labels),
    comments = comments_from_json(decoded.comments),
    assignees = assignee_logins(decoded.assignees),
    author_login = issue_author_login(decoded),
  }
end

function M.issue_view_cache_key(repo, issue_number)
  return issue_view_cache_key(repo, issue_number)
end

function M.install(handle)
  function handle.issue_view(repo, issue_number, fields, timeout)
    return handle._exec(
      gh_issue_view_argv(repo, issue_number, fields),
      timeout,
      "gh issue view",
      stdout_policy.content_json("issue_view")
    )
  end

  function handle.issue_view_cmd(repo, issue_number, fields)
    return render_issue_view_argv(gh_issue_view_argv(repo, issue_number, fields))
  end

  local function fetch_issue_view_stdout(repo, number, timeout, opts)
    local issue = handle._exec(
      gh_issue_rest_argv(repo, number),
      timeout,
      "gh issue view",
      stdout_policy.content_json("issue_view")
    )
    local comments = handle._exec(
      gh_issue_comments_rest_argv(repo, number),
      timeout,
      "gh issue comments",
      stdout_policy.content_json("issue_comments")
    )
    local stdout = rest_issue_to_view_stdout(issue.stdout, comments.stdout)
    cache_successful_issue_view(issue_view_cache_key(repo, number), stdout, opts and opts.consumer or "")
    return stdout
  end

  function handle.read_issue(source_ref, opts)
    local options = opts or {}
    local repo, number = repo_and_number(source_ref)
    local timeout = tonumber(options.timeout) or 30
    local key = issue_view_cache_key(repo, number)
    if options.force_fresh == true then
      return M.normalize_issue(fetch_issue_view_stdout(repo, number, timeout, options), source_ref)
    end

    local cached = cache_available() and decode_cached_view(cache_get(key)) or nil
    local validator = tostring(options.updated_at or options.updatedAt or options.validator or "")
    if validator ~= "" then
      -- GitHub updatedAt is second-granular, so this validator is freshness-best-effort only.
      -- It is safe for stale-tolerant observe reads; authority and write-gate reads must force_fresh.
      if cached ~= nil and cached.updated_at == validator then
        return M.normalize_issue(cached.stdout, source_ref)
      end
      return M.normalize_issue(fetch_issue_view_stdout(repo, number, timeout, options), source_ref)
    end

    if cached ~= nil then
      local current = handle._exec(
        gh_issue_updated_at_argv(repo, number),
        timeout,
        "gh issue updated_at",
        stdout_policy.plain_text()
      )
      if parse_updated_at_stdout(current.stdout) == cached.updated_at then
        return M.normalize_issue(cached.stdout, source_ref)
      end
      if cache_available() then
        cache_set(key, "")
      end
    end

    local out = handle._exec(
      gh_issue_view_full_argv(repo, number),
      timeout,
      "gh issue view",
      stdout_policy.content_json("issue_view")
    )
    cache_successful_issue_view(key, out.stdout, options.consumer or "")
    return M.normalize_issue(out.stdout, source_ref)
  end

  function handle.issue_rest_view(repo, issue_number, timeout)
    return handle._exec(
      gh_issue_rest_argv(repo, issue_number),
      timeout,
      "gh issue REST view",
      stdout_policy.content_json("issue_view")
    )
  end

  function handle.issue_view(repo, issue_number, fields, timeout)
    return handle._exec(
      gh_issue_view_argv(repo, issue_number, fields),
      timeout,
      "gh issue view",
      stdout_policy.content_json("issue_view")
    )
  end

  function handle.issue_updated_at(repo, issue_number, timeout)
    return handle._exec(
      gh_issue_updated_at_argv(repo, issue_number),
      timeout,
      "gh issue updated_at",
      stdout_policy.plain_text()
    )
  end

  function handle.issue_add_sub_issue(repo, parent_issue_number, sub_issue_number, timeout)
    local child = handle._exec(
      gh_issue_rest_argv(repo, sub_issue_number),
      timeout,
      "gh issue REST view",
      stdout_policy.content_json("issue_view")
    )
    local child_id = issue_database_id(child.stdout, "sub-issue")
    local ok, result = pcall(function()
      return handle._exec(
        gh_issue_add_sub_issue_argv(repo, parent_issue_number, child_id),
        timeout,
        "gh issue add sub-issue",
        stdout_policy.write_response()
      )
    end)
    if ok then
      return result
    end
    if maybe_duplicate_sub_issue_error(result) then
      local parent_ok, parent = pcall(function()
        return handle._exec(
          gh_issue_sub_issues_argv(repo, parent_issue_number),
          timeout,
          "gh issue list sub-issues",
          stdout_policy.trusted_metadata_json()
        )
      end)
      if parent_ok and parent_has_sub_issue_id(parent.stdout, child_id) then
        return { stdout = "", stderr = result.result and result.result.stderr or "", exit_code = 0, idempotent = true }
      end
    end
    error(result)
  end

  function handle.entity_updated_at(repo, kind, number, timeout)
    if kind == "pr" then
      return handle.pr_updated_at(repo, number, timeout)
    end
    return handle.issue_updated_at(repo, number, timeout)
  end

  function handle.issue_assign(repo, issue_number, login, timeout)
    return handle._exec(
      gh_issue_edit_assignee_argv(repo, issue_number, "--add-assignee", login),
      timeout,
      "gh issue assign",
      stdout_policy.write_response()
    )
  end

  function handle.issue_unassign(repo, issue_number, login, timeout)
    return handle._exec(
      gh_issue_edit_assignee_argv(repo, issue_number, "--remove-assignee", login),
      timeout,
      "gh issue unassign",
      stdout_policy.write_response()
    )
  end

  function handle.issue_add_label(repo, issue_number, label, timeout)
    return handle._exec(
      gh_issue_edit_label_argv(repo, issue_number, "--add-label", label),
      timeout,
      "gh issue add label",
      stdout_policy.write_response()
    )
  end

  function handle.issue_remove_label(repo, issue_number, label, timeout)
    return handle._exec(
      gh_issue_edit_label_argv(repo, issue_number, "--remove-label", label),
      timeout,
      "gh issue remove label",
      stdout_policy.write_response()
    )
  end

end

return M
