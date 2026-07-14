local devloop_base = require("devloop.base")
local m_claims = require("devloop.claims")
local parsers_issue = require("devloop.parsers.issue")
local core = require("core")
local execution_start = require("devloop.execution_start")
local saga = require("workflow.saga")
local v_execution_request = require("devloop.validators.execution_request")
local entity_lib = require("devloop.entity")

local spec = {
  consumes = { "devloop_execute_request" },
  published_seam = { "devloop_execute_request" },
  produces = {
    "consensus.proposal",
    "github-proxy.github_issue_comment_request",
    "github-proxy.github_issue_label_request",
  },
  stall_window = "30s",
}

local function execute_start_done(_event)
  return false
end

local function read_current(repo, issue_number, request)
  local view = core.gh_issue_view_intake_judge(repo, issue_number, 30)
  if view.exit_code ~= 0 then
    error("github-devloop: gh-issue-execute-start-view-failed: " .. tostring(view.stderr))
  end
  local current = parsers_issue.parse_issue_view_intake_judge(core, view.stdout)
  current.repo, current.number = repo, issue_number
  core.log_forged_markers("execute_start", request.proposal_id, current.comments)
  if current.state ~= "OPEN" then
    core.log_cas_decision("execute_start", request.proposal_id, { state = nil, version = nil }, "execution-request", "thinking", "skip-closed", "issue is not open")
    return nil
  end
  if devloop_base.is_intake_held(current.labels) then
    core.log_cas_decision("execute_start", request.proposal_id, { state = nil, version = nil }, "execution-request", "thinking", "skip-held", "fkst-dev:hold label is present")
    return nil
  end
  if not m_claims.claim_issue_for_management(core, "execute_start", repo, issue_number, current, request.proposal_id) then
    return nil
  end
  return current
end

local function raise_execution_start(repo, issue_number, request, current, event_ts)
  local effects = execution_start.build_execution_start_effects(core, repo, issue_number, request, current, event_ts, "execute_start")
  if effects == nil then
    log.warn("github-devloop dept=execute_start proposal_id=" .. tostring(request.proposal_id) .. " tag=SKIP reason=cannot-build-valid-execution-start-effects")
    return false
  end
  local proposal = effects.proposal
  local add_labels, remove_labels = core.state_label_changes("thinking")
  core.log_apply("execute_start", request.proposal_id, "thinking", proposal.effect_version, {
    add = add_labels,
    remove = remove_labels,
  }, {
    "github-proxy.github_issue_comment_request",
    "github-proxy.github_issue_label_request",
    "consensus.proposal",
  })
  core.log_raise("execute_start", request.proposal_id, "github-proxy.github_issue_comment_request", effects.thinking_comment_request)
  core.log_raise("execute_start", request.proposal_id, "github-proxy.github_issue_label_request", effects.thinking_label_request)
  core.log_raise("execute_start", request.proposal_id, "consensus.proposal", proposal)
  return true
end

local function act_execute_start(event)
  local request = event.payload or {}
  if not v_execution_request.is_supported_execution_request(core, request) then
    core.log_entry("execute_start", event, "unknown", core.payload_field(request, "dedup_key"))
    core.log_cas_decision("execute_start", "unknown", { state = nil, version = nil }, "execution-request", "thinking", "skip-foreign(payload)", "unsupported event payload")
    return
  end

  core.log_entry("execute_start", event, request.proposal_id, request.dedup_key)
  local repo, issue_number = devloop_base.parse_issue_source_ref(request.source_ref)
  if repo == nil then
    core.log_cas_decision("execute_start", request.proposal_id, { state = nil, version = nil }, "execution-request", "thinking", "skip-foreign(source_ref)", "invalid source_ref")
    return
  end

  local lock_key = entity_lib.observe_lock_key(repo, issue_number)
  with_lock(lock_key, function()
    devloop_base.assert_trusted_bot_configured()
    local current = read_current(repo, issue_number, request)
    if current == nil then
      return
    end
    raise_execution_start(repo, issue_number, request, current, event.ts)
  end)
end

return saga.department(spec, {
  done = execute_start_done,
  act = act_execute_start,
  wrap = core.wrap_pipeline_failure,
  name = "execute_start",
})
