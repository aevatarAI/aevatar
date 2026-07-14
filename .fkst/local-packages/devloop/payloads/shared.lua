local base_ids = require("devloop.base_ids")
local C = {}
local github_view = require("forge.github_view")
local github_handle = nil

function C.github(_M)
  if github_handle ~= nil then
    return github_handle
  end
  if type(exec_argv) ~= "function" then
    error("github-devloop: GitHub adapter requires exec_argv")
  end
  github_handle = require("forge.github").new(exec_argv)
  return github_handle
end

function C.label_names(_M, labels)
  return github_view.label_names(labels)
end

function C.bounded_framing(M, framing)
  if framing == nil then
    return nil
  end
  local value = tostring(framing)
  if #value > M._max_framing_len then
    value = base_ids.truncate_utf8(value, M._max_framing_len)
  end
  return value
end

function C.bounded_control_text(M, value, limit)
  if value == nil then
    return nil
  end
  local text = tostring(value):gsub("%c", " "):gsub("%s+", " ")
  text = text:gsub("^%s+", ""):gsub("%s+$", "")
  if text == "" then
    return nil
  end
  local cap = limit or M._max_blocking_gap_len
  if #text > cap then
    text = base_ids.truncate_utf8(text, cap)
  end
  return text
end

return C
