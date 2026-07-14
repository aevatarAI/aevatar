-- contract.transition_version: transition version normalization (value-core;
-- depends only on contract.strings for key sanitization + checksum, the same
-- intra-contract value dependency contract.source_ref already uses).
local V = {}
local strings = require("contract.strings")

local max_version_key_len = 40
local decimal_checksum = strings.decimal_checksum

function V.safe_version_segment(version)
  local safe = strings.sanitize_key(version, false):gsub("[/#]", "-"):gsub("%-+", "-")
  safe = safe:gsub("^%-+", ""):gsub("%-+$", "")
  if safe == "" then
    safe = "version"
  end
  if #safe > max_version_key_len then
    local suffix = "-" .. decimal_checksum(version)
    safe = safe:sub(1, max_version_key_len - #suffix):gsub("%-+$", "") .. suffix
  end
  if safe == "" then
    return "version"
  end
  return safe
end

function V.strip_suffixes(version)
  local text = tostring(version or "")
  local previous = nil
  while previous ~= text do
    previous = text
    text = text
      :gsub("/rereview/%d+/[0-9A-Fa-f]+$", "")
      :gsub("%-rereview%-%d+%-[0-9A-Fa-f]+$", "")
      :gsub("/review%-meta/%d+$", "")
      :gsub("%-review%-meta%-%d+$", "")
      :gsub("/review%-meta%-action/%d+$", "")
      :gsub("%-review%-meta%-action%-%d+$", "")
      :gsub("/review%-loop/%d+$", "")
      :gsub("%-review%-loop%-%d+$", "")
      :gsub("/review/%d+$", "")
      :gsub("%-review%-%d+$", "")
      :gsub("/fix/%d+$", "")
      :gsub("%-fix%-%d+$", "")
      :gsub("/timeout%-reconcile/[%w%-]+/%d+$", "")
      :gsub("%-timeout%-reconcile%-[%w%-]+%-%d+$", "")
      :gsub("/timeout/[%w%-]+/%d+$", "")
      :gsub("%-timeout%-[%w%-]+%-%d+$", "")
      :gsub("/reimplement/%d+$", "")
      :gsub("%-reimplement%-%d+$", "")
      :gsub("/ready%-split/%d+$", "")
      :gsub("%-ready%-split%-%d+$", "")
      :gsub("/loop/%d+$", "")
      :gsub("%-loop%-%d+$", "")
  end
  return text
end

return V
