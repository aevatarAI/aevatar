local base_ids = require("devloop.base_ids")
local contract_time = require("contract.time")
local strings = require("contract.strings")
local decimal_checksum = strings.decimal_checksum

local conflict_hotspot_threshold = 3
local conflict_hotspot_window_days = 7
local conflict_hotspot_window_seconds = conflict_hotspot_window_days * 24 * 60 * 60
local max_conflict_log_bytes = 200000
local max_conflict_evidence = 8
local C = {}

local function is_safe_conflict_path(path)
  local text = tostring(path or "")
  return text ~= ""
    and #text <= 240
    and text:find("^/") == nil
    and text:find("%.%.", 1, true) == nil
    and text:find("[%z\r\n\t%s]") == nil
    and text:find("^[%w%._%-%/]+$") ~= nil
end

function C.conflict_path_key(M, path)
  local key = strings.sanitize_key(tostring(path or ""), false):gsub("/", "-"):gsub("%-+", "-")
  if #key > 140 then
    local suffix = "-" .. decimal_checksum(key)
    key = base_ids.truncate_utf8(key, 140 - #suffix):gsub("%-+$", "") .. suffix
  end
  return key
end

local function current_conflict_timestamp()
  return os.date("!%Y-%m-%dT%H:%M:%SZ", now())
end

function C.conflict_file_paths_from_unmerged(M, stdout)
  local paths = {}
  local seen = {}
  for line in tostring(stdout or ""):gmatch("[^\r\n]+") do
    local path = line:match("\t(.+)$")
    if path ~= nil and is_safe_conflict_path(path) then
      if not seen[path] then
        seen[path] = true
        table.insert(paths, path)
      end
    elseif path ~= nil then
      M.log_line("warn", "fix", "unknown", "CONFLICT_FILE_SKIPPED", {
        "reason=unsafe-path",
        "path_key=" .. C.conflict_path_key(M, path),
      })
    end
  end
  table.sort(paths)
  return paths
end

function C.log_conflict_files(M, dept, proposal_id, pr_number, unmerged_stdout)
  local paths = C.conflict_file_paths_from_unmerged(M, unmerged_stdout)
  if #paths == 0 then
    M.log_line("info", dept or "fix", proposal_id, "CONFLICT_FILE", {
      "action=no-op",
      "reason=no-safe-conflict-files",
      "pr=" .. tostring(pr_number or ""),
    })
    return paths
  end
  for _, path in ipairs(paths) do
    M.log_line("info", dept or "fix", proposal_id, "CONFLICT_FILE", {
      "ts=" .. current_conflict_timestamp(),
      "conflict_file=" .. path,
      "pr=" .. tostring(pr_number or ""),
      "proposal_id=" .. tostring(proposal_id or "unknown"),
    })
  end
  return paths
end

local function parse_conflict_timestamp(text)
  local timestamp = tostring(text or ""):match("ts=(%d%d%d%d%-%d%d%-%d%dT%d%d[:%-]%d%d[:%-]%d%dZ)")
    or tostring(text or ""):match("(%d%d%d%d%-%d%d%-%d%dT%d%d:%d%d:%d%dZ)")
  local seconds = contract_time.iso_timestamp_epoch_seconds(timestamp)
  if seconds == nil then
    return nil, nil
  end
  return timestamp, seconds
end

local function parse_conflict_log_line(line)
  local text = tostring(line or "")
  if text:find("tag=CONFLICT_FILE", 1, true) == nil then
    return nil
  end
  local timestamp, timestamp_seconds = parse_conflict_timestamp(text)
  local file = text:match("conflict_file=([^%s]+)")
  local pr = text:match("pr=(%d+)")
  local proposal_id = text:match("proposal_id=([^%s]+)") or text:match("proposal=([^%s]+)")
  if timestamp_seconds == nil
    or file == nil
    or pr == nil
    or proposal_id == nil
    or not is_safe_conflict_path(file) then
    return nil
  end
  return {
    timestamp = timestamp,
    timestamp_seconds = timestamp_seconds,
    file = file,
    pr = tonumber(pr),
    proposal_id = proposal_id,
    line = text,
  }
end

function C.parse_conflict_file_facts(log_text)
  local facts = {}
  local text = tostring(log_text or "")
  if #text > max_conflict_log_bytes then
    text = text:sub(#text - max_conflict_log_bytes + 1)
  end
  for line in text:gmatch("[^\r\n]+") do
    local fact = parse_conflict_log_line(line)
    if fact ~= nil then
      table.insert(facts, fact)
    end
  end
  return facts
end

function C.conflict_hotspots(facts, threshold, now_seconds)
  local cutoff_seconds = (tonumber(now_seconds) or now()) - conflict_hotspot_window_seconds
  local by_file = {}
  for _, fact in ipairs(facts or {}) do
    if fact.timestamp_seconds ~= nil and fact.timestamp_seconds >= cutoff_seconds then
      local item = by_file[fact.file]
      if item == nil then
        item = {
          file = fact.file,
          prs = {},
          pr_seen = {},
          evidence = {},
        }
        by_file[fact.file] = item
      end
      if fact.pr ~= nil and not item.pr_seen[fact.pr] then
        item.pr_seen[fact.pr] = true
        table.insert(item.prs, fact.pr)
      end
      if #item.evidence < max_conflict_evidence then
        table.insert(item.evidence, fact)
      end
    end
  end
  local result = {}
  for _, item in pairs(by_file) do
    table.sort(item.prs)
    if #item.prs >= (threshold or conflict_hotspot_threshold) then
      table.insert(result, item)
    end
  end
  table.sort(result, function(a, b)
    if #a.prs ~= #b.prs then
      return #a.prs > #b.prs
    end
    return a.file < b.file
  end)
  return result
end

return C
