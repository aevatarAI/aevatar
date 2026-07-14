local t = fkst.test
local core = require("core")

local raw_mock_command = t.mock_command
local raw_command_calls = t.command_calls

local function normalize_rendered_command(command)
  local rendered = tostring(command or "")
  rendered = rendered:gsub("'([^']*)'", "%1")
  rendered = rendered:gsub("body=@", "body=")
  rendered = rendered:gsub("%s+", " ")
  rendered = rendered:gsub("%s+$", "")
  return rendered
end

function t.mock_command(command, response)
  local normalized = normalize_rendered_command(command)
  if normalized:find("^gh api %-%-method POST .- %-%-field body=") ~= nil then
    raw_mock_command((normalized:gsub(" %-%-field body=.*$", " --field 'body=")), response)
    return
  end
  if normalized:find("^gh api %-%-method PATCH .- %-%-field body=") ~= nil then
    raw_mock_command((normalized:gsub(" %-%-field body=.*$", " --field 'body=")), response)
    return
  end
  if normalized:find("^gh api graphql %-f query=") ~= nil then
    raw_mock_command("gh api graphql -f 'query=", response)
    return
  end
  if normalized:find("^gh api %-%-paginate %-%-slurp ") ~= nil and normalized:find("[?&]", 1, false) ~= nil then
    raw_mock_command((normalized:gsub("^(gh api %-%-paginate %-%-slurp )", "%1'")), response)
    return
  end
  if normalized ~= command then
    raw_mock_command(normalized, response)
    return
  end
  raw_mock_command(command, response)
end

local function nonce()
  return tostring({}):gsub("[^%w._-]", "_")
end

local function issue_list_json(updated_at, state)
  return string.format(
    '[[{"number":42,"title":"Bridge issue","html_url":"https://github.example/owner/x/issues/42","updated_at":"%s","state":"%s","labels":[{"name":"adapter-enabled"},{"name":"bug"}]}]]\n',
    updated_at or "2026-06-03T01:02:03Z",
    state or "open"
  )
end

local function pr_list_json(updated_at, state)
  return string.format(
    '[[{"number":7,"title":"Bridge PR","html_url":"https://github.example/owner/x/pull/7","updated_at":"%s","state":"%s","labels":[{"name":"review"}]}]]\n',
    updated_at or "2026-06-03T02:03:04Z",
    state or "open"
  )
end

local function runtime_root(name)
  return "/tmp/fkst-packages-test/github-proxy/" .. tostring(now()) .. "/" .. nonce() .. "/" .. name
end

local function base_env(name, extra)
  local env = {
    FKST_GITHUB_REPO = "owner/x",
    FKST_RUNTIME_ROOT = runtime_root(name),
  }
  for key, value in pairs(extra or {}) do
    env[key] = value
  end
  return env
end

local function opts(name, extra_env)
  return {
    env = base_env(name, extra_env),
  }
end

local function mock_repo_env(value)
  t.mock_command('printf %s "$FKST_GITHUB_REPO"', { stdout = value or "owner/x" })
end

local function mock_proxy_replay_budget_env(value)
  t.mock_command('printf %s "$FKST_GITHUB_PROXY_REPLAY_BUDGET"', { stdout = value or "" })
end

local function mock_poll_label_prefix_env(value)
  t.mock_command('printf %s "$FKST_GITHUB_PROXY_POLL_LABEL_PREFIX"', { stdout = value or "adapter-" })
end

local function mock_write_env(value)
  t.mock_command('printf %s "$FKST_GITHUB_WRITE"', { stdout = value or "" })
end

local function mock_bot_env(value)
  t.mock_command('printf %s "$FKST_GITHUB_BOT_LOGIN"', { stdout = value or "fkst-test-bot" })
end

local function mock_issue_list(stdout, exit_code, stderr)
  t.mock_command("gh api --paginate --slurp repos/owner/x/issues?state=open&per_page=100", {
    stdout = stdout or issue_list_json(),
    stderr = stderr or "",
    exit_code = exit_code or 0,
  })
end

