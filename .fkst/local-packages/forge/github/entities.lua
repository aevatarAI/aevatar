local M = {}
local argv_render = require("forge.argv")
local gitref = require("forge.gitref")
local gh_result = require("forge.github.result").gh_result
local shell = require("forge.github.shell")

local function repo_owner(repo)
  return tostring(repo or ""):match("^([^/]+)/")
end

local function issue_list_argv(repo)
  return { "gh", "api", "--paginate", "--slurp", "repos/" .. tostring(repo) .. "/issues?state=open&per_page=100" }
end

local function issue_list_cli_argv(repo, state, limit, fields)
  return {
    "gh",
    "issue",
    "list",
    "--repo",
    tostring(repo),
    "--state",
    tostring(state),
    "--limit",
    tostring(limit),
    "--json",
    tostring(fields),
  }
end

local function pr_list_cli_argv(repo, state, limit, fields)
  return {
    "gh",
    "pr",
    "list",
    "--repo",
    tostring(repo),
    "--state",
    tostring(state),
    "--limit",
    tostring(limit),
    "--json",
    tostring(fields),
  }
end

local function pr_list_recent_merged_argv(repo, limit)
  return pr_list_cli_argv(repo, "merged", limit, "number,title,mergedAt,headRefOid")
end

local function pr_list_argv(repo)
  return { "gh", "api", "--paginate", "--slurp", "repos/" .. tostring(repo) .. "/pulls?state=open&per_page=100" }
end

local function issue_list_observe_argv(repo, label, page, include_headers)
  local argv = { "gh", "api" }
  if include_headers then
    table.insert(argv, "--include")
  end
  local query = "repos/" .. tostring(repo) .. "/issues?state=open&per_page=100"
  if label ~= nil and tostring(label) ~= "" then
    query = "repos/" .. tostring(repo) .. "/issues?state=open&labels="
      .. tostring(label):gsub(":", "%%3A") .. "&per_page=100"
  end
  if page ~= nil then
    query = query .. "&page=" .. tostring(page)
  else
    table.insert(argv, "--paginate")
    table.insert(argv, "--slurp")
  end
  table.insert(argv, query)
  return argv
end

local function pr_list_observe_argv(repo, page, include_headers)
  local argv = { "gh", "api" }
  if include_headers then
    table.insert(argv, "--include")
  end
  if page == nil then
    table.insert(argv, "--paginate")
    table.insert(argv, "--slurp")
  end
  table.insert(argv, "repos/" .. tostring(repo) .. "/pulls?state=open&per_page=100"
    .. (page ~= nil and ("&page=" .. tostring(page)) or ""))
  return argv
end

local function pr_list_merge_queue_argv(repo, base)
  return {
    "gh",
    "api",
    "--paginate",
    "--slurp",
    "repos/" .. tostring(repo) .. "/pulls?state=open&base=" .. shell.url_encode(base) .. "&per_page=100",
  }
end

local function pr_list_head_argv(repo, branch, base_branch)
  local owner = repo_owner(repo)
  local head_filter = owner ~= nil and (owner .. ":" .. tostring(branch)) or tostring(branch)
  local query = "repos/" .. tostring(repo) .. "/pulls?state=open&head=" .. shell.url_encode(head_filter) .. "&per_page=100"
  if base_branch ~= nil then
    query = query .. "&base=" .. shell.url_encode(base_branch)
  end
  return { "gh", "api", "--paginate", "--slurp", query }
end

local function pr_view_argv(repo, pr_number)
  return { "gh", "api", "repos/" .. tostring(repo) .. "/pulls/" .. tostring(pr_number) }
end

local function pr_view_cli_argv(repo, pr_number, fields)
  return { "gh", "pr", "view", tostring(pr_number), "--repo", tostring(repo), "--json", tostring(fields) }
end

local merge_pr_fields = "headRefName,headRefOid,baseRefName,baseRefOid,state,updatedAt,isDraft,mergedAt,comments,headRepository,headRepositoryOwner,isCrossRepository,mergeable,mergeStateStatus,statusCheckRollup"

local function pr_diff_argv(repo, pr_number)
  return { "gh", "pr", "diff", tostring(pr_number), "--repo", tostring(repo) }
end

local function pr_diff_name_only_argv(repo, pr_number)
  return { "gh", "pr", "diff", tostring(pr_number), "--repo", tostring(repo), "--name-only" }
end

local function pr_ready_argv(repo, pr_number)
  return { "gh", "pr", "ready", tostring(pr_number), "--repo", tostring(repo) }
