local base_ids = require("devloop.base_ids")
local strings = require("contract.strings")
local github_view = require("forge.github_view")

local C = {}
local json_string = github_view.json_string

local function normalize_poll_key(value)
  local text = tostring(value or "")
  if text ~= "" then
    return "poll-" .. strings.sanitize_key(text, 120):gsub("/", "-")
  end
  return nil
end

local function list_cache_key(M, repo, kind, scope, poll_key)
  local selected_kind = tostring(kind or "")
  if selected_kind ~= "issue" and selected_kind ~= "pr" then
    error("github-devloop: invalid entity list kind")
  end
  local normalized_poll_key = normalize_poll_key(poll_key)
  if normalized_poll_key == nil then
    return nil
  end
  return table.concat({
    "github-devloop",
    "entity-list",
    base_ids.safe_repo(repo),
    selected_kind,
    strings.sanitize_key(scope or "open", 80):gsub("/", "-"),
    normalized_poll_key,
  }, "/")
end

local function decode_cached_list(encoded)
  local ok, decoded = pcall(json.decode, encoded or "")
  if not ok or type(decoded) ~= "table" or decoded.stdout == nil then
    return nil
  end
  return {
    stdout = tostring(decoded.stdout),
    stderr = "",
    exit_code = 0,
  }
end

local function encode_cached_list(stdout)
  return '{"stdout":' .. json_string(stdout or "") .. "}"
end

local function fetch_shared_list(M, repo, kind, scope, poll_key, exec_spec)
  local key = list_cache_key(M, repo, kind, scope, poll_key)
  if key == nil then
    return exec_spec()
  end
  local cached = decode_cached_list(cache_get(key))
  if cached ~= nil then
    return cached
  end

  local result = exec_spec()
  if type(result) == "table" and result.exit_code == 0 then
    cache_set(key, encode_cached_list(result.stdout or ""))
  end
  return result
end

function C.entity_list_cache_key(M, repo, kind, scope, poll_key)
  return list_cache_key(M, repo, kind, scope, poll_key)
end

function C.entity_list_poll_key(M, event)
  if type(event) == "table" then
    if event.ts ~= nil then
      return tostring(event.ts)
    end
    local payload = event.payload
    if type(payload) == "table" then
      for _, key in ipairs({ "tick", "generated_at", "ts" }) do
        if payload[key] ~= nil then
          return tostring(payload[key])
        end
      end
    end
  end
  return nil
end

function C.fetch_shared_issue_observe_list(M, repo, opts)
  local options = opts or {}
  local exec_opts = M.gh_issue_list_observe_opts(repo)
  exec_opts.timeout = options.timeout or exec_opts.timeout
  return fetch_shared_list(M, repo, "issue", "open", options.poll_key, function()
    return exec_opts.run(exec_opts.timeout)
  end)
end

function C.fetch_shared_pr_observe_list(M, repo, opts)
  local options = opts or {}
  local exec_opts = M.gh_pr_list_observe_opts(repo)
  exec_opts.timeout = options.timeout or exec_opts.timeout
  return fetch_shared_list(M, repo, "pr", "open", options.poll_key, function()
    return exec_opts.run(exec_opts.timeout)
  end)
end

return C
