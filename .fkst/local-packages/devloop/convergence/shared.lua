local source_refs = require("contract.source_ref")
local strings = require("contract.strings")
local decimal_checksum = strings.decimal_checksum
local valid_round = require("devloop.rounds").valid_round

local max_digest_len = 64
local max_attr_len = 240
local max_question_len = 2000
local findings_component_len = 700
local findings_record_len = 1500

local function normalize_text(value)
  return tostring(value or ""):gsub("%s+", " "):gsub("^%s+", ""):gsub("%s+$", "")
end

local function digest(M, prefix, value)
  local text = tostring(value or "")
  return tostring(prefix) .. "-" .. #text .. "-" .. decimal_checksum(text)
end

local function safe_attr(value, limit)
  local text = tostring(value or ""):gsub("%c", " "):gsub('"', "'"):gsub("[<>]", ""):gsub("%s+", " ")
  text = text:gsub("^%s+", ""):gsub("%s+$", "")
  local cap = limit or max_attr_len
  if #text > cap then
    text = text:sub(1, cap)
  end
  return text
end

local function decode_attr(value)
  if type(value) ~= "string" or value == "" then
    return nil
  end
  if value:find("%c") ~= nil or value:find("[<>]") ~= nil or value:find('"', 1, true) ~= nil then
    return nil
  end
  return value
end

local sorted_angle_items

local function encode_component(value)
  return tostring(value or "")
    :gsub("%%", "%%25")
    :gsub("|", "%%7C")
    :gsub(";", "%%3B")
end

local function decode_component(value)
  return tostring(value or "")
    :gsub("%%3[Bb]", ";")
    :gsub("%%7[Cc]", "|")
    :gsub("%%25", "%%")
end

local function encode_angle_replay(angle_digests)
  local parts = {}
  for _, item in ipairs(sorted_angle_items(angle_digests)) do
    table.insert(parts, encode_component(item.angle) .. "|" .. encode_component(item.verdict) .. "|" .. encode_component(item.digest))
  end
  return safe_attr(table.concat(parts, ";"), 1000)
end

local function decode_angle_replay(value)
  local text = decode_attr(value)
  if text == nil then
    return nil
  end
  local items = {}
  for part in text:gmatch("[^;]+") do
    local angle, verdict, item_digest = part:match("^([^|]+)|([^|]+)|(.*)$")
    if angle == nil or verdict == nil or item_digest == nil then
      return nil
    end
    table.insert(items, {
      angle = decode_component(angle),
      verdict = decode_component(verdict),
      digest = decode_component(item_digest),
    })
  end
  if #items == 0 then
    return nil
  end
  return items
end

local function normalize_findings_line(value)
  if value == nil then
    return nil
  end
  local text = tostring(value):gsub("%c", " "):gsub("%s+", " ")
  text = text:gsub("^%s+", ""):gsub("%s+$", "")
  if text == "" then
    return nil
  end
  if #text > findings_component_len then
    return nil
  end
  return text
end

local function normalize_findings_text(value)
  if value == nil then
    return nil
  end
  local lines = {}
  local text = tostring(value):gsub("\r\n", "\n"):gsub("\r", "\n")
  for line in (text .. "\n"):gmatch("([^\n]*)\n") do
    local normalized = normalize_findings_line(line)
    if normalized ~= nil then
      local label, rest = normalized:match("^([%a_]+):%s+(.+)$")
      if (label == "settled" or label == "open") and rest ~= "" then
        table.insert(lines, label .. ":")
        table.insert(lines, rest)
      else
        table.insert(lines, normalized)
      end
    end
  end
  if #lines == 0 then
    return nil
  end
  local normalized = table.concat(lines, "\n")
  if #normalized > findings_record_len then
    return nil
  end
  return normalized
end

