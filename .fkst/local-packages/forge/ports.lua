-- forge.ports: production port wiring shared by every department on the gh/git
-- Ports & Adapters layer. A department defines make_department(ports) that
-- closes over the injected handles and returns its engine-facing table
-- ({ spec, pipeline }); forge.ports.install builds the production handles from the
-- host exec_argv primitive, constructs the department, and exposes
-- make_department so fake-port tests can re-build the same department against
-- forge.github_fake / forge.git_fake. This removes the per-department
-- production_exec_argv / production_ports copy (DRY: the framework owns the stable
-- common wiring, the script keeps only its business pipeline).
local M = {}

local function production_exec_argv()
  if type(exec_argv) == "function" then
    return exec_argv
  end
  return function()
    error("forge.ports: production ports require exec_argv")
  end
end

function M.production_handles()
  local run = production_exec_argv()
  return {
    github = require("forge.github").new(run),
    git = require("forge.git").new(run),
  }
end

local function validate_department(department)
  if type(department) ~= "table" or type(department.spec) ~= "table" or type(department.pipeline) ~= "function" then
    error("forge.ports.install: make_department must return a table with spec and pipeline", 2)
  end
end

local function make_with_pipeline_restore(make_department, handles)
  local previous_pipeline = _G.pipeline
  local ok, department_or_err = pcall(make_department, handles)
  if not ok then
    _G.pipeline = previous_pipeline
    error(department_or_err, 0)
  end
  local captured_pipeline = _G.pipeline
  if type(department_or_err) == "table" and type(captured_pipeline) == "function" and captured_pipeline ~= previous_pipeline then
    department_or_err.pipeline = captured_pipeline
  end
  local ok_validate, validate_err = pcall(validate_department, department_or_err)
  if not ok_validate then
    _G.pipeline = previous_pipeline
    error(validate_err, 0)
  end
  _G.pipeline = previous_pipeline
  return department_or_err
end

function M.install(make_department)
  assert(type(make_department) == "function", "forge.ports.install requires a make_department function")
  local department = make_with_pipeline_restore(make_department, M.production_handles())
  _G.pipeline = department.pipeline
  department.make_department = function(handles)
    return make_with_pipeline_restore(make_department, handles)
  end
  return department
end

return M
