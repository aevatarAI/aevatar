-- Direct contract test for the contract.sweep shared module extracted from
-- core/sweep_bounds. Pins the pure leaf behavior (bounds, rotation offset,
-- cursor batching, deferred-result shapes) so future cross-package consumers
-- (and any further extraction) cannot drift it silently. The package M.sweep_*
-- facade -- including the package-local rotate/batch orchestrators -- is covered
-- through the observability/liveness suites; this file pins the contract.
local sweep = require("workflow.sweep")
local t = fkst.test

local function eq_list(actual, expected)
  t.eq(#actual, #expected)
  for i = 1, #expected do
    t.eq(actual[i], expected[i])
  end
end

return {
  test_positive_integer_bounds_and_fallback = function()
    t.eq(sweep.positive_integer(7, 25, 1, 300), 7)
    t.eq(sweep.positive_integer(0, 25, 1, 300), 25) -- below minimum
    t.eq(sweep.positive_integer(500, 25, 1, 300), 25) -- above maximum
    t.eq(sweep.positive_integer(3.5, 25, 1, 300), 25) -- non-integer
    t.eq(sweep.positive_integer("nope", 25, 1, 300), 25) -- non-numeric
  end,

  test_rotation_offset_numeric_and_hash = function()
    t.eq(sweep.rotation_offset(4, 1), 1) -- numeric seed -> seed % count
    t.eq(sweep.rotation_offset(0, 5), 0) -- non-positive count
    local off = sweep.rotation_offset(4, "abc") -- string seed -> checksum path
    t.eq(off >= 0 and off < 4, true)
    t.eq(sweep.rotation_offset(4, "abc"), off) -- deterministic for same seed
  end,

  test_cursor_batch_wraparound = function()
    local all, rem0, next0 = sweep.cursor_batch({ 1, 2, 3 }, 0, 5, 25)
    eq_list(all, { 1, 2, 3 })
    t.eq(rem0, 0)
    t.eq(next0, 0)
    local sel, rem, nxt = sweep.cursor_batch({ 1, 2, 3, 4, 5 }, 4, 2, 25)
    eq_list(sel, { 5, 1 }) -- starts at cursor 4, wraps
    t.eq(rem, 3)
    t.eq(nxt, 1)
  end,

  test_cursor_advance = function()
    t.eq(sweep.cursor_advance(4, 5, 2), 1) -- (4+2) % 5
    t.eq(sweep.cursor_advance(0, 0, 3), 0) -- non-positive total
  end,

  test_deferred_result_shape = function()
    local d = sweep.deadline_deferred_result("my-class", "boom")
    t.eq(d.deferred, true)
    t.eq(d.reason, "deadline")
    t.eq(d.error_class, "my-class")
    t.eq(d.stdout, "")
    t.eq(d.stderr, "boom")
    t.eq(d.exit_code, 0)
    local default = sweep.deadline_deferred_result()
    t.eq(default.error_class, "sweep command")
    t.eq(default.stderr, "sweep deadline exhausted")
  end,

  test_result_deferred_predicate = function()
    t.eq(sweep.result_deferred({ deferred = true }), true)
    t.eq(sweep.result_deferred({ deferred = false }), false)
    t.eq(sweep.result_deferred({}), false)
    t.eq(sweep.result_deferred("str"), false)
    t.eq(sweep.result_deferred(nil), false)
  end,
}
