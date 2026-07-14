local M = {}
local env = require("workflow.env")
local forge_strings = require("forge.strings")

require("core.error_facts").install(M)


-- A GitHub App's author login is "<slug>[bot]" via the REST API but bare
-- "<slug>" via GraphQL. Strip the suffix so callers comparing against a
-- configured bot login match regardless of which API populated the field.
-- No-op for ordinary user logins (which never end in "[bot]").
M.strip_bot_login_suffix = forge_strings.strip_bot_login_suffix

function M.is_positive_integer(value)
  local n = tonumber(value)
  return n ~= nil and n >= 1 and n % 1 == 0 and n <= 2147483647
end

local shared_helpers = {
  strip_bot_login_suffix = M.strip_bot_login_suffix,
  is_positive_integer = M.is_positive_integer,
}

-- Narrowest-surface proof for these shared helpers:
-- surface_proof = "forge-shared-domain-helper"
-- forge_status = "shared-with-ratchet-migration-slicer"
-- collapse_status = "multi-call-site-behavioral-reuse"

require("core.issue_create").install(M, shared_helpers)
require("core.github_graphql").install(M)
require("core.blocked_by").install(M, shared_helpers)
require("core.external_effect_sagas").install(M)
require("core.rest_view").install(M)
require("core.entity_view").install(M)
require("core.gh_rate").install(M)
require("core.comment").install(M, shared_helpers)
require("core.marker_guard").install(M)
require("core.claims").install(M)

local allowed_env = {
  FKST_GITHUB_REPO = true,
  FKST_GITHUB_BOT_LOGIN = true,
  FKST_GITHUB_WRITE = true,
  FKST_GITHUB_PROXY_POLL_LABEL_PREFIX = true,
  FKST_GITHUB_PROXY_REPLAY_BUDGET = true,
  FKST_DEBUG_STAMP = true,
}
local trusted_bot_login = nil

local is_git_ref_safe = forge_strings.is_git_ref_safe

local function is_git_sha(value)
  return type(value) == "string" and value:find("^[0-9A-Fa-f]+$") ~= nil and #value >= 6 and #value <= 64
end

local function read_env_command(name)
  if not allowed_env[name] then
    error("env name is not allowed: " .. tostring(name))
  end
  return 'printf %s "$' .. name .. '"'
end

M.read_env_command = read_env_command
M.read_env = env.read_env(read_env_command, {
  missing_exec_error = "read_env requires exec_sync",
  propagate_exec_errors = true,
})

function M.write_with_outbound_log(payload, target, log_outbound)
  local repo = payload.repo
  local logged = false
  local read_env = M.read_env
  M.read_env = function(name, exec)
    local value = read_env(name, exec)
    if name == "FKST_GITHUB_REPO" and (repo == nil or repo == "") then
      repo = value
    end
    if name == "FKST_GITHUB_WRITE" and not logged then
      log_outbound(payload, repo, value)
      logged = true
    end
    return value
  end

  local written = nil
  local ok, err = pcall(function()
    written = M.write_comment_request(payload, target)
  end)
  M.read_env = read_env
  if not ok then
    error(err)
  end
  return written, repo
end

function M.github_proxy_replay_budget(exec)
  local ok, value = pcall(M.read_env, "FKST_GITHUB_PROXY_REPLAY_BUDGET", exec)
  if not ok then
    return 10
  end
  if value == nil then
    return 10
  end
  value = tostring(value):gsub("^%s+", ""):gsub("%s+$", "")
  local parsed = tonumber(value)
  if parsed == nil or parsed ~= math.floor(parsed) or parsed < 1 or parsed > 100 then
    error("github-proxy: invalid FKST_GITHUB_PROXY_REPLAY_BUDGET")
  end
  return parsed
end

