local M = {}
local strings = require("forge.strings")

function M.url_encode(value)
  return (tostring(value or ""):gsub("([^%w%-%._~])", function(char)
    return string.format("%%%02X", string.byte(char))
  end))
end

M.is_git_ref_safe = strings.is_git_ref_safe

return M
