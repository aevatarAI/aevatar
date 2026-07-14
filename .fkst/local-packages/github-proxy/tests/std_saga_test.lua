local saga = require("workflow.saga")
local t = fkst.test

local function event(extra)
  local value = {
    queue = "demo",
    payload = {},
  }
  for key, field in pairs(extra or {}) do
    value[key] = field
  end
  return value
end

return {
  test_department_sets_pipeline_and_runs_act_when_not_done = function()
    local acted = 0
    local module = saga.department({
      consumes = { "demo" },
      produces = { "done" },
    }, {
      name = "demo",
      done = function(_event)
        return false
      end,
      act = function(received)
        acted = acted + 1
        return received.payload.value
      end,
    })

    t.eq(module.spec.consumes[1], "demo")
    t.eq(module.spec.produces[1], "done")
    t.eq(pipeline(event({ payload = { value = "ok" } })), "ok")
    t.eq(acted, 1)
  end,

  test_department_skips_when_done = function()
    local acted = 0
    local skipped = 0
    saga.department({
      consumes = { "demo" },
    }, {
      done = function(_event)
        return true
      end,
      act = function(_event)
        acted = acted + 1
      end,
      on_skip = function(_event)
        skipped = skipped + 1
      end,
    })

    t.is_nil(pipeline(event()))
    t.eq(acted, 0)
    t.eq(skipped, 1)
  end,

  test_department_skips_foreign_before_done = function()
    local accepted = 0
    local done_checked = 0
    local acted = 0
    local skipped_foreign = 0
    saga.department({
      consumes = { "demo" },
    }, {
      accept = function(_event)
        accepted = accepted + 1
        return false
      end,
      done = function(_event)
        done_checked = done_checked + 1
        return false
      end,
      act = function(_event)
        acted = acted + 1
      end,
      on_skip_foreign = function(_event)
        skipped_foreign = skipped_foreign + 1
      end,
    })

    t.is_nil(pipeline(event()))
    t.eq(accepted, 1)
    t.eq(done_checked, 0)
    t.eq(acted, 0)
    t.eq(skipped_foreign, 1)
  end,

  test_department_uses_wrap = function()
    local wrapped_name = nil
    saga.department({
      consumes = { "demo" },
    }, {
      name = "wrapped-demo",
      done = function(_event)
        return false
      end,
      act = function(_event)
        return "raw"
      end,
      wrap = function(name, fn)
        wrapped_name = name
        return function(received)
          return "wrapped-" .. fn(received)
        end
      end,
    })

    t.eq(wrapped_name, "wrapped-demo")
    t.eq(pipeline(event()), "wrapped-raw")
  end,

  test_department_validation_fails_closed = function()
    t.raises(function()
      saga.department(nil)
    end)
    t.raises(function()
      saga.department({ consumes = {} }, { done = function() end, act = function() end })
    end)
    t.raises(function()
      saga.department({ consumes = { "demo" } }, { act = function() end })
    end)
    t.raises(function()
      saga.department({ consumes = { "demo" } }, { done = function() end })
    end)
    t.raises(function()
      saga.department({ consumes = { "demo" } }, nil)
    end)
  end,
}
