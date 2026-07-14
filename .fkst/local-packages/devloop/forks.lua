local devloop_base = require("devloop.base")
local m_claims = require("devloop.claims")
local parsers_misc = require("devloop.parsers.misc")
local parsers_issue = require("devloop.parsers.issue")
local base_ids = require("devloop.base_ids")

local F = {}

local max_login_len = 80

function F.managed_fork_trust_set(core, bot_login, managed)
  local trust_set = {}
  if type(managed) == "table" then
    for login, trusted in pairs(managed) do
      if trusted then trust_set[login] = true end
    end
  elseif type(core) == "table" then
    for login, trusted in pairs(m_claims.managed_bot_logins(core) or {}) do
      if trusted then trust_set[login] = true end
    end
  end
  local normalized = devloop_base.strip_bot_login_suffix(bot_login)
  if normalized ~= nil and normalized ~= "" then
    trust_set[normalized] = true
  end
  return trust_set
end

local function is_trusted_fork_marker_author(core, comment, trust_set)
  return parsers_misc._is_trusted_comment(core, comment, trust_set)
end

local function safe_marker_attr(value)
  local text = tostring(value or ""):gsub("<!%-%- fkst:[^\n]*%-%->", " ")
  text = text:gsub("&lt;!%-%- fkst:[^\n]*%-%-&gt;", " ")
  text = text:gsub("%c", " "):gsub('"', "'"):gsub("[<>]", ""):gsub("%s+", " ")
  text = text:gsub("^%s+", ""):gsub("%s+$", "")
  if #text > 240 then
    text = base_ids.truncate_utf8(text, 240)
  end
  return text
end

function F.fork_issue_dedup_key(repo, issue_number)
  if not base_ids.issue_ref_round_trips(repo, issue_number) then
    error("github-devloop: invalid fork issue target")
  end
  return base_ids.dedup_key({
    "github-devloop",
    "fork",
    base_ids.safe_repo(repo),
    "issue",
    base_ids.safe_issue(issue_number),
    "v1",
  })
end

function F.has_trusted_issue_create_parent_marker(core, comments, dedup_key, bot_login, managed)
  if type(comments) ~= "table" then
    return false
  end
  local trust_set = F.managed_fork_trust_set(core, bot_login, managed)
  local create_pattern = "<!%-%- fkst:github%-proxy:issue%-create%-intent:v1.-%-%->"
  local created_pattern = "<!%-%- fkst:github%-proxy:issue%-created:v1.-%-%->"
  for _, comment in ipairs(comments) do
    if is_trusted_fork_marker_author(core, comment, trust_set) then
      local body = parsers_misc.comment_body(core, comment)
      for marker in body:gmatch(create_pattern) do
        if marker:match('dedup="([^"]+)"') == tostring(dedup_key) then
          return true
        end
      end
      for marker in body:gmatch(created_pattern) do
        if marker:match('dedup="([^"]+)"') == tostring(dedup_key) then
          return true
        end
      end
    end
  end
  return false
end

function F.trusted_issue_created_number(core, comments, dedup_key, bot_login, managed)
  if type(comments) ~= "table" then
    return nil
  end
  local trust_set = F.managed_fork_trust_set(core, bot_login, managed)
  local created_pattern = "<!%-%- fkst:github%-proxy:issue%-created:v1.-%-%->"
  for _, comment in ipairs(comments) do
    if is_trusted_fork_marker_author(core, comment, trust_set) then
      local body = parsers_misc.comment_body(core, comment)
      for marker in body:gmatch(created_pattern) do
        if marker:match('dedup="([^"]+)"') == tostring(dedup_key) then
          local issue_number = tonumber(marker:match('issue="(%d+)"'))
          if issue_number ~= nil and issue_number > 0 and issue_number % 1 == 0 then
            return math.floor(issue_number)
          end
        end
      end
    end
  end
  return nil
end

function F.fork_issue_title(issue_number, original_title)
  local title = tostring(original_title or "Issue")
  title = title:gsub("%c", " "):gsub("%s+", " "):gsub("^%s+", ""):gsub("%s+$", "")
  if title == "" then
    title = "Issue"
  end
  local prefix = "Fork of #" .. tostring(issue_number) .. ": "
  local limit = base_ids.max_title_len - #prefix
  if limit < 1 then
    limit = 1
  end
  if #title > limit then
    title = base_ids.truncate_utf8(title, limit)
  end
  return prefix .. title
end

