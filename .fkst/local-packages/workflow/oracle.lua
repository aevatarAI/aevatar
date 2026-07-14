local M = {}

function M.effect_key(effect)
  if effect.kind == "write" then
    return "W:" .. tostring(effect.op) .. ":" .. tostring(effect.target or "") .. ":" .. tostring(effect.dedup_key or "")
  end
  return "R:" .. tostring(effect.queue) .. ":" .. tostring(effect.dedup_key or "")
end

function M.recorder()
  local writes = {}
  local raises = {}
  return {
    record_write = function(write)
      write.kind = "write"
      table.insert(writes, write)
    end,
    record_raise = function(raised)
      raised.kind = "raise"
      table.insert(raises, raised)
    end,
    record_read = function(_read)
    end,
    effects = function()
      local all = {}
      for _, write in ipairs(writes) do
        table.insert(all, write)
      end
      for _, raised in ipairs(raises) do
        table.insert(all, raised)
      end
      return all
    end,
  }
end

function M.same_effects(a, b)
  local function bag(list)
    local effects = {}
    for _, effect in ipairs(list or {}) do
      local key = M.effect_key(effect)
      effects[key] = (effects[key] or 0) + 1
    end
    return effects
  end
  local left = bag(a)
  local right = bag(b)
  for key, count in pairs(left) do
    if right[key] ~= count then
      return false
    end
  end
  for key, count in pairs(right) do
    if left[key] ~= count then
      return false
    end
  end
  return true
end

return M
