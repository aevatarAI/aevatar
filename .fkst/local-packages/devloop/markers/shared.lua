local base_ids = require("devloop.base_ids")
local S = {}

S.valid_round = require("devloop.rounds").valid_round
S.strings = require("contract.strings")
S.max_attr_len = 240

local intake_service_class_set = {
  expedite = true,
  standard = true,
  background = true,
}

function S.normalize_intake_service_class(value)
  local text = tostring(value or ""):lower()
  if intake_service_class_set[text] then
    return text
  end
  return "standard"
end

function S.is_intake_service_class(value)
  return intake_service_class_set[tostring(value or "")] == true
end

function S.marker_attr(marker, name)
  return marker:match(name .. '="([^"]*)"')
end

function S.safe_marker_attr(M, value, limit)
  local text = tostring(value or "")
  text = text:gsub("<!%-%- fkst:[^\n]*%-%->", " ")
  text = text:gsub("&lt;!%-%- fkst:[^\n]*%-%-&gt;", " ")
  text = text:gsub("%c", " "):gsub('"', "'"):gsub("[<>]", ""):gsub("%s+", " ")
  text = text:gsub("^%s+", ""):gsub("%s+$", "")
  local cap = limit or S.max_attr_len
  if #text > cap then
    text = base_ids.truncate_utf8(text, cap)
  end
  return text
end

function S.decode_marker_attr(value)
  if type(value) ~= "string" or value == "" then
    return nil
  end
  if value:find("%c") ~= nil or value:find("[<>]") ~= nil or value:find('"', 1, true) ~= nil then
    return nil
  end
  return value
end

return S
