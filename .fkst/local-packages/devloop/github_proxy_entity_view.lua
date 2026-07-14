local C = {}
local github_view = require("forge.github_view")
local gh_exec_mod = require("devloop.gh_exec")
local github_handle = nil

local parse_view_updated_at = github_view.parse_view_updated_at
local parse_updated_at_stdout = github_view.parse_updated_at_stdout
local json_string = github_view.json_string
local json_value = github_view.json_value
local rest_state = github_view.rest_state
local rest_pr_state = github_view.rest_pr_state
local append_comments = github_view.append_comments
local labels_json = github_view.labels_json
local assignees_json = github_view.assignees_json
local repo_name_with_owner = github_view.repo_name_with_owner
local repo_owner_login = github_view.repo_owner_login
local decode_comments_json = function(stdout) return github_view.decode_comments_json(stdout, "github-devloop: REST") end

local max_cache_key_segment_len = 120

local function github()
  if github_handle ~= nil then
    return github_handle
  end
  if type(exec_argv) ~= "function" then
    error("github-devloop: GitHub adapter requires exec_argv")
  end
  github_handle = require("forge.github").new(exec_argv)
  return github_handle
end

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

local function entity_view_cache_key(repo, kind, number)
  return "github-proxy/view/"
    .. sanitize_cache_segment(repo, true)
    .. "/"
    .. sanitize_cache_segment(kind, false)
    .. "/"
    .. sanitize_cache_segment(number, false)
end

local function decode_cached_view(encoded)
  local ok, decoded = pcall(json.decode, encoded or "")
  if not ok or type(decoded) ~= "table" then
    return nil
  end
  if type(decoded.stdout) ~= "string" then
    return nil
  end
  if decoded.updated_at == nil or tostring(decoded.updated_at) == "" then
    return nil
  end
  decoded.updated_at = tostring(decoded.updated_at)
  return decoded
end

local function rest_pr_mergeable(value)
  if value == true then
    return "MERGEABLE"
  end
  if value == false then
    return "CONFLICTING"
  end
  return "UNKNOWN"
end

local function rest_pr_merge_state_status(value)
  if value == nil or tostring(value) == "" then
    return "UNKNOWN"
  end
  return tostring(value):upper()
end

local function adapter_error_result(err)
  if type(err) == "table" and type(err.result) == "table" then
    return err.result
  end
  error(err)
end

local function decode_json(stdout)
  local ok, decoded = pcall(json.decode, stdout or "")
  if ok and type(decoded) == "table" then
    return decoded
  end
  error("github-devloop: REST response is not valid JSON")
end

local function decode_entity_json(stdout)
  if stdout == nil or stdout == "" then
    error("github-devloop: REST entity response is empty")
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
      local created_at = comment.createdAt or comment.created_at
      table.insert(parts, '{"id":' .. json_value(id)
        .. ',"body":' .. json_value(comment.body)
        .. ',"author":{"login":' .. json_value(author_login) .. "}"
        .. ',"createdAt":' .. json_value(created_at)
        .. "}")
    end
  end
  return "[" .. table.concat(parts, ",") .. "]"
end

local function rest_comments_to_view_json(comments_stdout)
  local decoded = decode_comments_json(comments_stdout)
  local comments = {}
  append_comments(comments, decoded)
  return comments_json(comments)
end

local function rest_issue_to_view_json(issue_stdout, comments_stdout)
  local issue = decode_entity_json(issue_stdout)
  local comment_source = comments_stdout
  if type(issue.comments) == "table" then
    comment_source = '{"comments":' .. comments_json(issue.comments) .. "}"
  end
  local author_login = nil
  if type(issue.user) == "table" then
    author_login = issue.user.login
  end
  return '{"number":' .. json_value(issue.number)
    .. ',"title":' .. json_value(issue.title)
    .. ',"body":' .. json_value(issue.body)
    .. ',"labels":' .. labels_json(issue.labels)
    .. ',"state":' .. json_value(rest_state(issue.state))
    .. ',"createdAt":' .. json_value(issue.created_at or issue.createdAt)
    .. ',"updatedAt":' .. json_value(issue.updated_at or issue.updatedAt)
    .. ',"assignees":' .. assignees_json(issue.assignees)
    .. ',"author":{"login":' .. json_value(author_login) .. "}"
    .. ',"comments":' .. rest_comments_to_view_json(comment_source)
    .. "}"
