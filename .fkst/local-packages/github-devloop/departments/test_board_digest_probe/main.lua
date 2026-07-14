local saga = require("workflow.saga")
local helper = require("tests.board_digest_probe_helpers")

local spec = {
  consumes = { "board_digest_probe" },
  produces = { "board_digest_probe", "board_digest_result" },
  fanout = { "board_digest_probe" },
  ephemeral = { "board_digest_probe" },
  retry = false,
}

return saga.department(spec, {
  name = "test_board_digest_probe",
  done = function(_event)
    return false
  end,
  act = function(event)
    return helper.pipeline(event)
  end,
})
