-- std module behavior tests are hosted in github-proxy (a flat package, the
-- strictest single-root conformance gate) because the engine test runner only
-- scans <root>/tests and <root>/departments/* (no recursion into std/tests).
local payload = require("contract.payload")
local t = fkst.test

local function captured_error(fn)
  local ok, err = pcall(fn)
  t.eq(ok, false)
  return tostring(err)
end

return {
  test_require_field_returns_present_values = function()
    t.eq(payload.require_field({ repo = "owner/repo" }, "repo", "autochrono"), "owner/repo")
    t.eq(payload.require_field({ count = 0 }, "count", "autochrono"), 0)
  end,

  test_require_field_preserves_package_error_context = function()
    local missing = captured_error(function()
      payload.require_field({}, "repo", "autochrono")
    end)
    local empty = captured_error(function()
      payload.require_field({ repo = "" }, "repo", "github-autochrono glue")
    end)

    t.eq(missing:find("autochrono: missing repo", 1, true) ~= nil, true)
    t.eq(empty:find("github-autochrono glue: missing repo", 1, true) ~= nil, true)
  end,
}
