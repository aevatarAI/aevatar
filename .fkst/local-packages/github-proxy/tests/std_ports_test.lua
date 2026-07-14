local ports = require("forge.ports")

local function assert_install_rejects(make_department)
  local ok, err = pcall(ports.install, make_department)
  assert(ok == false, "install rejects malformed make_department return")
  assert(tostring(err):find("forge.ports.install: make_department must return a table with spec and pipeline", 1, true) ~= nil,
    "install reports the department return-shape contract")
end

return {
  test_install_passes_production_handles_and_exposes_make_department = function()
    local seen
    local make_department = function(p)
      seen = p
      return { spec = { consumes = { "q" } }, pipeline = function() end }
    end

    local dept = ports.install(make_department)

    assert(type(seen) == "table", "make_department receives a ports table")
    assert(type(seen.github) == "table" and type(seen.github.read_issue) == "function", "github handle is the adapter")
    assert(type(seen.git) == "table", "git handle present")
    assert(dept.spec.consumes[1] == "q", "department spec preserved")
    assert(dept.pipeline ~= nil, "department pipeline preserved")
    assert(type(dept.make_department) == "function", "make_department exposed for fake-port tests")
  end,

  test_install_publishes_pipeline_and_fake_factory_restores_previous_pipeline = function()
    local production_pipeline = function() return "production" end
    local fake_pipeline = function() return "fake" end
    local before = _G.pipeline
    _G.pipeline = function() return "before" end

    local dept = ports.install(function(p)
      if p.fake then
        _G.pipeline = fake_pipeline
        return { spec = { consumes = { "q" } }, pipeline = fake_pipeline }
      end
      _G.pipeline = production_pipeline
      return { spec = { consumes = { "q" } }, pipeline = production_pipeline }
    end)

    assert(_G.pipeline == production_pipeline, "install publishes the production pipeline")
    local fake = dept.make_department({ fake = true })
    assert(fake.pipeline == fake_pipeline, "fake department keeps its returned pipeline")
    assert(_G.pipeline == production_pipeline, "fake factory restores the published pipeline")
    _G.pipeline = before
  end,

  test_install_captures_global_pipeline_side_effect_into_department_contract = function()
    local side_effect_pipeline = function() return "side-effect" end
    local returned_pipeline = function() return "returned" end
    local before = _G.pipeline
    _G.pipeline = function() return "before" end

    local dept = ports.install(function()
      _G.pipeline = side_effect_pipeline
      return { spec = { consumes = { "q" } }, pipeline = returned_pipeline }
    end)

    assert(dept.pipeline == side_effect_pipeline, "install captures the side-effect pipeline")
    assert(_G.pipeline == side_effect_pipeline, "install publishes the captured pipeline")
    _G.pipeline = before
  end,

  test_production_handles_builds_github_and_git_handles = function()
    local handles = ports.production_handles()
    assert(type(handles.github.read_issue) == "function", "github adapter handle")
    assert(type(handles.git) == "table", "git adapter handle")
  end,

  test_install_rejects_non_function = function()
    assert(not pcall(ports.install, nil), "install requires a make_department function")
  end,

  test_install_rejects_nil_department = function()
    assert_install_rejects(function()
      return nil
    end)
  end,

  test_install_rejects_non_table_department = function()
    assert_install_rejects(function()
      return "department"
    end)
  end,

  test_install_rejects_department_missing_spec = function()
    assert_install_rejects(function()
      return { pipeline = function() end }
    end)
  end,

  test_install_rejects_department_with_non_table_spec = function()
    assert_install_rejects(function()
      return { spec = "spec", pipeline = function() end }
    end)
  end,

  test_install_rejects_department_missing_pipeline = function()
    assert_install_rejects(function()
      return { spec = {} }
    end)
  end,

  test_install_rejects_department_with_non_function_pipeline = function()
    assert_install_rejects(function()
      return { spec = {}, pipeline = "pipeline" }
    end)
  end,
}