function M.github_proxy_poll_label_prefixes(exec)
  local ok, value = pcall(M.read_env, "FKST_GITHUB_PROXY_POLL_LABEL_PREFIX", exec)
  if not ok or value == nil then
    return {}
  end
  local prefixes = {}
  local seen = {}
  for raw in tostring(value):gmatch("[^,]+") do
    local prefix = raw:gsub("^%s+", ""):gsub("%s+$", "")
    if prefix ~= "" and not seen[prefix] then
      seen[prefix] = true
      table.insert(prefixes, prefix)
    end
  end
  return prefixes
end

require("forge.github_debug_stamp").install(M)

function M.log_line(level, dept, tag, fields)
  local parts = {
    "github-proxy",
    "dept=" .. tostring(dept or "unknown"),
    "tag=" .. tostring(tag or "event"),
  }
  for _, field in ipairs(fields or {}) do
    table.insert(parts, tostring(field))
  end
  log[level or "info"](table.concat(parts, " "))
end

local function command_result_stderr(result) return type(result) == "table" and tostring(result.stderr or "") or "" end

local function command_result_exit_code(result) return type(result) == "table" and tonumber(result.exit_code) or nil end

function M.is_gh_rate_limited(result)
  local stderr = command_result_stderr(result)
  local lower = stderr:lower()
  -- Broad "api rate limit" covers both "API rate limit exceeded" and the
  -- dominant "API rate limit already exceeded for user ID <n>" wording, where
  -- the interposed "already" defeats a contiguous "api rate limit exceeded".
  if lower:find("api rate limit", 1, true) ~= nil then
    return true
  end
  if lower:find("was submitted too quickly", 1, true) ~= nil then
    return true
  end
  if lower:find("secondary rate limit", 1, true) ~= nil then
    return true
  end
  if lower:find("abuse", 1, true) ~= nil and lower:find("rate", 1, true) ~= nil then
    return true
  end
  if lower:find("http 429", 1, true) ~= nil or lower:find("status 429", 1, true) ~= nil then
    return true
  end
  if lower:find("429 too many requests", 1, true) ~= nil or lower:find("too many requests", 1, true) ~= nil then
    return true
  end
  return false
end

function M.gh_error_class(result)
  if M.is_gh_rate_limited(result) then
    return "gh-rate-limited"
  end
  return "gh-command-failed"
end

function M.is_gh_rate_limit_error(err)
  if type(err) == "table" then
    return err.class == "gh-rate-limited"
  end
  return tostring(err):find("gh-rate-limited", 1, true) ~= nil
end

function M.gh_error(context, result)
  local class = M.gh_error_class(result)
  local prefix = "github-proxy: " .. tostring(context)
  return {
    class = class,
    retryable = class == "gh-rate-limited",
    result = result,
    message = prefix .. " failed: " .. class .. ": " .. command_result_stderr(result),
  }
end

function M.gh_error_message(context, result)
  return M.gh_error(context, result).message
end

function M.configure_trusted_bot_login(login)
  if login == nil or tostring(login) == "" then
    trusted_bot_login = nil
    return nil
  end
  trusted_bot_login = M.strip_bot_login_suffix(login)
  return trusted_bot_login
end

function M.assert_trusted_bot_configured()
  local login = M.read_env("FKST_GITHUB_BOT_LOGIN")
  if login ~= nil then
    M.configure_trusted_bot_login(login)
  end

  if trusted_bot_login == nil then
    error("github-proxy: FKST_GITHUB_BOT_LOGIN is required when FKST_GITHUB_WRITE=1")
  end
  return trusted_bot_login
end

function M.entity_cache_key(repo, entity_type, number)
  return "github-proxy/" .. tostring(entity_type) .. "/" .. tostring(repo) .. "/" .. tostring(number)
end

function M.entity_dedup_key(repo, entity_type, number, updated_at)
  return tostring(repo)
    .. "#"
    .. tostring(entity_type)
    .. "#"
    .. tostring(number)
    .. "@"
    .. tostring(updated_at)
end

function M.issue_dedup_key(repo, number, updated_at)
  return M.entity_dedup_key(repo, "issue", number, updated_at)
end

