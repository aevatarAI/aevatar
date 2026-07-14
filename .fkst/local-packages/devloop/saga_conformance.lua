local saga_conformance = {}

local function record(id, message)
  return { id = id, message = tostring(message) }
end

local function append_records(out, id, messages)
  for _, message in ipairs(messages or {}) do
    table.insert(out, record(id, message))
  end
end

function saga_conformance.errors(core)
  if type(core) ~= "table" then
    return { record("saga.conformance", "core module is unavailable") }
  end
  if type(core.restart_transition_table) ~= "function" then
    return {}
  end

  local rows = core.restart_transition_table()
  local out = {}
  if type(core.strict_restart_responsibility_contract_errors) == "function" then
    append_records(out, "saga.restart-responsibility", core.strict_restart_responsibility_contract_errors(rows))
  end
  if type(core.strict_restart_liveness_contract_errors) == "function" then
    append_records(out, "saga.restart-liveness", core.strict_restart_liveness_contract_errors(rows))
  end
  return out
end

return saga_conformance
