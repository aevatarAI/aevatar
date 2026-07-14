local testing = require("testkit.testing")
local run_fake = testing.run_fake
local run_fake_expecting_failure = testing.run_fake_expecting_failure
local gh_fake = require("forge.github_fake")

local function failing_department()
  return {
    spec = { consumes = { "demo" } },
    pipeline = function(_event)
      raise("demo.before-fail", { dedup_key = "before-fail" })
      error("forced fake failure")
    end,
  }
end

local function make_test_department(ports)
  local function pipeline(event)
    local issue = ports.github.read_issue(event.payload.source_ref)
    if issue.state == "OPEN" then
      raise("demo.request", { dedup_key = "d:" .. issue.number })
    end
  end
  return { spec = { consumes = { "demo" } }, pipeline = pipeline, ports = ports }
end

return {
  test_run_fake_captures_raises_and_reads = function()
    local model = gh_fake.model({
      issues = {
        ["owner/repo#issue/42"] = { number = 42, state = "OPEN" },
      },
    })
    local dept = make_test_department({ github = gh_fake.new(model), git = nil })
    local result = run_fake(dept, {
      payload = {
        source_ref = { kind = "external", ref = "owner/repo#issue/42" },
      },
    })
    assert(result.result == nil)
    assert(result.failure == nil)
    assert(#result.raises == 1, "must capture the S2 raise")
    assert(result.raises[1].queue == "demo.request")
    assert(result.raises[1].payload.dedup_key == "d:42")
    assert(result.writes == model.writes)
  end,

  test_run_fake_reraises_pipeline_error_by_default = function()
    -- Regression (#710 Finding 2): a pipeline error under run_fake must fail the
    -- test loudly (re-raise), not return a {failure} shape a caller may forget
    -- to assert on.
    local ok, err = pcall(run_fake, failing_department(), { payload = {} })
    assert(ok == false, "run_fake must re-raise a pipeline error, not swallow it")
    assert(tostring(err):find("forced fake failure", 1, true) ~= nil)
  end,

  test_run_fake_expecting_failure_captures_error_shape = function()
    local result = run_fake_expecting_failure(failing_department(), { payload = {} })
    assert(result.result == nil)
    assert(result.failure ~= nil)
    assert(tostring(result.failure.error):find("forced fake failure", 1, true) ~= nil)
    assert(#result.raises == 1)
    assert(result.raises[1].queue == "demo.before-fail")
    assert(type(result.writes) == "table")
  end,

  test_run_fake_expecting_failure_rejects_a_succeeding_pipeline = function()
    local dept = { spec = { consumes = { "demo" } }, pipeline = function(_event) end }
    assert(not pcall(run_fake_expecting_failure, dept, { payload = {} }),
      "run_fake_expecting_failure must reject a pipeline that did not error")
  end,
}