-- Stable source pointer for the durable-delivery engine: a reliable consumer
-- re-derives the current entity from this ref (e.g. REST issue detail) instead of
-- trusting a possibly-stale payload. ref is the entity identity WITHOUT the
-- version (updated_at lives in dedup_key / the payload).
function M.entity_source_ref(repo, entity_type, number)
  return {
    kind = "external",
    ref = tostring(repo) .. "#" .. tostring(entity_type) .. "/" .. tostring(number),
  }
end

local function sanitize_runtime_segment(value)
  local safe = tostring(value or ""):gsub("[^%w._-]", "-")
  safe = safe:gsub("-+", "-"):gsub("^-+", ""):gsub("-+$", "")
  if safe == "" then
    return "empty"
  end
  return safe
end

function M.issue_label_lock_key(repo, issue_number)
  local id = sanitize_runtime_segment(repo) .. "/issue/" .. sanitize_runtime_segment(issue_number)
  if #id > 180 then
    id = id:sub(1, 180)
  end
  return "github-proxy/label-lock/" .. id
end

function M.entity_label_lock_key(repo, target_kind, number)
  local kind = tostring(target_kind or "issue")
  if kind ~= "issue" and kind ~= "pr" then
    kind = "issue"
  end
  local id = sanitize_runtime_segment(repo) .. "/" .. kind .. "/" .. sanitize_runtime_segment(number)
  if #id > 180 then
    id = id:sub(1, 180)
  end
  return "github-proxy/label-lock/" .. id
end

function M.is_safe_branch(branch)
  return is_git_ref_safe(branch)
end

function M.is_safe_pr_number(pr_number) return M.is_positive_integer(pr_number) end

function M.is_safe_head_sha(head_sha) return is_git_sha(head_sha) end

function M.parse_entity_list(gh_json_stdout, entity_type)
  local decoded = json.decode(gh_json_stdout or "[]")
  local entities = {}
  local function parse_assignees(value)
    local assignees = {}
    for _, assignee in ipairs(value.assignees or {}) do
      if type(assignee) == "table" and assignee.login ~= nil then
        table.insert(assignees, tostring(assignee.login))
      elseif type(assignee) == "string" then
        table.insert(assignees, assignee)
      end
    end
    return assignees
  end

  local function visit_items(value)
    if type(value) ~= "table" then
      return
    end
    if value.number ~= nil then
      local number = tonumber(value.number)
      if number ~= nil and (entity_type ~= "issue" or value.pull_request == nil) then
        local labels, item_state = json.decode("[]"), value.state
        for _, label in ipairs(value.labels or {}) do
          if type(label) == "table" and label.name ~= nil then
            table.insert(labels, tostring(label.name))
          elseif type(label) == "string" then
            table.insert(labels, label)
          end
        end
        if type(item_state) == "string" then
          item_state = item_state:upper()
        end
        local author = value.user or value.author
        local author_login = type(author) == "table" and author.login or author
        table.insert(entities, { number = number, title = value.title, url = value.url or value.html_url,
          updated_at = value.updatedAt or value.updated_at, state = item_state, labels = labels,
          assignees = parse_assignees(value), author_login = author_login })
      end
      return
    end
    for _, item in ipairs(value) do
      visit_items(item)
    end
  end
  visit_items(decoded)
  return entities
end

function M.has_label(labels, expected)
  if type(labels) ~= "table" then
    return false
  end
  for _, label in ipairs(labels) do
    if tostring(label) == expected then
      return true
    end
  end
  return false
end

function M.parse_issue_state(gh_json_stdout)
  local decoded = json.decode(gh_json_stdout or "{}")
  local labels = {}
  for _, label in ipairs(decoded.labels or {}) do
    if type(label) == "table" and label.name ~= nil then
      table.insert(labels, tostring(label.name))
    elseif type(label) == "string" then
      table.insert(labels, label)
    end
  end
  return {
    labels = labels,
    comments = M.parse_issue_comments(gh_json_stdout),
    assignees = M.assignee_logins(decoded.assignees),
  }
