local failure_triage_cap = require("failure_triage_cap")

local dead_letter = require("workflow.dead_letter")
local devloop_logging = require("devloop.logging")
local saga = require("workflow.saga")

local spec = {
  consumes = { "dead_letter" },
  produces = { "github-proxy.github_issue_create_request" },
  stall_window = "2m",
}

local function triage(payload)
  local decision = failure_triage_cap.decide(payload)
  if decision.action == "raise" then
    devloop_logging.log_raise("failure_triage", decision.fact.fingerprint, "github-proxy.github_issue_create_request", decision.request)
  else
    log.info(
      "github-devloop dept=failure_triage tag=TRIAGE"
        .. " action=" .. tostring(decision.action)
        .. " reason=" .. tostring(decision.reason or "")
        .. " fingerprint=" .. tostring(decision.fact and decision.fact.fingerprint or "")
        .. " count=" .. tostring(decision.count or "")
    )
  end
end

return saga.department(spec, dead_letter.handlers({
  package = "github-devloop",
  after_log = triage,
}))
