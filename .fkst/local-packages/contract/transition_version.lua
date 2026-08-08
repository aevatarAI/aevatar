-- contract.transition_version: transition version normalization and ordering.
local V = {}
local strings = require("contract.strings")
local source_ref = require("contract.source_ref")

local max_version_key_len = 40
local decimal_checksum = strings.decimal_checksum
local order_number_width = 12

local slash_suffix_specs = {
  ["review-meta-action"] = { kind = "review_meta_action", numeric = true },
  ["review-loop"] = { kind = "review_loop", numeric = true },
  ["ready-split"] = { kind = "ready_split", numeric = true },
  ["reimplement"] = { kind = "reimplement", numeric = true },
  ["timeout-reconcile"] = { kind = "timeout_reconcile", state_numeric = true },
  ["timeout"] = { kind = "timeout", state_numeric = true },
  ["rereview"] = { kind = "rereview", n_hex = true },
  ["review-meta"] = { kind = "review_meta", numeric = true },
  ["review"] = { kind = "review", numeric = true },
  ["loop"] = { kind = "loop", numeric = true },
  ["fix"] = { kind = "fix", numeric = true },
}

local hyphen_suffixes = {
  { name = "review-meta-action", kind = "review_meta_action", parts = 1 },
  { name = "review-loop", kind = "review_loop", parts = 1 },
  { name = "ready-split", kind = "ready_split", parts = 1 },
  { name = "reimplement", kind = "reimplement", parts = 1 },
  { name = "timeout-reconcile", kind = "timeout_reconcile", state_numeric = true },
  { name = "timeout", kind = "timeout", state_numeric = true },
  { name = "rereview", kind = "rereview", parts = 2 },
  { name = "review-meta", kind = "review_meta", parts = 1 },
  { name = "review", kind = "review", parts = 1 },
  { name = "loop", kind = "loop", parts = 1 },
  { name = "fix", kind = "fix", parts = 1 },
}

local timeout_order_states = {
  "thinking",
  "ready",
  "implementing",
  "awaiting-pr",
  "impl-failed",
  "pr-open",
  "reviewing",
  "review-meta",
  "merge-ready",
  "merging",
  "fixing",
  "blocked",
}

local function padded_order_number(value)
  return string.format("%0" .. tostring(order_number_width) .. "d", tonumber(value) or 0)
end

local function sign_order(value)
  if value > 0 then
    return 1
  end
  if value < 0 then
    return -1
  end
  return 0
end

local function slash_numeric_suffix(parts, pos, spec)
  local raw = parts[pos + 1]
  if type(raw) ~= "string" or raw:match("^%d+$") == nil then
    return nil
  end
  local n = tonumber(raw)
  if n == nil then
    return nil
  end
  return { kind = spec.kind, n = n, separator = "slash" }, pos + 2
end

local function slash_state_numeric_suffix(parts, pos, spec)
  local state = parts[pos + 1]
  local raw = parts[pos + 2]
  if state == nil or state == "" or type(raw) ~= "string" or raw:match("^%d+$") == nil then
    return nil
  end
  local n = tonumber(raw)
  if n == nil then
    return nil
  end
  return { kind = spec.kind, state = state, n = n, separator = "slash" }, pos + 3
end

local function slash_n_hex_suffix(parts, pos, spec)
  local n = tonumber(parts[pos + 1])
  local hex = parts[pos + 2]
  if n == nil or hex == nil or hex:match("^[0-9A-Fa-f]+$") == nil then
    return nil
  end
  return { kind = spec.kind, n = n, hex = hex, separator = "slash" }, pos + 3
end

local function parse_slash_suffix_chain(parts, start_pos)
  local suffixes = {}
  local pos = start_pos
  while pos <= #parts do
    local spec = slash_suffix_specs[parts[pos]]
    if spec == nil then
      return nil
    end
    local suffix, next_pos
    if spec.numeric then
      suffix, next_pos = slash_numeric_suffix(parts, pos, spec)
    elseif spec.state_numeric then
      suffix, next_pos = slash_state_numeric_suffix(parts, pos, spec)
    elseif spec.n_hex then
      suffix, next_pos = slash_n_hex_suffix(parts, pos, spec)
    end
    if suffix == nil then
      return nil
    end
    table.insert(suffixes, suffix)
    pos = next_pos
  end
  return suffixes
