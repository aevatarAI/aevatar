local saga = require("workflow.saga")
local helper = require("tests.cache_seed_helpers")

local spec = {
  consumes = { "cache_seed" },
  produces = { "cache_seed", "cache_seeded" },
  fanout = { "cache_seed" },
  ephemeral = { "cache_seed" },
  retry = false,
}

return saga.department(spec, {
  name = "test_cache_seed",
  done = function(_event)
    return false
  end,
  act = function(event)
    return helper.pipeline(event)
  end,
})
