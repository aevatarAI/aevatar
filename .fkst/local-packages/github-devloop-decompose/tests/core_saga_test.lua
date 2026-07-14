local h = require("tests.devloop_helpers")
local t = h.t
local core = h.core

return {
  test_effect_once_skips_when_completion_fact_is_visible = function()
    local calls = 0
    local result = core.effect_once({
      effect_id = "github-devloop/saga-test/complete",
      completion_check = function()
        return true
      end,
      perform = function()
        calls = calls + 1
      end,
    })

    t.eq(result.action, "skip")
    t.eq(result.effect_id, "github-devloop/saga-test/complete")
    t.eq(calls, 0)
  end,

  test_effect_once_performs_when_completion_fact_is_absent = function()
    local calls = 0
    local result = core.effect_once({
      effect_id = "github-devloop/saga-test/missing",
      completion_check = function()
        return false
      end,
      perform = function()
        calls = calls + 1
        return "created"
      end,
    })

    t.eq(result.action, "perform")
    t.eq(result.result, "created")
    t.eq(calls, 1)
  end,
}