end

function M.parse_issue_list(gh_json_stdout)
  return M.parse_entity_list(gh_json_stdout, "issue")
end

function M.github_issue_list(repo, timeout)
  return M.github().issue_list(repo, timeout or 30)
end

function M.github_pr_list(repo, timeout)
  return M.github().pr_list(repo, timeout or 30)
end

function M.github_pr_list_head(repo, branch, base_branch, timeout)
  if not is_git_ref_safe(branch) then
    error("github-proxy: invalid branch")
  end
  if base_branch ~= nil and not is_git_ref_safe(base_branch) then
    error("github-proxy: invalid base branch")
  end
  return M.github().pr_list_head(repo, branch, base_branch, timeout or 30)
end

function M.parse_pr_list_for_head(gh_json_stdout, branch)
  local decoded = json.decode(gh_json_stdout or "[]")
  for _, item in ipairs(decoded) do
    local items = (type(item) == "table" and item[1] ~= nil) and item or { item }
    for _, pr in ipairs(items) do
      if type(pr) == "table" then
        local number = pr.number
        local head = pr.headRefName or pr.head_ref_name
        if head == nil and type(pr.head) == "table" then
          head = pr.head.ref
        end
        local base = pr.baseRefName or pr.base_ref_name
        if base == nil and type(pr.base) == "table" then
          base = pr.base.ref
        end
        local state = tostring(pr.state or "")
        if M.is_positive_integer(number)
          and tostring(head or "") == tostring(branch)
          and state:lower() == "open" then
          return {
            number = tonumber(number),
            url = pr.url or pr.html_url,
            head_ref_name = head,
            base_ref_name = base,
            state = pr.state,
          }
        end
      end
    end
  end
  return nil
end

function M.git_push_branch(branch, timeout)
  if not is_git_ref_safe(branch) then
    error("github-proxy: invalid branch")
  end
  return M.git().push_branch(branch, timeout or 120)
end

function M.git_show_ref_branch(branch, timeout)
  if not is_git_ref_safe(branch) then
    error("github-proxy: invalid branch")
  end
  return M.git().show_ref_branch(branch, timeout or 30)
end

function M.git_is_ancestor(maybe_ancestor_sha, descendant_sha, timeout)
  if not is_git_sha(maybe_ancestor_sha) then
    error("github-proxy: invalid ancestor sha")
  end
  if not is_git_sha(descendant_sha) then
    error("github-proxy: invalid descendant sha")
  end
  return M.git().is_ancestor(maybe_ancestor_sha, descendant_sha, timeout or 30)
end

function M.parse_git_show_ref_head(stdout, branch)
  local head_sha, ref = tostring(stdout or ""):match("^%s*([0-9a-fA-F]+)%s+(%S+)")
  if is_git_sha(head_sha) and ref == "refs/heads/" .. tostring(branch) then
    return head_sha:lower()
  end
  return nil
end

function M.github_pr_create(repo, branch, base_branch, title, body_file, timeout)
  if not is_git_ref_safe(branch) then
    error("github-proxy: invalid branch")
  end
  if base_branch ~= nil and not is_git_ref_safe(base_branch) then
    error("github-proxy: invalid base branch")
  end
  return M.github().pr_create(repo, branch, base_branch, title, body_file, timeout or 60)
end

function M.parse_pr_create(stdout)
  local url = tostring(stdout or ""):match("(https?://%S+/pull/(%d+))")
  local number = url and url:match("/pull/(%d+)")
  if M.is_positive_integer(number) then
    return {
      number = tonumber(number),
      url = url,
    }
  end
  return nil
end

function M.github_pr_view_head_oid(repo, pr_number, timeout)
  if not M.is_positive_integer(pr_number) then
    error("github-proxy: invalid PR number")
  end
  return M.github().pr_view(repo, pr_number, timeout or 30)
end

