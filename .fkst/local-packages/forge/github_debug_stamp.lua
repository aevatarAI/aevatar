local S = {}
local strings = require("contract.strings")

local marker_prefix = "<!-- fkst:debug-stamp:v1"
local max_attr_len = 120

local function checksum(value)
  local hash = 2166136261
  local text = tostring(value or "")
  for index = 1, #text do
    hash = (hash * 16777619 + text:byte(index)) % 4294967291
  end
  return string.format("%010d", hash)
end

local function attr_value(value)
  local text = tostring(value or "")
  text = text:gsub("[\r\n\t]", " ")
  text = text:gsub('"', "'")
  text = text:gsub("[<>]", "_")
  text = text:gsub("%s+", " ")
  text = strings.trim(text)
  if #text > max_attr_len then
    text = text:sub(1, max_attr_len)
  end
  if text == "" then
    return "unknown"
  end
  return text
end

local function read_debug_flag(read_env)
  if type(read_env) ~= "function" then
    return false
  end
  local ok, value = pcall(read_env, "FKST_DEBUG_STAMP")
  return ok and strings.trim(value) == "1"
end

local function read_code_version(git)
  if type(git) ~= "table" or type(git.rev_parse_verify_head) ~= "function" then
    return "unknown"
  end
  local ok, result = pcall(git.rev_parse_verify_head, 30)
  if not ok or type(result) ~= "table" or result.exit_code ~= 0 then
    return "unknown"
  end
  local head = strings.trim(result.stdout)
  if head:find("^[0-9A-Fa-f]+$") == nil then
    return "unknown"
  end
  if #head > 64 then
    head = head:sub(1, 64)
  end
  return head:lower()
end

function S.marker_prefix()
  return marker_prefix
end

function S.stamp(context, opts)
  local selected = opts or {}
  if not read_debug_flag(selected.read_env) then
    return nil
  end
  local fields = {
    marker_prefix,
    ' emitter="' .. attr_value(context and context.emitter) .. '"',
    ' target="' .. attr_value(context and context.target) .. '"',
    ' code_version="' .. attr_value(read_code_version(selected.git)) .. '"',
  }
  if context ~= nil and context.dedup_key ~= nil and tostring(context.dedup_key) ~= "" then
    table.insert(fields, ' dedup_hash="' .. checksum(context.dedup_key) .. '"')
  end
  if context ~= nil and context.context ~= nil and tostring(context.context) ~= "" then
    table.insert(fields, ' context_hash="' .. checksum(context.context) .. '"')
  end
  table.insert(fields, " -->")
  return table.concat(fields)
end

function S.append(body, context, opts)
  local text = tostring(body or "")
  if text:find(marker_prefix, 1, true) ~= nil then
    return text
  end
  local marker = S.stamp(context, opts)
  if marker == nil then
    return text
  end
  return text:gsub("%s+$", "") .. "\n\n" .. marker .. "\n"
end

local function production_git()
  if type(exec_argv) ~= "function" then
    return nil
  end
  return require("forge.git").new(exec_argv)
end

function S.install(M, read_env, git)
  local function debug_stamp_marker_prefix()
    return marker_prefix
  end

  local function with_github_debug_stamp(body, context)
    return S.append(body, context, {
      read_env = read_env or M.read_env,
      git = git or production_git(),
    })
  end
  rawset(M, "debug_stamp_marker_prefix", debug_stamp_marker_prefix)
  rawset(M, "with_github_debug_stamp", with_github_debug_stamp)
  return {
    debug_stamp_marker_prefix = debug_stamp_marker_prefix,
    with_github_debug_stamp = with_github_debug_stamp,
  }
end

return S
