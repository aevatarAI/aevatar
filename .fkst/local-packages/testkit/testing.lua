local M = {}

local function capture_raises(fn)
  local old_raise = raise
  local raised = {}
  raise = function(queue, payload)
    table.insert(raised, {
      queue = queue,
      payload = payload,
    })
  end
  local ok, result = pcall(fn)
  raise = old_raise
  if ok then
    return result, raised, nil
  end
  return nil, raised, { error = result }
end

local function writes_from_department(dept)
  local ports = type(dept) == "table" and dept.ports or nil
  local model = type(dept) == "table" and dept.model or nil
  if type(model) == "table" and type(model.writes) == "table" then
    return model.writes
  end
  if type(ports) == "table" then
    for _, port in pairs(ports) do
      if type(port) == "table" and type(port._model) == "table" and type(port._model.writes) == "table" then
        return port._model.writes
      end
    end
  end
  return {}
end

local function capture_pipeline(dept, event)
  assert(type(dept) == "table", "dept must be a table")
  assert(type(dept.pipeline) == "function", "dept must expose .pipeline")
  local result, raises, failure = capture_raises(function()
    return dept.pipeline(event)
  end)
  return result, raises, failure, writes_from_department(dept)
end

-- Expose, don't swallow (#710 Finding 2): a pipeline error under run_fake fails
-- the test loudly. The previous run_fake returned a {failure} shape on error, so
-- a test that forgot to assert `failure == nil` passed even when the pipeline
-- errored — a false-green that undercuts the #633 "no false-green" promise.
-- Tests that intend to assert an error use run_fake_expecting_failure.
function M.run_fake(dept, event)
  local result, raises, failure, writes = capture_pipeline(dept, event)
  if failure ~= nil then
    error(failure.error, 0)
  end
  return {
    result = result,
    raises = raises,
    writes = writes,
    failure = nil,
  }
end

function M.run_fake_expecting_failure(dept, event)
  local result, raises, failure, writes = capture_pipeline(dept, event)
  assert(failure ~= nil, "run_fake_expecting_failure: pipeline was expected to error but did not")
  return {
    result = result,
    raises = raises,
    writes = writes,
    failure = failure,
  }
end

return M
