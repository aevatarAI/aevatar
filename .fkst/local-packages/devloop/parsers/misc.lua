local devloop_base = require("devloop.base")
local error_facts = require("contract.error_facts")
local forge_validators = require("devloop.forge_validators")
local shared = require("devloop.parsers.shared")
local strings = require("forge.strings")
local C = {}

function C.comments_from_json(M, comments_json)
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
      table.insert(comments, {
        body = comment,
        author_login = M._test_bot_login,
      })
    end
  end
  return comments
end

function C.parse_dashboard_issue_list(M, stdout)
  local decoded = json.decode(stdout or "[]")
  local items = {}
  if type(decoded) ~= "table" then
    return items
  end
  shared.each_paginated_item(M, decoded, function(issue)
    if type(issue) == "table" and tonumber(issue.number) ~= nil then
      local author_login = nil
      if type(issue.author) == "table" and issue.author.login ~= nil then
        author_login = tostring(issue.author.login)
      elseif issue.author_login ~= nil then
        author_login = tostring(issue.author_login)
      elseif type(issue.user) == "table" and issue.user.login ~= nil then
        author_login = tostring(issue.user.login)
      end
      table.insert(items, {
        number = tonumber(issue.number),
        title = tostring(issue.title or ""),
        author_login = author_login,
        body = tostring(issue.body or ""),
        labels = issue.labels,
        updated_at = issue.updated_at or issue.updatedAt,
      })
    end
  end)
  return items
end

function C.parse_repo_labels(M, stdout)
  local decoded = json.decode(stdout or "[]")
  local items = {}
  shared.each_paginated_item(M, decoded, function(label)
    if type(label) == "table" and label.name ~= nil then
      table.insert(items, {
        name = tostring(label.name),
        color = label.color and tostring(label.color) or nil,
        description = label.description and tostring(label.description) or nil,
      })
    end
  end)
  return items
end

local function comment_author_login(M, comment)
  -- Normalize the comment author login so an author read as "<slug>[bot]" (REST)
  -- matches a bare-"<slug>" configured bot login (GraphQL). No-op for ordinary logins.
  if type(comment) == "table" then
    if comment.author_login ~= nil then
      return devloop_base.strip_bot_login_suffix(comment.author_login)
    end
    if type(comment.author) == "table" and comment.author.login ~= nil then
      return devloop_base.strip_bot_login_suffix(comment.author.login)
    end
    if type(comment.user) == "table" and comment.user.login ~= nil then
      return devloop_base.strip_bot_login_suffix(comment.user.login)
    end
    return nil
  end
  return M._test_bot_login
end

local function comment_created_at(_M, comment)
  if type(comment) == "table" then
    return comment.created_at
  end
  return nil
end

local function is_trusted_comment(M, comment, trust_set)
  -- Parser-only trust filtering keeps the test default; pre-assert ownership gates use claim_owner.
  local author = comment_author_login(M, comment)
  if type(trust_set) == "table" then
    return trust_set[author] == true
  end
  return author == devloop_base.trusted_bot_login()
end

local function trusted_marker_comments(M, comments, trust_set)
  local filtered = {}
  if type(comments) ~= "table" then
    return filtered
  end
  for _, comment in ipairs(comments) do
    if is_trusted_comment(M, comment, trust_set) then
      table.insert(filtered, comment)
    end
  end
  return filtered
end

function C.comment_body(_M, comment)
  return strings.comment_body(comment)
end

function C.comment_author_login(M, comment)
  return comment_author_login(M, comment)
end

function C.comment_created_at(M, comment)
  return comment_created_at(M, comment)
end

function C._comment_body(M, comment)
  return C.comment_body(M, comment)
end

function C._comment_author_login(M, comment)
  return C.comment_author_login(M, comment)
end

function C._comment_created_at(M, comment)
  return C.comment_created_at(M, comment)
end

function C._is_trusted_comment(M, comment, trust_set)
  return is_trusted_comment(M, comment, trust_set)
end

function C._trusted_marker_comments(M, comments, trust_set)
  return trusted_marker_comments(M, comments, trust_set)
end

local function upper_text(value)
  return tostring(value or ""):upper()
end

local function check_entry_state(entry)
  if type(entry) ~= "table" then
    return nil, nil
  end
  return upper_text(entry.state or entry.status), upper_text(entry.conclusion)
end

local green_check_conclusions = {
  SUCCESS = true,
  -- NEUTRAL is excluded for irreversible-merge safety.
  SKIPPED = true,
}

local green_status_states = {
  SUCCESS = true,
}

local red_status_states = {
  ERROR = true,
  FAILURE = true,
}

local max_rollup_check_name_len = 80
local max_rollup_failure_summary_len = 200
local max_rollup_failure_checks = 3

