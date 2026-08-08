local M = {}
local issue = require("forge.github.issue")
local argv_render = require("forge.argv")
local forge_strings = require("forge.strings")
local github_view = require("forge.github_view")
local content_filter = require("forge.github.content_filter")

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
    issues = seed and seed.issues or {},
    writes = seed and seed.writes or {},
    author_policy = seed and seed.author_policy or nil,
  }
end

function M.new(model)
  assert(type(model) == "table", "forge.github_fake.new requires a model")
  local handle = { _model = model }
  function handle.is_authorized_author(login)
    return content_filter.is_authorized(login, content_filter.policy_whitelist(model.author_policy))
  end
  function handle._exec(argv, timeout, context)
    table.insert(model.writes, {
      kind = "exec",
      argv = copy(argv),
      timeout = timeout,
      context = context,
    })
    return { stdout = "", stderr = "", exit_code = 0 }
  end
  function handle.read_issue(source_ref)
    local fixture = model.issues[source_ref.ref]
    if fixture == nil then
      error("fake: unknown issue " .. tostring(source_ref.ref))
    end
    return copy(issue.normalize_issue(fixture, source_ref))
  end
  require("forge.github.entities").install(handle)
  require("forge.github.comments").install(handle)
  require("forge.github.workflows").install(handle)
  function handle.issue_list_open_assigned(repo, assignee, timeout)
    table.insert(model.writes, {
      kind = "issue_list_open_assigned",
      repo = tostring(repo),
      assignee = tostring(assignee),
      timeout = timeout,
    })
    local normalized_assignee = forge_strings.strip_bot_login_suffix(assignee)
    local rows = {}
    for ref, fixture in pairs(model.issues or {}) do
      local issue_repo = fixture.repo or tostring(ref):match("^([^#]+)#issue/%d+$")
      local state = tostring(fixture.state or "OPEN"):upper()
      if issue_repo == tostring(repo) and state == "OPEN" then
        for _, login in ipairs(fixture.assignees or {}) do
          local candidate = type(login) == "table" and login.login or login
          if forge_strings.strip_bot_login_suffix(candidate) == normalized_assignee then
            table.insert(rows, fixture)
            break
          end
        end
      end
    end
    table.sort(rows, function(left, right)
      return tonumber(left.number or 0) < tonumber(right.number or 0)
    end)
    local rendered = {}
    for _, item in ipairs(rows) do
      table.insert(rendered, "{"
        .. '"number":' .. github_view.json_value(item.number)
        .. ',"title":' .. github_view.json_value(item.title or "")
        .. ',"assignees":' .. github_view.assignees_json(item.assignees)
        .. "}")
    end
    return {
      stdout = "[" .. table.concat(rendered, ",") .. "]",
      stderr = "",
      exit_code = 0,
    }
  end
  function handle.issue_view(repo, issue_number, fields, timeout)
    return handle._exec({
      "gh",
      "issue",
      "view",
      tostring(issue_number),
      "--repo",
      tostring(repo),
      "--json",
      tostring(fields),
    }, timeout, "gh issue view")
  end
  function handle.issue_rest_view(repo, issue_number, timeout)
    return handle._exec({ "gh", "api", "repos/" .. tostring(repo) .. "/issues/" .. tostring(issue_number) }, timeout, "gh issue REST view")
  end
  function handle.issue_view(repo, issue_number, fields, timeout)
    return handle._exec({
      "gh",
      "issue",
      "view",
      tostring(issue_number),
      "--repo",
      tostring(repo),
      "--json",
      tostring(fields),
    }, timeout, "gh issue view")
  end
  function handle.issue_view_cmd(repo, issue_number, fields)
    return table.concat({
      "gh",
      "issue",
      "view",
      argv_render.shell_single_quote(issue_number),
      "--repo",
      argv_render.shell_single_quote(repo),
      "--json",
      tostring(fields),
    }, " ")
  end
  function handle.issue_updated_at(repo, issue_number, timeout)
    return handle._exec({
      "gh",
      "api",
      "repos/" .. tostring(repo) .. "/issues/" .. tostring(issue_number),
      "--jq",
      ".updated_at // .updatedAt // \"\"",
    }, timeout, "gh issue updated_at")
  end
  function handle.entity_updated_at(repo, kind, number, timeout)
    if kind == "pr" then
      return handle.pr_updated_at(repo, number, timeout)
    end
    return handle.issue_updated_at(repo, number, timeout)
  end
  function handle.issue_assign(repo, issue_number, login, timeout)
    return handle._exec({
      "gh",
      "issue",
      "edit",
      tostring(issue_number),
      "--repo",
      tostring(repo),
      "--add-assignee",
      tostring(login),
    }, timeout, "gh issue assign")
  end
  function handle.issue_unassign(repo, issue_number, login, timeout)
    return handle._exec({
      "gh",
      "issue",
      "edit",
      tostring(issue_number),
      "--repo",
      tostring(repo),
      "--remove-assignee",
      tostring(login),
    }, timeout, "gh issue unassign")
  end
  function handle.issue_add_label(repo, issue_number, label, timeout)
    return handle._exec({
      "gh",
      "issue",
      "edit",
      tostring(issue_number),
      "--repo",
      tostring(repo),
      "--add-label",
      tostring(label),
    }, timeout, "gh issue add label")
  end
  function handle.issue_remove_label(repo, issue_number, label, timeout)
    return handle._exec({
      "gh",
      "issue",
      "edit",
      tostring(issue_number),
      "--repo",
      tostring(repo),
      "--remove-label",
      tostring(label),
    }, timeout, "gh issue remove label")
  end
  function handle.issue_add_sub_issue(repo, parent_issue_number, sub_issue_number, timeout)
    return handle._exec({
      "gh",
      "api",
      "--method",
      "POST",
      "repos/" .. tostring(repo) .. "/issues/" .. tostring(parent_issue_number) .. "/sub_issues",
      "-F",
      "sub_issue_id=" .. tostring(sub_issue_number),
    }, timeout, "gh issue add sub-issue")
  end
  require("forge.github.graphql").install(handle)
  return handle
end

return M