end

local function split_slash(text)
  local parts = {}
  local start_pos = 1
  while true do
    local slash_pos = text:find("/", start_pos, true)
    if slash_pos == nil then
      table.insert(parts, text:sub(start_pos))
      break
    end
    table.insert(parts, text:sub(start_pos, slash_pos - 1))
    start_pos = slash_pos + 1
  end
  return parts
end

local function join_parts(parts, first_pos, last_pos, separator)
  local values = {}
  for i = first_pos, last_pos do
    table.insert(values, parts[i])
  end
  return table.concat(values, separator)
end

local function parse_slash_version(text)
  local parts = split_slash(text)
  for pos = 1, #parts do
    local suffixes = parse_slash_suffix_chain(parts, pos)
    if suffixes ~= nil then
      return {
        base = join_parts(parts, 1, pos - 1, "/"),
        suffixes = suffixes,
      }
    end
  end
  return {
    base = text,
    suffixes = {},
  }
end

local function parse_hyphen_once(base)
  for _, spec in ipairs(hyphen_suffixes) do
    local prefix = "-" .. spec.name .. "-"
    local prefix_pos = base:match(".*()" .. prefix:gsub("%-", "%%-"))
    if prefix_pos ~= nil then
      local suffix_text = base:sub(prefix_pos + #prefix)
      local suffix_parts = {}
      for part in suffix_text:gmatch("[^-]+") do
        table.insert(suffix_parts, part)
      end
      if spec.state_numeric and #suffix_parts >= 2 then
        local n = tonumber(suffix_parts[#suffix_parts])
        if n ~= nil then
          return base:sub(1, prefix_pos - 1), {
            kind = spec.kind,
            state = join_parts(suffix_parts, 1, #suffix_parts - 1, "-"),
            n = n,
            separator = "hyphen",
          }
        end
      elseif #suffix_parts == spec.parts then
        if spec.kind == "rereview" then
          local n = tonumber(suffix_parts[1])
          local hex = suffix_parts[2]
          if n ~= nil and hex:match("^[0-9A-Fa-f]+$") ~= nil then
            return base:sub(1, prefix_pos - 1), { kind = spec.kind, n = n, hex = hex, separator = "hyphen" }
          end
        else
          local n = tonumber(suffix_parts[#suffix_parts])
          if n ~= nil then
            return base:sub(1, prefix_pos - 1), { kind = spec.kind, n = n, separator = "hyphen" }
          end
        end
      end
    end
  end
  return nil, nil
end

local function parse_hyphen_suffixes(parsed)
  local suffixes = {}
  local base = parsed.base
  while true do
    local next_base, suffix = parse_hyphen_once(base)
    if suffix == nil then
      break
    end
    table.insert(suffixes, 1, suffix)
    base = next_base
  end
  parsed.base = base
  for _, suffix in ipairs(suffixes) do
    table.insert(parsed.suffixes, suffix)
  end
  return parsed
end

local function ensure_parsed(value)
  if type(value) == "table" and type(value.suffixes) == "table" then
    return value
  end
  return V.parse(value)
end

local function max_round(value, kind)
  local max_n = 0
  for _, suffix in ipairs(ensure_parsed(value).suffixes or {}) do
    if suffix.kind == kind then
      local parsed = tonumber(suffix.n) or 0
      if parsed > max_n then
        max_n = parsed
      end
    end
  end
  return max_n
end

local function max_timeout_round_from_parsed(parsed)
  local max_n = 0
  for _, state_name in ipairs(timeout_order_states) do
    local state_max = 0
    for _, suffix in ipairs(parsed.suffixes or {}) do
      if suffix.kind == "timeout" and tostring(suffix.state or "") == state_name then
        local parsed_n = tonumber(suffix.n) or 0
        if parsed_n > state_max then
          state_max = parsed_n
        end
      end
    end
    if state_max > max_n then
      max_n = state_max
    end
  end
  return max_n
end

local function comparable_transition_base_from_parsed(parsed)
  local text = tostring(parsed.base or "")
  return text:match("^consensus:(.+)$") or text
end

local function version_updated_at_text(version)
  local text = tostring(version or "")
  local updated_at = ""
  for found in text:gmatch("(%d%d%d%d%-%d%d%-%d%dT%d%d[%-:]%d%d[%-:]%d%dZ)") do
    updated_at = found:gsub(":", "-")
  end
  return updated_at
end

local function version_primary_key_from_parsed(parsed)
  if parsed == nil then
    return 0, ""
  end
  local base = comparable_transition_base_from_parsed(parsed)
  local updated_at = version_updated_at_text(base)
  if updated_at ~= "" then
    return 1, updated_at
  end
  return 0, source_ref.version_order_key(V.safe_version_segment(base))
end

local function version_sort_key_from_parsed(parsed, stage_rank)
  local primary_rank, primary = version_primary_key_from_parsed(parsed)
  return {
    primary_rank = primary_rank,
    primary = primary,
    loop_n = max_round(parsed, "loop"),
    fix_n = max_round(parsed, "fix"),
    reimplement_n = max_round(parsed, "reimplement"),
    timeout_n = max_timeout_round_from_parsed(parsed),
    review_loop_n = max_round(parsed, "review_loop"),
    review_meta_action_n = max_round(parsed, "review_meta_action"),
    ready_split_n = max_round(parsed, "ready_split"),
    stage_rank = tonumber(stage_rank) or 0,
  }
end

local function compare_version_keys(left, right)
  if left.primary_rank ~= right.primary_rank then
    return left.primary_rank > right.primary_rank and 1 or -1
  end
  if left.primary ~= right.primary then
    return left.primary > right.primary and 1 or -1
  end
  if left.loop_n ~= right.loop_n then
    return left.loop_n > right.loop_n and 1 or -1
  end
  if left.fix_n ~= right.fix_n then
    return left.fix_n > right.fix_n and 1 or -1
  end
  if left.reimplement_n ~= right.reimplement_n then
    return left.reimplement_n > right.reimplement_n and 1 or -1
  end
  if left.timeout_n ~= right.timeout_n then
    return left.timeout_n > right.timeout_n and 1 or -1
  end
  if left.review_meta_action_n ~= right.review_meta_action_n then
    return left.review_meta_action_n > right.review_meta_action_n and 1 or -1
  end
  if left.review_loop_n ~= right.review_loop_n then
    return left.review_loop_n > right.review_loop_n and 1 or -1
  end
  if left.ready_split_n ~= right.ready_split_n then
    return left.ready_split_n > right.ready_split_n and 1 or -1
  end
  if left.stage_rank ~= right.stage_rank then
    return left.stage_rank > right.stage_rank and 1 or -1
  end
  return 0
end

local function versions_equivalent(left, right)
  if left == nil or right == nil then
    return left == right
  end
  if tostring(left) == tostring(right) then
    return true
  end
  return V.safe_version_segment(left) == V.safe_version_segment(right)
end

local function compare_same_base_transition_versions(left_parsed, right_parsed)
  local left_key = version_sort_key_from_parsed(left_parsed, 0)
  local right_key = version_sort_key_from_parsed(right_parsed, 0)
  if left_key.loop_n ~= right_key.loop_n then
    return left_key.loop_n > right_key.loop_n and 1 or -1
  end
  if left_key.fix_n ~= right_key.fix_n then
    return left_key.fix_n > right_key.fix_n and 1 or -1
  end
  if left_key.reimplement_n ~= right_key.reimplement_n then
    return left_key.reimplement_n > right_key.reimplement_n and 1 or -1
  end
  if left_key.timeout_n ~= right_key.timeout_n then
    return left_key.timeout_n > right_key.timeout_n and 1 or -1
  end
  if left_key.review_meta_action_n ~= right_key.review_meta_action_n then
    return left_key.review_meta_action_n > right_key.review_meta_action_n and 1 or -1
  end
  if left_key.review_loop_n ~= right_key.review_loop_n then
    return left_key.review_loop_n > right_key.review_loop_n and 1 or -1
  end
  if left_key.ready_split_n ~= right_key.ready_split_n then
    return left_key.ready_split_n > right_key.ready_split_n and 1 or -1
  end
  return 0
end

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

function V.strip_timeout_suffixes(version)
  local parsed = V.parse(version)
  while #parsed.suffixes > 0 do
    local suffix = parsed.suffixes[#parsed.suffixes]
    if suffix.kind ~= "timeout" and suffix.kind ~= "timeout_reconcile" then
      break
    end
    table.remove(parsed.suffixes)
  end
  return V.render(parsed)
end

function V.strip_trailing_fix(version)
  local parsed = V.parse(version)
  if #parsed.suffixes > 0 and parsed.suffixes[#parsed.suffixes].kind == "fix" then
    table.remove(parsed.suffixes)
  end
  return V.render(parsed)
end

function V.strip_trailing_loop(version)
  local parsed = V.parse(version)
  if #parsed.suffixes > 0 and parsed.suffixes[#parsed.suffixes].kind == "loop" then
    table.remove(parsed.suffixes)
  end
  return V.render(parsed)
end

function V.strip_trailing_reimplement(version)
  local parsed = V.parse(version)
  if #parsed.suffixes > 0 and parsed.suffixes[#parsed.suffixes].kind == "reimplement" then
    table.remove(parsed.suffixes)
  end
  return V.render(parsed)
end

function V.trailing_reimplement_round(version)
  local suffixes = V.parse(version).suffixes or {}
  if #suffixes == 0 or suffixes[#suffixes].kind ~= "reimplement" then
    return 0
  end
  return tonumber(suffixes[#suffixes].n) or 0
end

function V.strip_before_review_loop(version)
  local parsed = V.parse(version)
  local keep = {}
  for _, suffix in ipairs(parsed.suffixes or {}) do
    if suffix.kind == "review_loop" then
      break
    end
    table.insert(keep, suffix)
  end
  parsed.suffixes = keep
  return V.render(parsed)
end

function V.parse(version)
  local parsed = parse_slash_version(tostring(version or ""))
  return parse_hyphen_suffixes(parsed)
end

function V.render(version)
  local parsed = ensure_parsed(version)
  local text = tostring(parsed.base or "")
  for _, suffix in ipairs(parsed.suffixes or {}) do
    local separator = suffix.separator or "slash"
    if separator == "hyphen" then
      if suffix.kind == "timeout" then
        text = text .. "-timeout-" .. tostring(suffix.state or "") .. "-" .. tostring(suffix.n or 0)
      elseif suffix.kind == "timeout_reconcile" then
        text = text .. "-timeout-reconcile-" .. tostring(suffix.state or "") .. "-" .. tostring(suffix.n or 0)
      elseif suffix.kind == "rereview" then
        text = text .. "-rereview-" .. tostring(suffix.n or 0) .. "-" .. tostring(suffix.hex or "")
      else
        text = text .. "-" .. tostring(suffix.kind or ""):gsub("_", "-") .. "-" .. tostring(suffix.n or 0)
      end
    else
      if suffix.kind == "timeout" then
        text = text .. "/timeout/" .. tostring(suffix.state or "") .. "/" .. tostring(suffix.n or 0)
      elseif suffix.kind == "timeout_reconcile" then
        text = text .. "/timeout-reconcile/" .. tostring(suffix.state or "") .. "/" .. tostring(suffix.n or 0)
      elseif suffix.kind == "rereview" then
        text = text .. "/rereview/" .. tostring(suffix.n or 0) .. "/" .. tostring(suffix.hex or "")
      else
        text = text .. "/" .. tostring(suffix.kind or ""):gsub("_", "-") .. "/" .. tostring(suffix.n or 0)
      end
    end
  end
  return text
end

function V.loop_round(version)
  return max_round(version, "loop")
end

function V.fix_round(version)
  return max_round(version, "fix")
end

function V.review_loop_round(version)
  return max_round(version, "review_loop")
end

function V.has_review_loop(version)
  for _, suffix in ipairs(ensure_parsed(version).suffixes or {}) do
    if suffix.kind == "review_loop" then
      return true
    end
  end
  return false
end

function V.review_meta_action_round(version)
  return max_round(version, "review_meta_action")
end

function V.ready_split_round(version)
  return max_round(version, "ready_split")
end

function V.reimplement_round(version)
  return max_round(version, "reimplement")
end

function V.timeout_round(version, state_name)
  local max_n = 0
  local state = tostring(state_name or "")
  if state == "" then
    return 0
  end
  for _, suffix in ipairs(ensure_parsed(version).suffixes or {}) do
    if suffix.kind == "timeout" and tostring(suffix.state or "") == state then
      local parsed = tonumber(suffix.n) or 0
      if parsed > max_n then
        max_n = parsed
      end
    end
  end
  return max_n
end

function V.max_timeout_round(version)
  local max_n = 0
  for _, state_name in ipairs(timeout_order_states) do
    local parsed = V.timeout_round(version, state_name)
    if parsed > max_n then
      max_n = parsed
    end
  end
  return max_n
end

function V.next_loop(version)
  local base = tostring(version or "")
  return base .. "/loop/" .. tostring(V.loop_round(base) + 1)
end

function V.loop_at(version, round)
  return tostring(version or "") .. "/loop/" .. tostring(round)
end

function V.next_fix(version)
  local base = tostring(version or "")
  return base .. "/fix/" .. tostring(V.fix_round(base) + 1)
end

function V.next_review_loop(version)
  local base = tostring(version or "")
  return base .. "/review-loop/" .. tostring(V.review_loop_round(base) + 1)
end

function V.review_loop_at(version, round)
  return tostring(version or "") .. "/review-loop/" .. tostring(round)
end

function V.rereview_at(version, round, head_sha)
  return V.review_loop_at(version, round) .. "/rereview/" .. tostring(round) .. "/" .. tostring(head_sha or "")
end

function V.next_rereview(version, head_sha)
  local base = tostring(version or "")
  local next_n = V.review_loop_round(base) + 1
  return V.rereview_at(base, next_n, head_sha)
end

function V.next_review_meta_action(version)
  local base = tostring(version or "")
  return base .. "/review-meta-action/" .. tostring(V.review_meta_action_round(base) + 1)
end

function V.next_ready_split(version)
  local base = V.strip_suffixes(version)
  return tostring(base) .. "/ready-split/" .. tostring(V.ready_split_round(version) + 1)
end

function V.next_reimplement(version)
  local base = tostring(version or "")
  return base .. "/reimplement/" .. tostring(V.reimplement_round(base) + 1)
end

function V.reimplement_at(version, round)
  return tostring(version or "") .. "/reimplement/" .. tostring(round)
end

function V.timeout_at(version, state, round)
  local base = tostring(version or "")
  local state_name = tostring(state or "")
  local escaped = state_name:gsub("%-", "%%-")
  local previous = nil
  while previous ~= base do
    previous = base
    base = base:gsub("/timeout/" .. escaped .. "/%d+$", "")
  end
  return base .. "/timeout/" .. state_name .. "/" .. tostring(round)
end

function V.next_timeout(version, state)
  local state_name = tostring(state or "")
  return V.timeout_at(version, state_name, V.timeout_round(version, state_name) + 1)
end

function V.version_order_key(version)
  return source_ref.version_order_key(version)
end

function V.updated_at(version)
  return version_updated_at_text(version)
end

function V.compare(left, right)
  if left == right then
    return 0
  end
  if left == nil then
    return right == nil and 0 or -1
  end
  if right == nil then
    return 1
  end

  local left_parsed = V.parse(left)
  local right_parsed = V.parse(right)
  local left_base = comparable_transition_base_from_parsed(left_parsed)
  local right_base = comparable_transition_base_from_parsed(right_parsed)
  if versions_equivalent(left_base, right_base) then
    return compare_same_base_transition_versions(left_parsed, right_parsed)
  end
  return sign_order(compare_version_keys(
    version_sort_key_from_parsed(left_parsed, 0),
    version_sort_key_from_parsed(right_parsed, 0)
  ))
end

function V.marker_order_key(version, stage_rank)
  local key = version_sort_key_from_parsed(V.parse(version), stage_rank)
  return table.concat({
    tostring(key.primary or ""),
    padded_order_number(key.loop_n),
    padded_order_number(key.fix_n),
    padded_order_number(key.reimplement_n),
    padded_order_number(key.timeout_n),
    padded_order_number(key.review_meta_action_n),
    padded_order_number(key.review_loop_n),
    padded_order_number(key.ready_split_n),
    padded_order_number(key.stage_rank),
  }, "/")
end

return V