local function safe_rollup_check_name(M, entry)
  local name = "unknown"
  if type(entry) == "table" then
    name = tostring(entry.name or entry.context or entry.workflowName or entry.workflow_name or "")
    if name == "" then
      name = "unknown"
    end
  end
  name = devloop_base.neutralize_untrusted_comment_text(devloop_base._neutralize_fkst_markers(name))
  name = error_facts.one_line(name):gsub("[%c]", " "):gsub("^%s+", ""):gsub("%s+$", "")
  if name == "" then
    name = "unknown"
  end
  if #name > max_rollup_check_name_len then
    name = name:sub(1, max_rollup_check_name_len)
  end
  return name
end

local function entry_commit_sha(entry)
  if type(entry) ~= "table" then
    return nil
  end
  local candidates = {
    entry.headSha,
    entry.head_sha,
    entry.sha,
    entry.oid,
  }
  if type(entry.commit) == "table" then
    table.insert(candidates, entry.commit.oid)
    table.insert(candidates, entry.commit.sha)
  end
  if type(entry.checkSuite) == "table" then
    table.insert(candidates, entry.checkSuite.headSha)
    table.insert(candidates, entry.checkSuite.head_sha)
    if type(entry.checkSuite.commit) == "table" then
      table.insert(candidates, entry.checkSuite.commit.oid)
      table.insert(candidates, entry.checkSuite.commit.sha)
    end
  end
  if type(entry.check_suite) == "table" then
    table.insert(candidates, entry.check_suite.headSha)
    table.insert(candidates, entry.check_suite.head_sha)
    if type(entry.check_suite.commit) == "table" then
      table.insert(candidates, entry.check_suite.commit.oid)
      table.insert(candidates, entry.check_suite.commit.sha)
    end
  end
  for _, candidate in ipairs(candidates) do
    if forge_validators.is_git_sha(candidate) then
      return tostring(candidate)
    end
  end
  return nil
end

function C.pr_rollup_failure_summary(M, pr)
  local entries = type(pr) == "table" and pr.status_check_rollup or nil
  if type(entries) ~= "table" or #entries == 0 then
    return ""
  end
  local failed = {}
  local failed_total = 0
  for _, entry in ipairs(entries) do
    local state, conclusion = check_entry_state(entry)
    local is_failed = false
    if state == "COMPLETED" then
      is_failed = not green_check_conclusions[conclusion]
    elseif conclusion == "" and red_status_states[state] then
      is_failed = true
    end
    if is_failed then
      local status = state
      if conclusion ~= "" then
        status = status .. "/" .. conclusion
      end
      failed_total = failed_total + 1
      if #failed < max_rollup_failure_checks then
        table.insert(failed, safe_rollup_check_name(M, entry) .. ": " .. status)
      end
    end
  end
  if #failed == 0 then
    return ""
  end
  local summary = table.concat(failed, "; ")
  if failed_total > #failed then
    local suffix = "; (+" .. tostring(failed_total - #failed) .. " more)"
    local head_limit = max_rollup_failure_summary_len - #suffix
    if head_limit < 1 then
      head_limit = 1
    end
    if #summary > head_limit then
      summary = summary:sub(1, head_limit):gsub(";?%s*$", "")
    end
    summary = summary .. suffix
  end
  if #summary > max_rollup_failure_summary_len then
    summary = summary:sub(1, max_rollup_failure_summary_len)
  end
  summary = summary:gsub("[%c]", " "):gsub("^%s+", ""):gsub("%s+$", "")
  return summary
end

function C.rollup_failure_gate_sha(_M, pr)
  local entries = type(pr) == "table" and pr.status_check_rollup or nil
  if type(entries) ~= "table" or #entries == 0 then
    return nil
  end
  local gate_sha = nil
  for _, entry in ipairs(entries) do
    local state, conclusion = check_entry_state(entry)
    local is_failed = false
    if state == "COMPLETED" then
      is_failed = not green_check_conclusions[conclusion]
    elseif conclusion == "" and red_status_states[state] then
      is_failed = true
    end
    if is_failed then
      local sha = entry_commit_sha(entry)
      if sha == nil then
        return nil
      end
      if gate_sha == nil then
        gate_sha = sha
      elseif gate_sha ~= sha then
        return nil
      end
    end
  end
  return gate_sha
end

C.max_rollup_check_name_len = max_rollup_check_name_len
C.max_rollup_failure_summary_len = max_rollup_failure_summary_len

function C.is_ci_red_reason(_M, reason)
  return tostring(reason or "") == "own-ci-red"
end

function C.is_ci_wait_reason(_M, reason)
  local text = tostring(reason or "")
  return text == "external-ci-red"
    or text == "integration-ci-red"
    or text == "ci-unknown"
    or text == "checks-pending"
    or text == "rollup-pending"
end

function C._upper_text(_M, value)
  return upper_text(value)
end

return C