local function mock_pr_list(stdout, exit_code, stderr)
  t.mock_command("gh api --paginate --slurp repos/owner/x/pulls?state=open&per_page=100", {
    stdout = stdout or pr_list_json(),
    stderr = stderr or "",
    exit_code = exit_code or 0,
  })
end

local function mock_poll(issue_stdout, pr_stdout)
  mock_repo_env()
  mock_poll_label_prefix_env()
  mock_issue_list(issue_stdout)
  mock_pr_list(pr_stdout)
end

local function encode_json_string(value)
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
  return text
end

local function comment_json(body, author, id, database_id)
  local id_field = ""
  if id ~= nil then
    id_field = '"id":"' .. encode_json_string(id) .. '",'
  end
  local database_id_field = ""
  if database_id ~= nil then
    database_id_field = '"databaseId":' .. tostring(database_id) .. ","
  end
  return string.format('{%s%s"body":"%s","author":{"login":"%s"}}', id_field, database_id_field, encode_json_string(body), encode_json_string(author or "fkst-test-bot"))
end

local function rest_comment_json(body, author, id)
  local comment_id = id
  if comment_id == nil or tostring(comment_id):find("^%d+$") == nil then
    comment_id = 123456
  end
  return string.format(
    '{"id":%s,"body":"%s","user":{"login":"%s"}}',
    tostring(comment_id),
    encode_json_string(body),
    encode_json_string(author or "fkst-test-bot")
  )
end

local function render_rest_comments(comments, author)
  if type(comments) == "table" then
    local parts = {}
    for index, comment in ipairs(comments) do
      if type(comment) == "table" then
        table.insert(parts, rest_comment_json(comment.body, comment.author_login or comment.author, comment.databaseId or comment.database_id or comment.id or index))
      else
        table.insert(parts, rest_comment_json(comment, "fkst-test-bot", index))
      end
    end
    return table.concat(parts, ",")
  end
  return rest_comment_json(comments or "existing comment", author)
end

local function mock_comment_view(comments, author)
  local rendered = render_rest_comments(comments, author)
  if type(comments) ~= "table" then
    rendered = rendered
      .. ","
      .. rest_comment_json('<!-- fkst:generic-workflow:state:v1 proposal="generic-workflow/issue/owner/x/42" state="implementing" version="v1" stage_rank="600" -->')
      .. ","
      .. rest_comment_json('<!-- fkst:generic-workflow:implementing:v1 proposal="generic-workflow/issue/owner/x/42" dedup="v1" branch="generic-owner-x-42-01HY" head_sha="abc123" base_branch="dev" base_sha="abc123" -->')
  end
  t.mock_command("gh api --paginate --slurp repos/owner/x/issues/42/comments?per_page=100", {
    stdout = "[[" .. rendered .. "]]\n",
  })
  t.mock_command("gh api --paginate --slurp repos/owner/payload/issues/42/comments?per_page=100", {
    stdout = "[[" .. rendered .. "]]\n",
  })
end

local function mock_comment_view_failure()
  t.mock_command("gh api --paginate --slurp repos/owner/x/issues/42/comments?per_page=100", {
    stdout = "",
    stderr = "forced comment view failure",
    exit_code = 1,
  })
end

local function mock_label_view(labels)
  local parts = {}
  for _, label in ipairs(labels or {}) do
    table.insert(parts, string.format('{"name":"%s"}', label))
  end
  t.mock_command("gh api repos/owner/x/issues/42", {
    stdout = '{"labels":[' .. table.concat(parts, ",") .. "]}\n",
  })
end

local function mock_comment_write()
  t.mock_command("gh api --method POST repos/owner/x/issues/42/comments --field body=/tmp/fkst-github-proxy-comment-owner_x-issue-42.md", {
    stdout = '{"id":123456,"body":"created","user":{"login":"fkst-test-bot"}}\n',
    exit_code = 0,
  })
  t.mock_command("gh api --method POST repos/owner/payload/issues/42/comments --field body=/tmp/fkst-github-proxy-comment-owner_payload-issue-42.md", {
    stdout = '{"id":123456,"body":"created","user":{"login":"fkst-test-bot"}}\n',
    exit_code = 0,
  })
