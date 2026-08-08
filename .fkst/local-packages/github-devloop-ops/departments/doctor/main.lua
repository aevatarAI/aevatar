local core = require("core")
local devloop_logging = require("devloop.logging")
local saga = require("workflow.saga")

local spec = {
  consumes = { "devloop_doctor_tick" },
  produces = {},
  retry = false,
  stall_window = "2m",
}

local function doctor_done(_event)
  return false
end

local function act_doctor(_event)
  print(core.saga_doctor_run())
end

return saga.department(spec, {
  done = doctor_done,
  act = act_doctor,
  wrap = devloop_logging.wrap_pipeline_failure,
  name = "doctor",
})
