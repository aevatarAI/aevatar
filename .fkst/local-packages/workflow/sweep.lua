-- contract.sweep: pure leaf utilities (bounds, rotation offset, cursor batching,
-- deferred-result shapes) shared across packages. Only genuine leaves live here:
-- functions whose original package bodies contained no late-bound `M.*`
-- call into another facade function. The `rotate`/`batch` orchestrators stay in
-- the package facade so their `M.sweep_rotate -> M.sweep_rotation_offset` and
-- `M.sweep_batch -> M.sweep_rotate` late-binding remains byte-for-byte observable.
local S = {}
local strings = require("contract.strings")
local decimal_checksum = strings.decimal_checksum

function S.positive_integer(value, fallback, minimum, maximum)
  local n = tonumber(value)
  if n == nil or n ~= math.floor(n) or n < minimum or n > maximum then
    return fallback
  end
  return n
end

function S.rotation_offset(count, seed)
  local n = tonumber(count)
  if n == nil or n <= 0 then
    return 0
  end
  local numeric_seed = tonumber(seed)
  if numeric_seed ~= nil and numeric_seed == math.floor(numeric_seed) then
    return numeric_seed % n
  end
  local hash = decimal_checksum(tostring(seed or ""))
  return tonumber(hash) % n
end

function S.cursor_batch(items, cursor, cap, default_cap)
  local source = items or {}
  local count = #source
  local bounded_cap = S.positive_integer(cap, default_cap or 25, 1, 1000)
  if count <= bounded_cap then
    local all_items = {}
    for _, item in ipairs(source) do
      table.insert(all_items, item)
    end
    return all_items, 0, 0
  end

  local start = tonumber(cursor) or 0
  if start < 0 or start ~= math.floor(start) then
    start = 0
  end
  start = start % count

  local selected = {}
  for i = 1, bounded_cap do
    local index = ((start + i - 1) % count) + 1
    table.insert(selected, source[index])
  end

  local next_cursor = (start + #selected) % count
  return selected, math.max(0, count - #selected), next_cursor
end

function S.cursor_advance(cursor, total, processed)
  local count = tonumber(total) or 0
  if count <= 0 or count ~= math.floor(count) then
    return 0
  end
  local start = tonumber(cursor) or 0
  if start < 0 or start ~= math.floor(start) then
    start = 0
  end
  local step = tonumber(processed) or 0
  if step < 0 or step ~= math.floor(step) then
    step = 0
  end
  return (start + step) % count
end

function S.deadline_deferred_result(error_class, stderr)
  return {
    deferred = true,
    reason = "deadline",
    error_class = tostring(error_class or "sweep command"),
    stdout = "",
    stderr = tostring(stderr or "sweep deadline exhausted"),
    exit_code = 0,
  }
end

function S.result_deferred(result)
  return type(result) == "table" and result.deferred == true
end

return S