end

local function rest_pr_to_view_json(pr_stdout, comments_stdout)
  local pr = decode_entity_json(pr_stdout)
  local head = type(pr.head) == "table" and pr.head or {}
  local base = type(pr.base) == "table" and pr.base or {}
  local head_repo = type(head.repo) == "table" and head.repo or nil
  local base_repo = type(base.repo) == "table" and base.repo or nil
  local head_name_with_owner = repo_name_with_owner(head_repo)
  local base_name_with_owner = repo_name_with_owner(base_repo)
  if head_name_with_owner == nil or base_name_with_owner == nil then
    error("github-devloop: REST PR view missing repository facts")
  end
  local is_cross_repository = tostring(head_name_with_owner):lower() ~= tostring(base_name_with_owner):lower()
  local comment_source = comments_stdout
  if type(pr.comments) == "table" then
    comment_source = '{"comments":' .. comments_json(pr.comments) .. "}"
  end
  local head_repository = '{"nameWithOwner":' .. json_value(head_name_with_owner)
    .. ',"owner":{"login":' .. json_value(repo_owner_login(head_repo)) .. "}}"
  local owner_login = repo_owner_login(head_repo)
  local head_repository_owner = "{}"
  if owner_login ~= nil then
    head_repository_owner = '{"login":' .. json_value(owner_login) .. "}"
  end
  local merged_at = pr.merged_at or pr.mergedAt
  local merged = pr.merged == true or (type(merged_at) == "string" and merged_at ~= "")
  local merge_commit_sha = pr.merge_commit_sha
    or pr.mergeCommitOid
    or pr.merge_commit_oid
    or (type(pr.merge_commit) == "table" and pr.merge_commit.sha or nil)
  return '{"number":' .. json_value(pr.number)
    .. ',"title":' .. json_value(pr.title)
    .. ',"body":' .. json_value(pr.body)
    .. ',"headRefName":' .. json_value(head.ref)
    .. ',"headRefOid":' .. json_value(head.sha)
    .. ',"baseRefName":' .. json_value(base.ref)
    .. ',"baseRefOid":' .. json_value(base.sha)
    .. ',"state":' .. json_value(rest_pr_state(pr))
    .. ',"updatedAt":' .. json_value(pr.updated_at or pr.updatedAt)
    .. ',"isDraft":' .. json_value(pr.draft or pr.isDraft or false)
    .. ',"merged":' .. json_value(merged)
    .. ',"mergedAt":' .. json_value(merged_at)
    .. ',"mergeCommit":{"oid":' .. json_value(merge_commit_sha) .. "}"
    .. ',"labels":' .. labels_json(pr.labels)
    .. ',"comments":' .. rest_comments_to_view_json(comment_source)
    .. ',"headRepository":' .. head_repository
    .. ',"headRepositoryOwner":' .. head_repository_owner
    .. ',"isCrossRepository":' .. json_value(is_cross_repository)
    .. ',"mergeable":' .. json_value(rest_pr_mergeable(pr.mergeable))
    .. ',"mergeStateStatus":' .. json_value(rest_pr_merge_state_status(pr.mergeable_state))
    .. "}"
end

local function rest_issue_view_result(repo, issue_number, timeout)
  local github_port = github()
  local ok_issue, issue = pcall(github_port.issue_rest_view, repo, issue_number, timeout)
  if not ok_issue then
    return adapter_error_result(issue)
  end
  local ok_comments, comments = pcall(github_port.issue_comments, repo, issue_number, timeout)
  if not ok_comments then
    return adapter_error_result(comments)
  end
  local ok, view_json = pcall(rest_issue_to_view_json, issue.stdout, comments.stdout)
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

