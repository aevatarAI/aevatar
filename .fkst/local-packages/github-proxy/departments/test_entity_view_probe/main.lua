local saga = require("workflow.saga")
local helper = require("tests.entity_view_probe_helpers")

local spec = {
  consumes = { "entity_view_probe" },
  produces = { "entity_view_probe", "entity_view_probe_result" },
  fanout = { "entity_view_probe" },
  ephemeral = { "entity_view_probe" },
  retry = false,
}

return saga.department(spec, {
  name = "test_entity_view_probe",
  done = function(_event)
    return false
  end,
  act = function(event)
    return helper.pipeline(event)
  end,
})
