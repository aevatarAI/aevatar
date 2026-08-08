local h = require("tests.devloop_ops_helpers")
local t = h.t

local package_root = "packages/github-devloop-ops"

local function read_source(path)
  local handle = assert(io.open(package_root .. "/" .. path, "r"))
  local body = handle:read("*a")
  handle:close()
  return body
end

local function line_count(body)
  local count = 0
  for _ in tostring(body or ""):gmatch("\n") do
    count = count + 1
  end
  return count
end

local function assert_module(path, install_name)
  local body = read_source(path)
  t.is_true(line_count(body) < 700)
  t.is_true(body:find("function M%.install_" .. install_name, 1, false) ~= nil)
  t.is_true(body:find("return M", 1, true) ~= nil)
end

return {
  test_observability_core_does_not_depend_on_department_private_modules = function()
    local core_body = read_source("core.lua")
    t.eq(core_body:find('require("core.observability")', 1, true), nil)

    local core_observability = io.open(package_root .. "/core/observability.lua", "r")
    t.eq(core_observability, nil)

    local main_body = read_source("departments/observability/main.lua")
    t.is_true(line_count(main_body) < 250)
    t.is_true(main_body:find('require("departments.observability.census")', 1, true) ~= nil)
    t.is_true(main_body:find('require("departments.observability.common")', 1, true) ~= nil)
    t.is_true(main_body:find('require("departments.observability.avm_scoreboard")', 1, true) ~= nil)
    t.is_true(main_body:find('require("departments.observability.dashboard")', 1, true) ~= nil)
    t.is_true(main_body:find('require("departments.observability.reaper")', 1, true) ~= nil)

    assert_module("departments/observability/common.lua", "common")
    assert_module("departments/observability/avm_scoreboard.lua", "avm_scoreboard")
    t.eq(read_source("departments/observability/avm_scoreboard.lua"):find(
      'require("departments.observability.avm_ingest")',
      1,
      true
    ), nil)
    local avm_ingest = io.open(package_root .. "/departments/observability/avm_ingest.lua", "r")
    t.eq(avm_ingest, nil)
    if avm_ingest ~= nil then
      avm_ingest:close()
    end
    assert_module("departments/observability/census.lua", "census")
    assert_module("departments/observability/dashboard.lua", "dashboard")
    assert_module("departments/observability/reaper.lua", "reaper")
  end,
}
