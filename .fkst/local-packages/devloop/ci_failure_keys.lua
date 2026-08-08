local forge_validators = require("devloop.forge_validators")
local strings = require("contract.strings")

local C = {}

function C.is_valid(value, limit)
  if not strings.is_bounded_string(value, limit) then
    return false
  end
  if value:find("[%c%s\"<>]") ~= nil then
    return false
  end
  local head, checks = value:match("^head:([^/]+)/checks:(.+)$")
  if head == nil or not forge_validators.is_git_sha(head) then
    return false
  end
  return checks:match("^digest%-%d%d%d%d%d%d%d%d%d%d$") ~= nil
end

return C