local function repository_name_with_owner(head_repository, head_repository_owner)
  if type(head_repository) == "string" then
    return head_repository
  end
  if type(head_repository) ~= "table" then
    return nil
  end
  if head_repository.nameWithOwner ~= nil and head_repository.nameWithOwner ~= "" then
    if type(head_repository.nameWithOwner) == "userdata" then
      return nil
    end
    return tostring(head_repository.nameWithOwner)
  end
  if head_repository.name_with_owner ~= nil and head_repository.name_with_owner ~= "" then
    if type(head_repository.name_with_owner) == "userdata" then
      return nil
    end
    return tostring(head_repository.name_with_owner)
  end
  local name = head_repository.name
  local owner = nil
  if type(head_repository.owner) == "table" and head_repository.owner.login ~= nil then
    owner = head_repository.owner.login
  elseif type(head_repository_owner) == "table" and head_repository_owner.login ~= nil then
    owner = head_repository_owner.login
  elseif type(head_repository_owner) == "string" then
    owner = head_repository_owner
  end
  if owner ~= nil and name ~= nil then
    return tostring(owner) .. "/" .. tostring(name)
  end
  return nil
end

local function nil_if_json_null(value)
  if type(value) == "userdata" then
    return nil
  end
  return value
end

function M.parse_pr_view_head_state(gh_json_stdout, target_repo)
  local decoded = json.decode(gh_json_stdout or "{}")
  if decoded.headRefOid == nil and decoded.head_ref_oid == nil and decoded.head ~= nil then
    decoded = json.decode(M.rest_pr_to_view_json(gh_json_stdout or "{}", "[]") or "{}")
  end
  local head = decoded.headRefOid or decoded.head_ref_oid
  local state = decoded.state
  local head_repo = repository_name_with_owner(
    nil_if_json_null(decoded.headRepository or decoded.head_repository),
    nil_if_json_null(decoded.headRepositoryOwner or decoded.head_repository_owner)
  )
  local is_cross_repository = nil_if_json_null(decoded.isCrossRepository)
  if is_cross_repository == nil then
    is_cross_repository = nil_if_json_null(decoded.is_cross_repository)
  end
  if is_git_sha(head) and state ~= nil then
    return {
      head_ref_oid = tostring(head):lower(),
      base_ref_name = decoded.baseRefName or decoded.base_ref_name,
      state = tostring(state),
      head_repository = head_repo,
      is_cross_repository = is_cross_repository,
      is_target_repository = target_repo ~= nil
        and head_repo ~= nil
        and tostring(head_repo):lower() == tostring(target_repo):lower(),
    }
  end
  return nil
end

function M.github_label_list(repo, timeout)
  return M.github().label_list(repo, timeout or 30)
end

local default_label_color = "ededed"

local function normalize_label_color(value)
  if type(value) ~= "string" then
    return default_label_color
  end
  local color = value:gsub("^%s+", ""):gsub("%s+$", ""):gsub("^#", "")
  if color:match("^[0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f]$") ~= nil then
    return color:upper()
  end
  return default_label_color
end

function M.github_label_create(repo, label, timeout, color)
  color = normalize_label_color(color)
  return M.github().label_create(repo, label, color, timeout or 30)
end

function M.parse_issue_labels(gh_json_stdout)
  local decoded = json.decode(gh_json_stdout or "{}")
  local labels = {}
  for _, label in ipairs(decoded.labels or {}) do
    if type(label) == "table" and label.name ~= nil then
      table.insert(labels, tostring(label.name))
    elseif type(label) == "string" then
      table.insert(labels, label)
    end
  end
  return labels
end

function M.parse_entity_label_view(gh_json_stdout)
  local decoded = json.decode(gh_json_stdout or "{}")
  return {
    labels = M.parse_issue_labels(gh_json_stdout),
    comments = M.parse_issue_comments(gh_json_stdout),
    raw = decoded,
  }
end