function F.fork_origin_marker(repo, issue_number, author_login, source_ref)
  local normalized = base_ids.normalize_source_ref(source_ref or base_ids.issue_source_ref(repo, issue_number))
  return '<!-- fkst:github-devloop:fork-origin:v1 repo="' .. safe_marker_attr(repo)
    .. '" issue="' .. safe_marker_attr(issue_number)
    .. '" author="' .. safe_marker_attr(author_login or "unknown")
    .. '" source_ref_kind="' .. safe_marker_attr(normalized.kind)
    .. '" source_ref="' .. safe_marker_attr(normalized.ref)
    .. '" -->'
end

local function fork_origin_fact_from_text(core, text)
  for marker in tostring(text or ""):gmatch("<!%-%- fkst:github%-devloop:fork%-origin:v1.-%-%->") do
    local source_ref = {
      kind = marker:match('source_ref_kind="([^"]+)"'),
      ref = marker:match('source_ref="([^"]+)"'),
    }
    local repo, issue_number = devloop_base.parse_issue_source_ref(source_ref)
    if repo ~= nil and issue_number ~= nil then
      return {
        repo = repo,
        issue_number = issue_number,
        source_ref = source_ref,
      }
    end
  end
  return nil
end

function F.fork_origin_fact(core, entity, managed)
  if type(entity) ~= "table" then
    return nil
  end
  local trust_set = F.managed_fork_trust_set(core, m_claims.claim_owner(), managed)
  if m_claims.is_managed_bot_login(core, m_claims.issue_author_login(core, entity), trust_set) then
    local body_fact = fork_origin_fact_from_text(core, entity.body)
    if body_fact ~= nil then
      return body_fact
    end
  end
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(core, entity.comments, trust_set)) do
    local comment_fact = fork_origin_fact_from_text(core, parsers_misc.comment_body(core, comment))
    if comment_fact ~= nil then
      return comment_fact
    end
  end
  local title_issue = tostring(entity.title or ""):match("^Fork of #(%d+):")
  if title_issue ~= nil then
    local source_ref = base_ids.issue_source_ref(entity.repo, title_issue)
    local repo, issue_number = devloop_base.parse_issue_source_ref(source_ref)
    if repo ~= nil and issue_number ~= nil then
      return {
        repo = repo,
        issue_number = issue_number,
        source_ref = source_ref,
      }
    end
  end
  return nil
end

function F.rederive_issue_state(core, repo, issue_number)
  local view = core.gh_issue_view_state(repo, issue_number, 30)
  if view.exit_code ~= 0 then
    error("github-devloop: gh issue source_ref state recheck failed: " .. tostring(view.stderr))
  end
  return parsers_issue.parse_issue_view_state(core, view.stdout)
end

function F.rederive_issue_is_open(core, repo, issue_number)
  local current = F.rederive_issue_state(core, repo, issue_number)
  return tostring(current.state or ""):upper() == "OPEN", current
end

function F.fork_issue_body(repo, issue_number, author_login, source_ref)
  local normalized = base_ids.normalize_source_ref(source_ref or base_ids.issue_source_ref(repo, issue_number))
  return table.concat({
    "Self-owned fork for isolated implementation.",
    "",
    "Original: " .. tostring(repo) .. "#" .. tostring(issue_number),
    "Original author: " .. tostring(author_login or "unknown"),
    "Source ref: " .. tostring(normalized.kind) .. ":" .. tostring(normalized.ref),
    "",
    F.fork_origin_marker(repo, issue_number, author_login, normalized),
  }, "\n")
end

function F.build_fork_issue_create_request(core, repo, issue_number, current, source_ref)
  if tostring(current and current.state or ""):upper() ~= "OPEN" then
    return nil, "original-closed"
  end
  local author_login = m_claims.issue_author_login(core, current)
  if author_login == nil or #author_login > max_login_len then
    return nil, "author-unknown"
  end
  local dedup_key = F.fork_issue_dedup_key(repo, issue_number)
  local normalized = base_ids.normalize_source_ref(source_ref or base_ids.issue_source_ref(repo, issue_number))
  return {
    schema = "github-proxy.issue-create.v1",
    repo = tostring(repo),
    title = F.fork_issue_title(issue_number, current and current.title),
    body = F.fork_issue_body(repo, issue_number, author_login, normalized),
    assignees = { m_claims.claim_owner() },
    dedup_key = dedup_key,
    external_effect_saga = "fork-and-block",
    external_effect_step = "create-fork",
    parent_comment_target = {
      repo = tostring(repo),
      issue_number = tonumber(issue_number),
    },
    post_create_blocked_by = {
      blocked_issue_number = tonumber(issue_number),
      dedup_key = dedup_key .. "/blocked-by",
      external_effect_saga = "fork-and-block",
      external_effect_step = "block-original",
    },
    source_ref = normalized,
  }, nil
end

return F