end

local function label_list_json(labels)
  local parts = {}
  for _, label in ipairs(labels or {}) do
    table.insert(parts, string.format('{"name":"%s"}', encode_json_string(label)))
  end
  return "[" .. table.concat(parts, ",") .. "]\n"
end

local default_repo_labels = {
  "adapter-enabled",
  "adapter-thinking",
  "adapter-ready",
  "adapter-implementing",
  "adapter-pr-open",
  "adapter-reviewing",
  "adapter-merge-ready",
  "adapter-fixing",
  "adapter-blocked",
  "adapter-blocked-on-dependency",
  "adapter-impl-failed",
}

local function mock_repo_label_list(labels)
  t.mock_command("gh label list", {
    stdout = label_list_json(labels or default_repo_labels),
    stderr = "",
    exit_code = 0,
  })
end

local function mock_label_create(exit_code, stderr)
  t.mock_command("gh label create", {
    stdout = "",
    stderr = stderr or "",
    exit_code = exit_code or 0,
  })
end

local function mock_label_write(labels)
  mock_repo_label_list(labels)
  t.mock_command("gh issue edit", { stdout = "", exit_code = 0 })
end

local function mock_pr_comment_view(comments, author)
  local stdout = "[[" .. render_rest_comments(comments or "existing pr comment", author) .. "]]\n"
  for _, number in ipairs({ 7, 9, 10, 11 }) do
    t.mock_command("gh api --paginate --slurp repos/owner/x/issues/" .. tostring(number) .. "/comments?per_page=100", {
      stdout = stdout,
    })
  end
end

local function mock_pr_comment_write()
  t.mock_command("gh api --method POST repos/owner/x/issues/7/comments --field body=/tmp/fkst-github-proxy-comment-owner_x-pr-7.md", {
    stdout = '{"id":123456,"body":"created","user":{"login":"fkst-test-bot"}}\n',
    exit_code = 0,
  })
  t.mock_command("gh pr comment 7 --repo owner/x --body-file /tmp/fkst-github-proxy-comment-owner_x-pr-7.md", {
    stdout = "",
    exit_code = 0,
  })
  t.mock_command("gh pr comment 7 --repo owner/x --body-file /tmp/fkst-github-proxy-intent-issue-create-decompose_generic-workflow_issue_owner_x_42_v1_1_123.md", {
    stdout = "",
    exit_code = 0,
  })
  t.mock_command("gh pr comment 7 --repo owner/x --body-file /tmp/fkst-github-proxy-created-issue-create-decompose_generic-workflow_issue_owner_x_42_v1_1_123.md", {
    stdout = "",
    exit_code = 0,
  })
  t.mock_command("gh pr comment 7 --repo owner/x --body-file /tmp/fkst-github-proxy-pr-open-owner_x-generic-owner-x-42-01HY-pr-comment.md", {
    stdout = "",
    exit_code = 0,
  })
  t.mock_command("gh pr comment 9 --repo owner/x --body-file /tmp/fkst-github-proxy-pr-open-owner_x-generic-owner-x-42-01HY-pr-comment.md", {
    stdout = "",
    exit_code = 0,
  })
  t.mock_command("gh pr comment 10 --repo owner/x --body-file /tmp/fkst-github-proxy-pr-open-owner_x-generic-owner-x-42-01HY-pr-comment.md", {
    stdout = "",
    exit_code = 0,
  })
  t.mock_command("gh pr comment 11 --repo owner/x --body-file /tmp/fkst-github-proxy-pr-open-owner_x-generic-owner-x-42-01HY-pr-comment.md", {
    stdout = "",
    exit_code = 0,
  })
end

local function calls_matching(needle)
  local normalized_needle = normalize_rendered_command(needle)
  local matches = {}
  for _, call in ipairs(raw_command_calls()) do
    if normalize_rendered_command(call.rendered):find(normalized_needle, 1, true) ~= nil then
      table.insert(matches, call)
    end
  end
  return matches
