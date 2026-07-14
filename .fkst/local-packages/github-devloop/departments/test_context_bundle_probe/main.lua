local saga = require("workflow.saga")
local helper = require("tests.context_bundle_probe_helpers")

local spec = {
  consumes = { "context_bundle_probe" },
  produces = { "context_bundle_probe", "context_bundle_probe_result" },
  fanout = { "context_bundle_probe" },
  ephemeral = { "context_bundle_probe" },
  retry = false,
}

return saga.department(spec, {
  name = "test_context_bundle_probe",
  done = function(_event)
    return false
  end,
  act = function(event)
    return helper.pipeline(event)
  end,
})
