local S = {}
local github_view = require("forge.github_view")

function S.install(M)
local parse_view_updated_at = github_view.parse_view_updated_at
local parse_updated_at_stdout = github_view.parse_updated_at_stdout
local json_string = github_view.json_string

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

local function entity_view_storage_base_key(repo, kind, number, updated_at)
  return "github-proxy/view/"
    .. sanitize_cache_segment(repo, true)
    .. "/"
    .. sanitize_cache_segment(kind, false)
    .. "/"
    .. sanitize_cache_segment(number, false)
    .. "/"
    .. sanitize_cache_segment(updated_at, false)
end

local function entity_view_generation_key(repo, kind, number)
  return "github-proxy/view-generation/"
    .. sanitize_cache_segment(repo, true)
    .. "/"
    .. sanitize_cache_segment(kind, false)
    .. "/"
    .. sanitize_cache_segment(number, false)
end

local function entity_view_storage_cache_key(repo, kind, number, updated_at)
  local base_key = entity_view_storage_base_key(repo, kind, number, updated_at)
  local generation = cache_get(entity_view_generation_key(repo, kind, number))
  if generation == nil or generation == "" then
    return base_key
  end
  return base_key .. "-g-" .. sanitize_cache_segment(generation, false)
end

local function entity_view_cmd(repo, kind, number)
  if kind == "pr" then
    return M.gh_pr_rest_view_cmd(repo, number)
  end
  return M.gh_issue_rest_view_cmd(repo, number)
end

local function fetch_entity_updated_at(repo, kind, number)
  return M.github().entity_updated_at(repo, kind, number, 30)
end

local function decode_cached_view(encoded)
  local ok, decoded = pcall(json.decode, encoded or "")
  if not ok or type(decoded) ~= "table" then
    return nil
  end
  if type(decoded.stdout) ~= "string" then
    return nil
  end
  return decoded
end

local function encode_cached_view(stdout, producer)
  return '{"producer":' .. json_string(producer)
    .. ',"stdout":' .. json_string(stdout or "")
    .. "}"
end

local function fetch_entity_view(repo, kind, number, updated_at, opts)
  local selected_kind = tostring(kind or "")
  if selected_kind ~= "issue" and selected_kind ~= "pr" then
    error("github-proxy: invalid entity view kind")
  end
  local options = opts or {}
  local freshness = tostring(updated_at or "")
  local consumer = tostring(options.consumer or "")
  if options.fresh == true or options.marker_bearing == true or freshness == "" then
    if selected_kind == "pr" then
      return M.fetch_rest_pr_view(repo, number)
    end
    return M.fetch_rest_issue_view(repo, number)
  end

  local key = entity_view_storage_cache_key(repo, selected_kind, number, freshness)
  local cached = decode_cached_view(cache_get(key))
  if cached ~= nil and cached.producer ~= consumer then
    local current = M.gh_exec(function()
      return fetch_entity_updated_at(repo, selected_kind, number)
    end, 30, "GitHub entity updated_at")
    if parse_view_updated_at(cached.stdout) == freshness and parse_updated_at_stdout(current.stdout) == freshness then
      cache_set(key, "")
      return {
        stdout = cached.stdout,
        stderr = "",
        exit_code = 0,
      }
    end
    cache_set(key, "")
  end
  local result = selected_kind == "pr" and M.fetch_rest_pr_view(repo, number) or M.fetch_rest_issue_view(repo, number)
  if type(result) == "table" and result.exit_code == 0 and parse_view_updated_at(result.stdout) == freshness then
    cache_set(key, encode_cached_view(result.stdout or "", consumer))
  end
  return result
end

function M.entity_view_generation_key(repo, kind, number)
  return entity_view_generation_key(repo, kind, number)
end

function M.invalidate_entity_after_write(repo, kind, number)
  local selected_kind = tostring(kind or "")
  if selected_kind ~= "issue" and selected_kind ~= "pr" then
    error("github-proxy: invalid post-write invalidation kind")
  end
  local entity_key = M.entity_cache_key(repo, selected_kind, number)
  local generation_key = entity_view_generation_key(repo, selected_kind, number)
  with_lock(entity_key, function()
    cache_set(entity_key, "")
    local next_generation = (tonumber(cache_get(generation_key) or "0") or 0) + 1
    cache_set(generation_key, tostring(next_generation))
  end)
end

function M.gh_issue_view_entity_cmd(repo, issue_number)
  return entity_view_cmd(repo, "issue", issue_number)
end

function M.gh_pr_view_entity_cmd(repo, pr_number)
  return entity_view_cmd(repo, "pr", pr_number)
end

function M.gh_entity_updated_at_cmd(repo, kind, number)
  local selected_kind = tostring(kind or "")
  if selected_kind ~= "issue" and selected_kind ~= "pr" then
    error("github-proxy: invalid entity updatedAt kind")
  end
  return function()
    return fetch_entity_updated_at(repo, selected_kind, number)
  end
end

function M.fetch_entity_view(repo, kind, number, updated_at, opts)
  return fetch_entity_view(repo, kind, number, updated_at, opts)
end

function M.fetch_issue_view(repo, issue_number, updated_at, opts)
  return fetch_entity_view(repo, "issue", issue_number, updated_at, opts)
end

function M.fetch_pr_view(repo, pr_number, updated_at, opts)
  return fetch_entity_view(repo, "pr", pr_number, updated_at, opts)
end

function M.fetch_marker_issue_view(repo, issue_number, updated_at, opts)
  local options = opts or {}
  options.consumer = options.consumer or "marker-reader"
  options.marker_bearing = true
  return M.fetch_issue_view(repo, issue_number, updated_at, options)
end

function M.fetch_marker_pr_view(repo, pr_number, updated_at, opts)
  local options = opts or {}
  options.consumer = options.consumer or "marker-reader"
  options.marker_bearing = true
  return M.fetch_pr_view(repo, pr_number, updated_at, options)
end

end

return S