local function rest_pr_view_result(repo, pr_number, timeout)
  local github_port = github()
  local ok_pr, pr = pcall(github_port.pr_rest_view, repo, pr_number, timeout)
  if not ok_pr then
    return adapter_error_result(pr)
  end
  local ok_comments, comments = pcall(github_port.pr_comments, repo, pr_number, timeout)
  if not ok_comments then
    return adapter_error_result(comments)
  end
  local ok, view_json = pcall(rest_pr_to_view_json, pr.stdout, comments.stdout)
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

local function rest_entity_view_result(repo, kind, number, timeout)
  if kind == "pr" then
    return rest_pr_view_result(repo, number, timeout)
  end
  return rest_issue_view_result(repo, number, timeout)
end

local function encode_cached_view(stdout, updated_at, producer)
  return '{"updated_at":' .. json_string(updated_at)
    .. ',"producer":' .. json_string(producer)
    .. ',"stdout":' .. json_string(stdout or "")
    .. "}"
end

local function success_from_cache(cached)
  return {
    stdout = cached.stdout,
    stderr = "",
    exit_code = 0,
  }
end

local function cache_successful_view(key, result, producer)
  local view_updated_at = parse_view_updated_at(result and result.stdout)
  if type(result) == "table" and result.exit_code == 0 and view_updated_at ~= nil then
    cache_set(key, encode_cached_view(result.stdout or "", view_updated_at, producer))
  end
end

local function fetch_entity_view(repo, kind, number, updated_at, opts)
  local selected_kind = tostring(kind or "")
  if selected_kind ~= "issue" and selected_kind ~= "pr" then
    error("github-devloop: invalid entity view kind")
  end

  local validator = tostring(updated_at or "")
  local options = opts or {}
  local consumer = tostring(options.consumer or "")
  local timeout = tonumber(options.timeout) or 30
  local key = entity_view_cache_key(repo, selected_kind, number)
  if options.force_fresh == true then
    local result = rest_entity_view_result(repo, selected_kind, number, timeout)
    cache_successful_view(key, result, consumer)
    return result
  end

  local cached = decode_cached_view(cache_get(key))
  if validator ~= "" then
    -- GitHub updatedAt is second-granular, so this validator is freshness-best-effort only.
    -- It is safe for stale-tolerant observe reads; authority and write-gate reads must force_fresh.
    if cached ~= nil and cached.updated_at == validator then
      return success_from_cache(cached)
    end
    local result = rest_entity_view_result(repo, selected_kind, number, timeout)
    cache_successful_view(key, result, consumer)
    return result
  end

  if cached ~= nil then
    local ok_current, current = pcall(github().entity_updated_at, repo, selected_kind, number, timeout)
    if not ok_current then
      return adapter_error_result(current)
    end
    if parse_updated_at_stdout(current.stdout) == cached.updated_at then
      return success_from_cache(cached)
    end
    cache_set(key, "")
  end

  local result = rest_entity_view_result(repo, selected_kind, number, timeout)
  cache_successful_view(key, result, consumer)
  return result
end

function C.entity_view_cache_key(M, repo, kind, number)
  return entity_view_cache_key(repo, kind, number)
end

function C.entity_cache_key(repo, entity_type, number)
  return "github-proxy/" .. tostring(entity_type) .. "/" .. tostring(repo) .. "/" .. tostring(number)
end

function C.invalidate_entity_after_write(repo, kind, number)
  local selected_kind = tostring(kind or "")
  if selected_kind ~= "issue" and selected_kind ~= "pr" then
    error("github-devloop: invalid post-write invalidation kind")
  end
  local entity_key = C.entity_cache_key(repo, selected_kind, number)
  local view_key = entity_view_cache_key(repo, selected_kind, number)
  with_lock(entity_key, function()
    cache_set(entity_key, "")
    cache_set(view_key, "")
  end)
end

function C.fetch_entity_view(M, repo, kind, number, updated_at, opts)
  return fetch_entity_view(repo, kind, number, updated_at, opts)
end

function C.cached_entity_view(M, repo, kind, number)
  local selected_kind = tostring(kind or "")
  if selected_kind ~= "issue" and selected_kind ~= "pr" then
    error("github-devloop: invalid cached entity view kind")
  end
  local cached = decode_cached_view(cache_get(entity_view_cache_key(repo, selected_kind, number)))
  if cached == nil then
    return nil
  end
  return success_from_cache(cached)
