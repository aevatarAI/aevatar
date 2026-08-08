local M = {}

local function is_nonempty_string(value)
  return type(value) == "string" and value ~= ""
end

local function validate_effect_ids(effect_ids, context)
  if type(effect_ids) ~= "table" then
    error("devloop.restart_effect_entitlements: " .. context .. ".effect_ids must be an array of strings")
  end

  local count = 0
  for key, effect_id in pairs(effect_ids) do
    if type(key) ~= "number" or key < 1 or key % 1 ~= 0 then
      error("devloop.restart_effect_entitlements: " .. context .. ".effect_ids must be an array of strings")
    end
    if type(effect_id) ~= "string" then
      error("devloop.restart_effect_entitlements: " .. context .. ".effect_ids must be an array of strings")
    end
    count = count + 1
  end
  if count ~= #effect_ids then
    error("devloop.restart_effect_entitlements: " .. context .. ".effect_ids must be a dense array")
  end
end

local function validate_entry(entry, context)
  if type(entry) ~= "table" then
    error("devloop.restart_effect_entitlements: " .. context .. " must be a table")
  end
  if not is_nonempty_string(entry.id) then
    error("devloop.restart_effect_entitlements: " .. context .. ".id must be a non-empty string")
  end
  validate_effect_ids(entry.effect_ids, context)
end

local function validate_entitlements(entitlements)
  if type(entitlements) ~= "table" then
    error("devloop.restart_effect_entitlements: edge.transition_effect_entitlements must be a table")
  end
  validate_entry(entitlements.apply, "edge.transition_effect_entitlements.apply")
  validate_entry(entitlements.idempotent, "edge.transition_effect_entitlements.idempotent")
end

function M.resolve(edge, disposition)
  if disposition ~= "apply" and disposition ~= "idempotent" then
    error("devloop.restart_effect_entitlements: disposition must be apply or idempotent")
  end
  if type(edge) ~= "table" then
    error("devloop.restart_effect_entitlements: edge must be a table")
  end

  local entitlements = edge.transition_effect_entitlements
  validate_entitlements(entitlements)
  local selected = entitlements[disposition]
  local effect_ids = {}
  for _, effect_id in ipairs(selected.effect_ids) do
    table.insert(effect_ids, effect_id)
  end
  return {
    id = selected.id,
    effect_ids = effect_ids,
  }
end

return M
