local M = {}

local max_round = 100000

function M.valid_round(value)
  local n = tonumber(value)
  if n == nil or n < 0 or n ~= math.floor(n) or n > max_round then
    return nil
  end
  return n
end

return M
