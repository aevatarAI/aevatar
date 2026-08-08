-- forge.gitref: git ref/sha/PR-number safety predicates and validators.
local strings = require("contract.strings")
local forge_strings = require("forge.strings")

local S = {}

local max_sha_len = 64

S.is_git_ref_safe = forge_strings.is_git_ref_safe

function S.is_git_sha(value)
  return strings.is_bounded_string(value, max_sha_len) and tostring(value):find("^%x+$") ~= nil
end

function S.is_positive_pr_number(value)
  local number = tonumber(value)
  return number ~= nil and number >= 1 and number % 1 == 0 and number <= 2147483647
end

local function error_owner(owner)
  return tostring(owner or "gitref")
end

function S.require_safe_branch(name, value, owner)
  if not S.is_git_ref_safe(value) then
    error(error_owner(owner) .. ": invalid " .. tostring(name))
  end
  return tostring(value)
end

function S.require_safe_remote(value, owner)
  local remote = tostring(value or "")
  if remote == "" or remote:find("[\r\n]") ~= nil then
    error(error_owner(owner) .. ": invalid git remote")
  end
  if not S.is_git_ref_safe(remote) then
    error(error_owner(owner) .. ": invalid git remote")
  end
  return remote
end

function S.require_safe_sha(name, value, owner)
  if not S.is_git_sha(value) then
    error(error_owner(owner) .. ": invalid " .. tostring(name))
  end
  return tostring(value)
end

function S.require_positive_pr_number(value, owner)
  if not S.is_positive_pr_number(value) then
    error(error_owner(owner) .. ": invalid pull request number")
  end
  return tostring(value)
end

return S