end

function C.fetch_issue_view(M, repo, issue_number, updated_at, opts)
  return fetch_entity_view(repo, "issue", issue_number, updated_at, opts)
end

function C.fetch_pr_view(repo, pr_number, updated_at, opts)
  return fetch_entity_view(repo, "pr", pr_number, updated_at, opts)
end

function C.fetch_marker_issue_view(M, repo, issue_number, updated_at, opts)
  local options = opts or {}
  options.consumer = options.consumer or "marker-reader"
  return C.fetch_issue_view(M, repo, issue_number, updated_at, options)
end

function C.fetch_marker_pr_view(repo, pr_number, updated_at, opts)
  local options = opts or {}
  options.consumer = options.consumer or "marker-reader"
  return C.fetch_pr_view(repo, pr_number, updated_at, options)
end

function C.fetch_issue_view_state(M, repo, issue_number, updated_at, opts)
  local options = opts or {}
  options.consumer = options.consumer or "observe_issue"
  return C.fetch_marker_issue_view(M, repo, issue_number, updated_at, options)
end

function C.fetch_issue_view_open_pr(M, repo, issue_number, updated_at, opts)
  local options = opts or {}
  options.consumer = options.consumer or "open_pr"
  return C.fetch_marker_issue_view(M, repo, issue_number, updated_at, options)
end

function C.commit_issue_subject_snapshot(M, repo, issue_number)
  if issue_number == nil then
    return {}
  end
  local ok, view = pcall(github().issue_view, repo, issue_number, "number,title", 30)
  if not ok or type(view) ~= "table" then
    return {}
  end
  local decoded_ok, decoded = pcall(json.decode, view.stdout or "{}")
  if not decoded_ok or type(decoded) ~= "table" then
    return {}
  end
  return {
    title = tostring(decoded.title or ""),
  }
end

function C.fetch_pr_view_origin(repo, pr_number, updated_at, opts)
  local options = opts or {}
  options.consumer = options.consumer or "observe_pr"
  return C.fetch_marker_pr_view(repo, pr_number, updated_at, options)
end

-- Opt-in cross-process stale-tolerant read cache (manual TTL). For OBSERVATION
-- /scan reads ONLY: a bounded-stale view is acceptable because the read does
-- not gate an irreversible action and is re-checked on the next poll. Authority
-- reads (merge gate CI/mergeability, version-CAS write gates, claim/head
-- verification, review/fix/implement decisions) MUST NOT use this -- they call
-- adapter force-fresh reads so they always see current truth. The cache value is
-- "<expiry_epoch>\n<stdout>"; only exit_code==0 results are cached. The key must
-- encode the read VARIANT (field-set) so two reads with different fields never
-- share a slot. This collapses the dominant GraphQL drain: the same entity
-- re-read every poll by the same scan dept (measured ~10x duplication on hot
-- issues).
function C.gh_exec_cached(M, cmd, cache_key, ttl_seconds, exec)
  local cached = cache_get(cache_key)
  if type(cached) == "string" and cached ~= "" then
    local sep = cached:find("\n", 1, true)
    local expiry = sep and tonumber(cached:sub(1, sep - 1)) or nil
    if expiry ~= nil and expiry > now() then
      return { stdout = cached:sub(sep + 1), exit_code = 0, cached = true }
    end
  end
  local result
  if type(cmd) == "function" then
    result = cmd()
  else
    result = gh_exec_mod.gh_exec(cmd, nil, exec)
  end
  if type(result) == "table" and tonumber(result.exit_code) == 0 then
    cache_set(cache_key, tostring(now() + (ttl_seconds or 60)) .. "\n" .. tostring(result.stdout or ""))
  end
  return result
end

-- Readable cache key for an opt-in scan read: github-devloop/ghread/<variant>/<repo>/<number>.
function C.gh_read_cache_key(M, variant, repo, number)
  return "github-devloop/ghread/"
    .. sanitize_cache_segment(variant, false)
    .. "/"
    .. sanitize_cache_segment(repo, true)
    .. "/"
    .. sanitize_cache_segment(number, false)
end

return C
