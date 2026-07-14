local M = {}

function M.shell_single_quote(value)
  return "'" .. tostring(value):gsub("'", "'\\''") .. "'"
end

function M.render(values)
  local parts = {}
  for _, value in ipairs(values or {}) do
    table.insert(parts, M.shell_single_quote(value))
  end
  return table.concat(parts, " ")
end

return M