end

local function pr_close_argv(repo, pr_number)
  return { "gh", "pr", "close", tostring(pr_number), "--repo", tostring(repo) }
end

local function issue_close_argv(repo, issue_number)
  return { "gh", "issue", "close", tostring(issue_number), "--repo", tostring(repo) }
end

local function pr_merge_argv(repo, pr_number, head_sha)
  return {
    "gh",
    "pr",
    "merge",
    tostring(pr_number),
    "--repo",
    tostring(repo),
    "--merge",
    "--match-head-commit",
    tostring(head_sha),
  }
end

local function render_gh_argv(argv, quote_positions)
  local quoted = {}
  for _, position in ipairs(quote_positions or {}) do
    quoted[position] = true
  end
  local parts = {}
  for index, value in ipairs(argv or {}) do
    if quoted[index] then
      table.insert(parts, argv_render.shell_single_quote(value))
    else
      table.insert(parts, tostring(value))
    end
  end
  return table.concat(parts, " ")
end

local function entity_updated_at_argv(repo, kind, number)
  local path_kind = kind == "pr" and "pulls" or "issues"
  return {
    "gh",
    "api",
    "repos/" .. tostring(repo) .. "/" .. path_kind .. "/" .. tostring(number),
    "--jq",
    ".updated_at // .updatedAt // \"\"",
  }
end

local function issue_search_argv(repo, query, fields)
  return {
    "gh",
    "issue",
    "list",
    "--repo",
    tostring(repo),
    "--state",
    "all",
    "--limit",
    "100",
    "--search",
    tostring(query),
    "--json",
    tostring(fields),
  }
end

local function api_paginate_slurp_argv(path)
  return { "gh", "api", "--paginate", "--slurp", tostring(path) }
end

local function api_method_argv(method, path, fields, input_file, include_headers)
  local argv = { "gh", "api", "--method", tostring(method) }
  if include_headers then
    table.insert(argv, "--include")
  end
  table.insert(argv, tostring(path))
  for _, field in ipairs(fields or {}) do
    table.insert(argv, "-f")
    table.insert(argv, tostring(field))
  end
  if input_file ~= nil then
    table.insert(argv, "--input")
    table.insert(argv, tostring(input_file))
  end
  return argv
end

local function issue_create_argv(repo, title, body_file, labels, assignees)
  local argv = {
    "gh",
    "issue",
    "create",
    "--repo",
    tostring(repo),
    "--title",
    tostring(title),
    "--body-file",
    tostring(body_file),
  }
  for _, label in ipairs(labels or {}) do
    table.insert(argv, "--label")
    table.insert(argv, tostring(label))
  end
  for _, assignee in ipairs(assignees or {}) do
    table.insert(argv, "--assignee")
    table.insert(argv, tostring(assignee))
  end
  return argv
end

local function pr_create_argv(repo, branch, base_branch, title, body_file)
  local argv = { "gh", "pr", "create", "--repo", tostring(repo), "--head", tostring(branch) }
  if base_branch ~= nil then
    table.insert(argv, "--base")
    table.insert(argv, tostring(base_branch))
  end
  table.insert(argv, "--title")
  table.insert(argv, tostring(title))
  table.insert(argv, "--body-file")
  table.insert(argv, tostring(body_file))
  return argv
end

local function pr_create_body_argv(repo, branch, base_branch, title, body)
  local argv = { "gh", "pr", "create", "--repo", tostring(repo), "--head", tostring(branch) }
  if base_branch ~= nil then
    table.insert(argv, "--base")
    table.insert(argv, tostring(base_branch))
  end
  table.insert(argv, "--title")
  table.insert(argv, tostring(title))
  table.insert(argv, "--body")
  table.insert(argv, tostring(body))
  return argv
end

local function label_list_argv(repo)
  return { "gh", "label", "list", "--repo", tostring(repo), "--limit", "1000", "--json", "name" }
end

local function label_create_argv(repo, label, color)
  return { "gh", "label", "create", tostring(label), "--repo", tostring(repo), "--color", tostring(color or "ededed") }
end

local function label_rest_create_argv(repo, name, color, description)
  return api_method_argv("POST", "repos/" .. tostring(repo) .. "/labels", {
    "name=" .. tostring(name),
    "color=" .. tostring(color),
    "description=" .. tostring(description or ""),
  })
end

local function label_rest_update_argv(repo, name, color, description)
  return api_method_argv("PATCH", "repos/" .. tostring(repo) .. "/labels/" .. tostring(name):gsub(":", "%%3A"), {
    "color=" .. tostring(color),
    "description=" .. tostring(description or ""),
  })
