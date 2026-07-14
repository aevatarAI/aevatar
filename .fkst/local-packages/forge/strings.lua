-- forge.strings: GitHub/Git-shaped string helpers for forge adapters and callers.
local contract_strings = require("contract.strings")

local S = {}

function S.strip_bot_login_suffix(login)
  if login == nil then
    return nil
  end
  return (tostring(login):gsub("%[bot%]$", ""))
end

function S.split_repo(repo)
  local owner, name = tostring(repo or ""):match("^([^/]+)/([^/]+)$")
  if owner == nil or owner == "" or name == nil or name == "" then
    return nil, nil
  end
  return owner, name
end

function S.comment_body(comment)
  if type(comment) == "table" then
    return tostring(comment.body or "")
  end
  return tostring(comment or "")
end

function S.is_git_ref_safe(value)
  local max_branch_len = 160
  if not contract_strings.is_bounded_string(value, max_branch_len) then
    return false
  end
  local text = tostring(value)
  if text:sub(1, 1) == "-" or text:sub(1, 1) == "/" then
    return false
  end
  if text:find("%.%.", 1, true) ~= nil
    or text:find("//", 1, true) ~= nil
    or text:find("@{", 1, true) ~= nil
    or text:sub(-1) == "/"
    or text:sub(-1) == "."
    or text:sub(-5) == ".lock" then
    return false
  end
  if text:find("[%s~^:?%[%]\\*]") ~= nil then
    return false
  end
  for segment in text:gmatch("[^/]+") do
    if segment == "." or segment == ".." or segment:sub(1, 1) == "." then
      return false
    end
  end
  return text:find("^[%w%._%-%/]+$") ~= nil
end

return S