function M.parse_repo_labels(gh_json_stdout)
  local decoded = json.decode(gh_json_stdout or "[]")
  local labels = {}
  for _, label in ipairs(decoded or {}) do
    if type(label) == "table" and label.name ~= nil then
      table.insert(labels, tostring(label.name))
    elseif type(label) == "string" then
      table.insert(labels, label)
    end
  end
  return labels
end

local function label_set(labels)
  local set = {}
  for _, label in ipairs(labels or {}) do
    set[tostring(label)] = true
  end
  return set
end

local function normalized_unique_labels(labels)
  local unique = {}
  local seen = {}
  for _, label in ipairs(labels or {}) do
    local text = tostring(label)
    if text ~= "" and not seen[text] then
      seen[text] = true
      table.insert(unique, text)
    end
  end
  return unique
end

function M.normalize_labels(value)
  local labels = {}
  if type(value) ~= "table" then
    return labels
  end
  for _, label in ipairs(value) do
    if label ~= nil and tostring(label) ~= "" then
      table.insert(labels, tostring(label))
    end
  end
  return labels
end

function M.is_gh_label_already_exists(result)
  local lower = command_result_stderr(result):lower()
  return lower:find("already exists", 1, true) ~= nil
    or lower:find("name already exists", 1, true) ~= nil
end

function M.ensure_repo_label(repo, label, existing_labels, label_colors)
  if existing_labels[label] then
    return true
  end

  local ok, result_or_error = M.gh_exec_result(function(timeout)
    local color = type(label_colors) == "table" and label_colors[label] or nil
    return M.github_label_create(repo, label, timeout, color)
  end, 30, "label create")
  if not ok then
    local raw_result = result_or_error.result
    if raw_result == nil or not M.is_gh_label_already_exists(raw_result) then
      error(result_or_error.message)
    end
  end
  existing_labels[label] = true
  return true
end

function M.github_issue_edit_labels(repo, issue_number, add_labels, remove_labels, timeout)
  return M.github().issue_edit_labels(repo, issue_number, add_labels, remove_labels, timeout or 30)
end

function M.github_pr_edit_labels(repo, pr_number, add_labels, remove_labels, timeout)
  return M.github().pr_edit_labels(repo, pr_number, add_labels, remove_labels, timeout or 30)
end

function M.apply_entity_labels(repo, target_kind, number, add_labels, remove_labels, label_colors)
  local add = normalized_unique_labels(add_labels)
  local remove = normalized_unique_labels(remove_labels)
  if #add == 0 and #remove == 0 then
    return false
  end

  local listed = M.gh_exec(function(timeout)
    return M.github_label_list(repo, timeout)
  end, 30, "label list")
  local existing = label_set(M.parse_repo_labels(listed.stdout))

  for _, label in ipairs(add) do
    M.ensure_repo_label(repo, label, existing, label_colors)
  end

  local safe_remove = {}
  for _, label in ipairs(remove) do
    if existing[label] then
      table.insert(safe_remove, label)
    else
      log.info("github-proxy: label remove skipped because repo label is missing: " .. label)
    end
  end

  if #add == 0 and #safe_remove == 0 then
    return false
  end

  local kind = tostring(target_kind or "issue")
  local edit = nil
  local edit_context = nil
  if kind == "issue" then
    edit = function(timeout)
      return M.github_issue_edit_labels(repo, number, add, safe_remove, timeout)
    end
    edit_context = "issue edit-labels"
  elseif kind == "pr" then
    edit = function(timeout)
      return M.github_pr_edit_labels(repo, number, add, safe_remove, timeout)
    end
    edit_context = "pr edit-labels"
  else
    error("github-proxy: invalid label target kind")
  end

  M.gh_exec(
    edit,
    30,
    edit_context
  )
  M.invalidate_entity_after_write(repo, kind, number)
  return true
end

function M.apply_issue_labels(repo, issue_number, add_labels, remove_labels, label_colors)
  return M.apply_entity_labels(repo, "issue", issue_number, add_labels, remove_labels, label_colors)
end

return M
