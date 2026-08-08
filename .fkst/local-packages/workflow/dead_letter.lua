-- workflow.dead_letter: shared deterministic L2 dead-letter logging handlers.
local error_facts = require("contract.error_facts")

local D = {}

function D.extract_source_ref(payload)
  local source_ref = payload.source_ref
  if source_ref == nil and type(payload.payload) == "table" then
    source_ref = payload.payload.source_ref
  end
  if type(source_ref) == "table" then
    return error_facts.source_ref_field(source_ref)
  end
  return error_facts.one_line(source_ref)
end

function D.extract_dedup_key(payload)
  if payload.dedup_key ~= nil then
    return payload.dedup_key
  end
  if type(payload.payload) == "table" then
    return payload.payload.dedup_key
  end
  return nil
end

function D.error_fields(payload)
  local error_class = error_facts.one_line(payload.error_class or "dead-letter")
  local error_message = payload.error or payload.message or error_class
  return error_facts.error_fact_fields(error_class, payload.queue, payload.dept, error_message, {
    source_ref = payload.source_ref or (type(payload.payload) == "table" and payload.payload.source_ref or nil),
    attempt = payload.attempt,
    terminal = true,
  })
end

function D.log_dead_letter(package_name, payload)
  log.warn(
    tostring(package_name)
      .. " dept=dead_letter tag=DEAD_LETTER"
      .. " " .. table.concat(D.error_fields(payload), " ")
      .. " delivery_id=" .. error_facts.one_line(payload.delivery_id)
      .. " queue=" .. error_facts.one_line(payload.queue)
      .. " dead_dept=" .. error_facts.one_line(payload.dept)
      .. " source_ref=" .. D.extract_source_ref(payload)
      .. " dedup_key=" .. error_facts.one_line(D.extract_dedup_key(payload))
      .. " attempt=" .. error_facts.one_line(payload.attempt)
      .. " error=" .. error_facts.one_line(payload.error)
  )
end

function D.handlers(opts)
  local config = opts or {}
  local package_name = config.package or "fkst-package"

  local function dead_letter_done(_event)
    return false
  end

  local function act_dead_letter(event)
    local payload = event.payload or {}
    D.log_dead_letter(package_name, payload)
    if type(config.after_log) == "function" then
      config.after_log(payload)
    end
  end

  return {
    done = dead_letter_done,
    act = act_dead_letter,
    wrap = config.wrap,
    name = "dead_letter",
  }
end

return D
