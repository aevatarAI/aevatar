local core = require("core")
local saga = require("workflow.saga")

local spec = {
  consumes = { "github_issue_create_request" },
  published_seam = { "github_issue_create_request" },
  produces = { "github_issue_blocked_by_request" },
  published_seam = { "github_issue_create_request" },
  stall_window = "30s",
}

local function done(_event)
  return false
end

local function act(event)
  core.write_issue_create_request(event.payload or {})
end

return saga.department(spec, {
  done = done,
  act = act,
  wrap = core.wrap_pipeline_failure,
  name = "github_issue_create",
})