end

local function count_calls(needle)
  return #calls_matching(needle)
end

local function module_name_for_department(department_path)
  local department = tostring(department_path or ""):match("^departments/([^/]+)/main%.lua$")
  if department == nil then
    error("github-proxy: unsupported department path " .. tostring(department_path))
  end
  return "departments." .. department .. ".main"
end

local function capture_comment_department_logs(department_path, event, write_env)
  mock_write_env(write_env)

  local captured = {}
  local old_log = log
  local old_write_comment_request = core.write_comment_request
  local write_requests = 0

  log = {
    info = function(message)
      table.insert(captured, tostring(message))
    end,
    warn = function(message)
      table.insert(captured, tostring(message))
    end,
    error = function(message)
      table.insert(captured, tostring(message))
    end,
  }
  core.write_comment_request = function(_payload, _target)
    write_requests = write_requests + 1
    core.read_env("FKST_GITHUB_WRITE")
  end

  local ok, err = pcall(function()
    local department = require(module_name_for_department(department_path))
    department.pipeline(event)
  end)

  core.write_comment_request = old_write_comment_request
  log = old_log
  if not ok then
    error(err)
  end

  return captured, write_requests
end

local function capture_label_department_logs(department_path, event, write_env, apply_result)
  mock_write_env(write_env)

  local captured = {}
  local old_log = log
  local old_apply_issue_labels = core.apply_issue_labels
  local old_with_lock = with_lock
  local write_requests = 0

  log = {
    info = function(message)
      table.insert(captured, tostring(message))
    end,
    warn = function(message)
      table.insert(captured, tostring(message))
    end,
    error = function(message)
      table.insert(captured, tostring(message))
    end,
  }
  core.apply_issue_labels = function(_repo, _issue_number, _add_labels, _remove_labels)
    write_requests = write_requests + 1
    if apply_result == false then
      return false
    end
    return true
  end
  with_lock = function(_key, fn)
    return fn()
  end

  local ok, err = pcall(function()
    local department = require(module_name_for_department(department_path))
    department.pipeline(event)
  end)

  core.apply_issue_labels = old_apply_issue_labels
  with_lock = old_with_lock
  log = old_log
  if not ok then
    error(err)
  end

  return captured, write_requests
end

local function long_dedup(suffix, total_len)
  local prefix = "generic-workflow/issue/owner/x/42/result/"
  return prefix .. string.rep("v", total_len - #prefix - #suffix) .. suffix
end

local function reviewing_marker()
  return '<!-- fkst:generic-workflow:state:v1 proposal="generic-workflow/issue/owner/x/42" state="reviewing" version="v1" stage_rank="675" -->'
end


return {
  t = t,
  core = core,
  issue_list_json = issue_list_json,
  pr_list_json = pr_list_json,
  runtime_root = runtime_root,
  opts = opts,
  mock_repo_env = mock_repo_env,
  mock_proxy_replay_budget_env = mock_proxy_replay_budget_env,
  mock_poll_label_prefix_env = mock_poll_label_prefix_env,
  mock_write_env = mock_write_env,
  mock_bot_env = mock_bot_env,
  mock_issue_list = mock_issue_list,
  mock_pr_list = mock_pr_list,
  mock_poll = mock_poll,
  json_string = encode_json_string,
  encode_json_string = encode_json_string,
  comment_json = comment_json,
  mock_comment_view = mock_comment_view,
  mock_comment_view_failure = mock_comment_view_failure,
  mock_label_view = mock_label_view,
  mock_comment_write = mock_comment_write,
  mock_repo_label_list = mock_repo_label_list,
  mock_label_create = mock_label_create,
  mock_label_write = mock_label_write,
  mock_pr_comment_view = mock_pr_comment_view,
  mock_pr_comment_write = mock_pr_comment_write,
  calls_matching = calls_matching,
  count_calls = count_calls,
  capture_comment_department_logs = capture_comment_department_logs,
  capture_label_department_logs = capture_label_department_logs,
  long_dedup = long_dedup,
  reviewing_marker = reviewing_marker,
}
