local core = require("core")
local dead_letter = require("workflow.dead_letter")
local error_facts = require("contract.error_facts")
local saga = require("workflow.saga")

local spec = {
  consumes = { "dead_letter" },
  produces = {},
  stall_window = "2m",
}

local function dead_letter_done(_event)
  return false
end

local function act_dead_letter(event)
  local payload = event.payload or {}
  local error_class = error_facts.one_line(payload.error_class or "dead-letter")
  local error_message = payload.error or payload.message or error_class
  local fields = error_facts.error_fact_fields(error_class, payload.queue, payload.dept, error_message, {
    source_ref = payload.source_ref or (type(payload.payload) == "table" and payload.payload.source_ref or nil),
    attempt = payload.attempt,
    terminal = true,
  })

  log.warn(
    "consensus dept=dead_letter tag=DEAD_LETTER"
      .. " " .. table.concat(fields, " ")
      .. " delivery_id=" .. error_facts.one_line(payload.delivery_id)
      .. " queue=" .. error_facts.one_line(payload.queue)
      .. " dead_dept=" .. error_facts.one_line(payload.dept)
      .. " source_ref=" .. dead_letter.extract_source_ref(payload)
      .. " dedup_key=" .. error_facts.one_line(dead_letter.extract_dedup_key(payload))
      .. " attempt=" .. error_facts.one_line(payload.attempt)
      .. " error=" .. error_facts.one_line(payload.error)
  )
end

return saga.department(spec, {
  done = dead_letter_done,
  act = act_dead_letter,
  wrap = core.wrap_pipeline_failure,
  name = "dead_letter",
})
