-- contract.strings: small, dependency-free string utilities shared across packages.
local S = {}

function S.trim(value)
  return (tostring(value or ""):gsub("^%s+", ""):gsub("%s+$", ""))
end

-- contract.strings.json_string is a temporary byte-identical stopgap for #976 only:
-- canonical JSON encoding remains deferred to a dedicated encoder boundary.
-- Keep this body matched to the folded github-devloop encode_json_string copies;
-- do not extend it into a partial general JSON serializer.
function S.json_string(value)
  local text = tostring(value or "")
  text = text:gsub("\\", "\\\\")
  text = text:gsub('"', '\\"')
  text = text:gsub("\b", "\\b")
  text = text:gsub("\f", "\\f")
  text = text:gsub("\n", "\\n")
  text = text:gsub("\r", "\\r")
  text = text:gsub("\t", "\\t")
  text = text:gsub("[%z\1-\31]", function(char)
    return string.format("\\u%04x", char:byte())
  end)
  return '"' .. text .. '"'
end

function S.is_bounded_string(value, limit)
  return type(value) == "string" and value ~= "" and #value <= limit
end

function S.decimal_checksum(value)
  local hash = 2166136261
  local text = tostring(value or "")
  for i = 1, #text do
    hash = (hash * 16777619 + text:byte(i)) % 4294967291
  end
  return string.format("%010d", hash)
end

function S.is_path_safe_key(value, limit)
  if not S.is_bounded_string(value, limit) then
    return false
  end
  if value:sub(1, 1) == "/" then
    return false
  end
  if value:find("\\", 1, true) ~= nil then
    return false
  end
  if value:find("%s") ~= nil then
    return false
  end
  if value:find("[^%w%._%-%/#]") ~= nil then
    return false
  end
  for segment in value:gmatch("[^/]+") do
    if segment == "." or segment == ".." then
      return false
    end
  end
  return true
end

function S.sanitize_key(value, limit)
  local max_len = limit
  local sanitized = tostring(value or ""):gsub("[^%w%._%-%/#]", "-")
  sanitized = sanitized:gsub("/+", "/")
  sanitized = sanitized:gsub("^/+", ""):gsub("/+$", "")
  if sanitized == "" then
    return "empty"
  end

  local segments = {}
  for segment in sanitized:gmatch("[^/]+") do
    local safe_segment = segment
    if safe_segment == "." or safe_segment == ".." then
      safe_segment = "-"
    end
    table.insert(segments, safe_segment)
  end

  sanitized = table.concat(segments, "/")
  if max_len ~= false and max_len ~= nil and #sanitized > max_len then
    sanitized = sanitized:sub(1, max_len)
    sanitized = sanitized:gsub("/+$", "")
  end
  if sanitized == "" then
    return "empty"
  end
  return sanitized
end

function S.runtime_safe_segment(value)
  local safe = tostring(value or ""):gsub("[^%w._-]", "_")
  safe = safe:gsub("_+", "_"):gsub("^_+", ""):gsub("_+$", "")
  if safe == "" then
    return "empty"
  end
  return safe
end

return S
