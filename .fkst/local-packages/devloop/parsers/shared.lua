local strings = require("contract.strings")
local github_view = require("forge.github_view")
local C = {}

function C.label_names(_M, labels)
  return github_view.label_names(labels)
end

function C.each_paginated_item(_M, decoded, callback)
  if type(decoded) ~= "table" then
    return
  end
  for _, value in ipairs(decoded) do
    if type(value) == "table" then
      if value[1] ~= nil then
        for _, item in ipairs(value) do
          callback(item)
        end
      elseif next(value) ~= nil then
        callback(value)
      end
    end
  end
end

function C.parse_numbered_list(M, stdout)
  local decoded = json.decode(stdout or "[]")
  local items = {}
  C.each_paginated_item(M, decoded, function(item)
    if type(item) == "table" and tonumber(item.number) ~= nil then
      table.insert(items, {
        number = tonumber(item.number),
        state = item.state,
        updated_at = item.updated_at or item.updatedAt,
      })
    end
  end)
  return items
end

C.strings = strings
C.github_view = github_view

return C
