local M = {}

local function workflow_run_argv(repo, workflow, ref, fields)
  local argv = {
    "gh",
    "workflow",
    "run",
    tostring(workflow),
    "--repo",
    tostring(repo),
  }
  if ref ~= nil then
    table.insert(argv, "--ref")
    table.insert(argv, tostring(ref))
  end
  for key, value in pairs(fields or {}) do
    table.insert(argv, "-f")
    table.insert(argv, tostring(key) .. "=" .. tostring(value))
  end
  return argv
end

function M.install(handle)
  function handle.workflow_run(repo, workflow, ref, fields, timeout)
    return handle._exec(workflow_run_argv(repo, workflow, ref, fields), timeout, "gh workflow run")
  end
end

return M