end

local function edit_labels_argv(command, repo, number, add_labels, remove_labels)
  local argv = { "gh", command, "edit", tostring(number), "--repo", tostring(repo) }
  for _, label in ipairs(add_labels or {}) do
    table.insert(argv, "--add-label")
    table.insert(argv, tostring(label))
  end
  for _, label in ipairs(remove_labels or {}) do
    table.insert(argv, "--remove-label")
    table.insert(argv, tostring(label))
  end
  return argv
end

function M.install(handle)
  function handle.issue_list(repo, timeout)
    return handle._exec(issue_list_argv(repo), timeout, "gh issue list")
  end

  function handle.issue_list_cli(repo, state, limit, fields, timeout)
    return handle._exec(issue_list_cli_argv(repo, state, limit, fields), timeout, "gh issue list")
  end

  function handle.issue_list_intake(repo, limit, timeout)
    return handle.issue_list_cli(repo, "open", limit, "number,title,body,updatedAt,labels,assignees,author", timeout)
  end

  function handle.issue_list_recent_closed(repo, limit, timeout)
    local bounded_limit = tonumber(limit or 30)
    if bounded_limit == nil or bounded_limit < 1 or bounded_limit > 100 then
      error("forge.github.entities: invalid closed issue list limit")
    end
    return handle.issue_list_cli(repo, "closed", math.floor(bounded_limit), "number,title,closedAt,labels", timeout)
  end

  function handle.issue_list_board_digest(repo, timeout)
    return handle.issue_list_cli(repo, "open", 100, "number,title,labels", timeout)
  end

  function handle.pr_list(repo, timeout)
    return handle._exec(pr_list_argv(repo), timeout, "gh pr list")
  end

  function handle.issue_list_observe(repo, label, page, include_headers, timeout)
    return handle._exec(issue_list_observe_argv(repo, label, page, include_headers), timeout, "gh issue observe list")
  end

  function handle.pr_list_observe(repo, page, include_headers, timeout)
    return handle._exec(pr_list_observe_argv(repo, page, include_headers), timeout, "gh PR observe list")
  end

  function handle.pr_list_cli(repo, state, limit, fields, timeout)
    return handle._exec(pr_list_cli_argv(repo, state, limit, fields), timeout, "gh pr list")
  end

  function handle.pr_list_recent_merged(repo, limit, timeout)
    return handle._exec(pr_list_recent_merged_argv(repo, limit), timeout, "gh pr list recent merged")
  end

  function handle.pr_list_board_digest(repo, timeout)
    return handle.pr_list_cli(repo, "open", 100, "number,title,labels", timeout)
  end

  function handle.pr_list_head(repo, branch, base_branch, timeout)
    return handle._exec(pr_list_head_argv(repo, branch, base_branch), timeout, "gh pr list --head")
  end

  function handle.pr_list_merge_queue(repo, base, timeout)
    return handle._exec(pr_list_merge_queue_argv(repo, base), timeout, "gh pr merge queue list")
  end

  function handle.pr_list_merge_queue_cmd(repo, base)
    return render_gh_argv(pr_list_merge_queue_argv(repo, base), { 5 })
  end

  function handle.pr_view(repo, pr_number, timeout)
    return handle._exec(pr_view_argv(repo, pr_number), timeout, "gh PR REST head repository/headRefOid/state")
  end

  function handle.pr_cli_view(repo, pr_number, fields, timeout)
    return handle._exec(pr_view_cli_argv(repo, pr_number, fields), timeout, "gh pr view")
  end

  function handle.gh_pr_view_merge(repo, pr_number, timeout)
    return gh_result(function()
      return handle.pr_cli_view(repo, pr_number, merge_pr_fields, timeout)
    end)
  end

  function handle.pr_cli_view_cmd(repo, pr_number, fields)
    return render_gh_argv(pr_view_cli_argv(repo, pr_number, fields), { 4, 6 })
  end

  function handle.pr_rest_view(repo, pr_number, timeout)
    return handle._exec(pr_view_argv(repo, pr_number), timeout, "gh PR REST view")
  end

  function handle.pr_diff_name_only(repo, pr_number, timeout)
    return handle._exec(pr_diff_name_only_argv(repo, pr_number), timeout, "gh pr diff --name-only")
  end

  function handle.pr_diff(repo, pr_number, timeout)
    return handle._exec(pr_diff_argv(repo, pr_number), timeout, "gh pr diff")
  end

  function handle.pr_ready(repo, pr_number, timeout)
    return handle._exec(pr_ready_argv(repo, pr_number), timeout, "gh pr ready")
  end

  function handle.pr_close(repo, pr_number, timeout)
    return handle._exec(pr_close_argv(repo, pr_number), timeout, "gh pr close")
  end

  function handle.issue_close(repo, issue_number, timeout)
    return handle._exec(issue_close_argv(repo, issue_number), timeout, "gh issue close")
  end

  function handle.pr_merge(repo, pr_number, head_sha, timeout)
    return handle._exec(pr_merge_argv(repo, pr_number, head_sha), timeout, "gh pr merge")
  end

  function handle.gh_pr_merge(repo, pr_number, head_sha, timeout)
    if tostring(head_sha or "") == "" then
      error("github-devloop: invalid merge head sha")
    end
    return gh_result(function()
      return handle.pr_merge(repo, pr_number, head_sha, timeout)
    end)
  end

  function handle.pr_merge_cmd(repo, pr_number, head_sha)
    return render_gh_argv(pr_merge_argv(repo, pr_number, head_sha), { 4, 6, 9 })
  end

  function handle.pr_updated_at(repo, pr_number, timeout)
    return handle._exec(entity_updated_at_argv(repo, "pr", pr_number), timeout, "gh PR updated_at")
  end

  function handle.issue_search(repo, query, fields, timeout)
    return handle._exec(issue_search_argv(repo, query, fields), timeout, "gh issue search")
  end

  function handle.api_get(repo, path, timeout)
    return handle._exec({ "gh", "api", "repos/" .. tostring(repo) .. "/" .. tostring(path) }, timeout, "gh api GET")
  end

  function handle.gh_commit_check_runs(repo, head_sha, timeout)
    return gh_result(function()
      return handle.api_get(repo, "commits/" .. gitref.require_safe_sha("commit check-runs head sha", head_sha, "github-devloop") .. "/check-runs", timeout)
    end)
  end

  function handle.api_paginate_slurp(path, timeout)
    return handle._exec(api_paginate_slurp_argv(path), timeout, "gh api paginated list")
  end

  function handle.api_method(method, path, fields, input_file, include_headers, timeout)
    return handle._exec(api_method_argv(method, path, fields, input_file, include_headers), timeout, "gh api method")
  end

  function handle.gh_check_run_rerequest(repo, check_run_id, timeout)
    local id = tostring(check_run_id or "")
    if id == "" or id:find("[^0-9]") ~= nil then
      error("github-devloop: invalid check-run id")
    end
    return gh_result(function()
      return handle.api_method("POST", "repos/" .. tostring(repo) .. "/check-runs/" .. id .. "/rerequest", nil, nil, nil, timeout)
    end)
  end

  function handle.issue_create(repo, title, body_file, labels, assignees, timeout)
    return handle._exec(issue_create_argv(repo, title, body_file, labels, assignees), timeout, "gh issue create")
  end

  function handle.pr_create(repo, branch, base_branch, title, body_file, timeout)
    return handle._exec(pr_create_argv(repo, branch, base_branch, title, body_file), timeout, "gh pr create")
  end

  function handle.pr_create_body(repo, branch, base_branch, title, body, timeout)
    return handle._exec(pr_create_body_argv(repo, branch, base_branch, title, body), timeout, "gh pr create")
  end

  function handle.label_list(repo, timeout)
    return handle._exec(label_list_argv(repo), timeout, "gh label list")
  end

  function handle.label_create(repo, label, color, timeout)
    return handle._exec(label_create_argv(repo, label, color), timeout, "gh label create")
  end

  function handle.label_rest_create(repo, name, color, description, timeout)
    return handle._exec(label_rest_create_argv(repo, name, color, description), timeout, "gh label REST create")
  end

  function handle.label_rest_update(repo, name, color, description, timeout)
    return handle._exec(label_rest_update_argv(repo, name, color, description), timeout, "gh label REST update")
  end

  function handle.issue_edit_labels(repo, issue_number, add_labels, remove_labels, timeout)
    return handle._exec(edit_labels_argv("issue", repo, issue_number, add_labels, remove_labels), timeout, "gh issue edit")
  end

  function handle.pr_edit_labels(repo, pr_number, add_labels, remove_labels, timeout)
    return handle._exec(edit_labels_argv("pr", repo, pr_number, add_labels, remove_labels), timeout, "gh pr edit")
  end
end

return M