local function normalize_findings_record(record)
  if record == nil then
    return nil
  end
  if type(record) ~= "table" then
    return normalize_findings_text(record)
  end

  local lines = {}
  local settled = normalize_findings_line(record.settled)
  local open = normalize_findings_line(record.open)
  if settled ~= nil then
    table.insert(lines, "settled:")
    table.insert(lines, settled)
  end
  if open ~= nil then
    table.insert(lines, "open:")
    table.insert(lines, open)
  end
  local text = table.concat(lines, "\n")
  if text == "" then
    return nil
  end
  if #text > findings_record_len then
    return nil
  end
  return text
end

local function encode_findings_record(record)
  local text = normalize_findings_record(record)
  if text == nil then
    return ""
  end
  return text:gsub("%%", "%%25"):gsub('"', "'"):gsub("[<>]", ""):gsub("\n", "%%0A")
end

local function decode_findings_record(value)
  local text = decode_attr(value)
  if text == nil then
    return nil
  end
  return normalize_findings_text(text:gsub("%%0[Aa]", "\n"):gsub("%%25", "%%"))
end

function sorted_angle_items(angle_digests)
  local items = {}
  if type(angle_digests) ~= "table" then
    return items
  end
  for _, item in ipairs(angle_digests) do
    if type(item) == "table" then
      table.insert(items, {
        angle = safe_attr(item.angle or "unknown", max_attr_len),
        verdict = safe_attr(item.verdict or "invalid", max_attr_len),
        digest = safe_attr(item.digest or "", max_attr_len),
      })
    end
  end
  table.sort(items, function(a, b)
    if a.angle == b.angle then
      return a.verdict .. ":" .. a.digest < b.verdict .. ":" .. b.digest
    end
    return a.angle < b.angle
  end)
  return items
end

local function attr(marker, name)
  return marker:match(name .. '="([^"]*)"')
end

local function is_digest(value)
  return type(value) == "string" and value ~= "" and #value <= max_digest_len and value:find("%c") == nil
end

local function is_bounded_attr(M, value, limit)
  return strings.is_bounded_string(value, limit or max_attr_len) and value:find("%c") == nil
end

local Shared = {
  source_refs = source_refs,
  strings = strings,
  decimal_checksum = decimal_checksum,
  valid_round = valid_round,
  max_digest_len = max_digest_len,
  max_attr_len = max_attr_len,
  max_question_len = max_question_len,
  findings_record_len = findings_record_len,
  normalize_text = normalize_text,
  normalize_findings_record = normalize_findings_record,
  digest = digest,
  safe_attr = safe_attr,
  decode_attr = decode_attr,
  encode_component = encode_component,
  decode_component = decode_component,
  encode_angle_replay = encode_angle_replay,
  decode_angle_replay = decode_angle_replay,
  encode_findings_record = encode_findings_record,
  decode_findings_record = decode_findings_record,
  sorted_angle_items = sorted_angle_items,
  attr = attr,
  is_digest = is_digest,
  is_bounded_attr = is_bounded_attr,
}

function Shared.source_ref_digest(source_ref)
  if type(source_ref) ~= "table" then
    return digest(nil, "sr", "")
  end
  return digest(nil, "sr", tostring(source_ref.kind or "") .. "\n" .. tostring(source_ref.ref or ""))
end

function Shared.converge_question_digest(narrowed_question)
  local normalized = normalize_text(narrowed_question)
  if #normalized > max_question_len then
    normalized = normalized:sub(1, max_question_len)
  end
  return digest(nil, "q", normalized)
end

function Shared.converge_verdicts_digest(angle_digests)
  local parts = {}
  for _, item in ipairs(sorted_angle_items(angle_digests)) do
    table.insert(parts, item.angle .. "=" .. item.verdict)
  end
  return digest(nil, "v", table.concat(parts, "\n"))
end

function Shared.converge_angles_digest(angle_digests)
  local parts = {}
  for _, item in ipairs(sorted_angle_items(angle_digests)) do
    table.insert(parts, item.angle .. "=" .. item.verdict .. ":" .. item.digest)
  end
  return digest(nil, "a", table.concat(parts, "\n"))
end

return Shared
