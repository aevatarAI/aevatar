local devloop_base = require("devloop.base")
local entity_lib = require("devloop.entity")
local base_ids = require("devloop.base_ids")
local parsers_pr = require("devloop.parsers.pr")
local parsers_issue = require("devloop.parsers.issue")
local m_facts = require("devloop.markers.facts")
local core, sweep_bounds = require("core"), require("devloop.sweep_bounds")
local liveness_scan = require("devloop.liveness_scan")
local entity_list_cache = require("devloop.entity_list_cache")
local saga = require("workflow.saga")
local devloop_entity_view = require("devloop.github_proxy_entity_view")

local LIVENESS_SCAN_CURSOR_PREFIX = "github-devloop/liveness-scan/issue-cursor/"

local spec = {
  consumes = { "devloop_liveness_tick" }, published_seam = { "devloop_liveness_tick" },
  produces = {
    "devloop_observe_issue",
    "github-proxy.github_issue_comment_request",
    "github-proxy.github_pr_comment_request",
    "consensus.proposal",
    "devloop_ready",
    "github-devloop-decompose.devloop_decompose",
    "devloop_reconcile",
    "devloop_timeout_reconcile",
  },
  fanout = { "devloop_liveness_tick" },
  stall_window = "30s",
}

local function should_reinject_issue(repo, issue, limits, deadline)
  if not base_ids.issue_ref_round_trips(repo, issue.number) then
    return false
  end

  if not sweep_bounds.sweep_has_budget(deadline) then
    return nil, "deadline"
  end
  local proposal_id = base_ids.proposal_id(repo, issue.number)
  local state_view = require("devloop.github_proxy_entity_view").fetch_issue_view_state(core, repo, issue.number, issue.updated_at, {
    consumer = "liveness_scan",
    timeout = sweep_bounds.sweep_call_timeout(limits, deadline),
  })
  if state_view.exit_code ~= 0 then
    if liveness_scan.liveness_scan_is_timeout_result(core, state_view) then
      return nil, "deadline"
    end
    error("github-devloop: liveness-scan-issue-view-failed: " .. tostring(state_view.stderr))
  end

  local current = parsers_issue.parse_issue_view_state(core, state_view.stdout)
  if tostring(current.state or ""):upper() ~= "OPEN" then
    core.log_cas_decision("liveness_scan", proposal_id, { state = nil, version = nil }, "tick", "observe", "skip-closed", "issue is not open")
    return false
  end

  local state = require("devloop.entity").current_entity_state(core, current.comments, proposal_id)
  if not liveness_scan.liveness_scan_should_reinject_state(core, proposal_id, state) then
    return false
  end
  local snapshot = { comments = current.comments or {}, prs = {}, absent_prs = {}, state = state }
  local delegation = state.state == "awaiting-pr" and m_facts.pr_delegation_fact(core, current.comments, proposal_id, state.version) or nil
  local current_pr = nil
  if delegation ~= nil then
    local pr_view = devloop_entity_view.fetch_pr_view_origin(repo, delegation.pr_number, nil, {
      force_fresh = true,
      consumer = "liveness_scan",
    })
    if pr_view.exit_code ~= 0 then
      if liveness_scan.liveness_scan_is_timeout_result(core, pr_view) then
        return nil, "deadline"
      end
      error("github-devloop: liveness-scan-awaiting-pr-view-failed: " .. tostring(pr_view.stderr))
    end
    current_pr = parsers_pr.parse_pr_view_origin(core, pr_view.stdout)
    current_pr.number = delegation.pr_number
    current_pr.force_fresh = true
    snapshot.comments = current.comments or {}
  end
  local timeout_action = liveness_scan.liveness_scan_maybe_timeout_action(core, liveness_scan.liveness_scan_issue_entity(core, repo, issue.number), state, {
    proposal_id = proposal_id,
    current = { comments = current.comments or {}, labels = current.labels or {} },
    current_issue = current,
    current_pr = current_pr,
    ["pr-delegation"] = delegation,
    pr_delegation = delegation,
    snapshot = snapshot,
    event_ts = issue.updated_at,
    source_ref = entity_lib.issue_source_ref(repo, issue.number),
    fresh_current_state = state,
    now_seconds = now(),
  })
  if timeout_action == "handled" then
    return false
  end
  return true
end

local function liveness_scan_done(_event)
  return false
end

local function act_liveness_scan(event)
  core.log_entry("liveness_scan", event, "github-devloop/liveness-scan", "tick")
  devloop_base.assert_trusted_bot_configured()

  local repo = liveness_scan.liveness_scan_read_repo(core)
  if repo == nil then
    core.log_cas_decision("liveness_scan", "github-devloop/liveness-scan", { state = nil, version = nil }, "tick", "observe", "skip-invalid-repo", "FKST_GITHUB_REPO is missing or invalid")
    return
  end

  local limits = liveness_scan.liveness_scan_limits(core)
  local deadline = sweep_bounds.sweep_deadline(now(), limits)
  local timeout = sweep_bounds.sweep_call_timeout(limits, deadline)
  if timeout <= 0 then
    liveness_scan.liveness_scan_log_deferred(core, "deadline", { entity_cap = limits.entity_cap })
    return
  end
  local issues = liveness_scan.liveness_scan_list_open_issues(core, repo, timeout, entity_list_cache.entity_list_poll_key(core, event))
  local activations, deferred_by_cap, cursor_key, cursor, total = liveness_scan.liveness_scan_activation_slice(core, repo, "issue", issues, LIVENESS_SCAN_CURSOR_PREFIX)
  local processed = 0
  local attempted = 0

  for _, activation in ipairs(activations) do
    if not sweep_bounds.sweep_has_budget(deadline) then
      liveness_scan.liveness_scan_update_cursor(core, cursor_key, cursor, total, attempted)
      liveness_scan.liveness_scan_log_deferred(core, "deadline", {
        listed_issues = #issues,
        processed = processed,
        deferred = (#activations - processed) + deferred_by_cap,
        entity_cap = limits.entity_cap,
      })
      return
    end

    attempted = attempted + 1
    local should_reinject, defer_reason = should_reinject_issue(repo, activation.entity, limits, deadline)
    if defer_reason == "deadline" then
      liveness_scan.liveness_scan_update_cursor(core, cursor_key, cursor, total, attempted)
      liveness_scan.liveness_scan_log_deferred(core, "deadline", {
        listed_issues = #issues,
        processed = processed,
        deferred = (#activations - processed) + deferred_by_cap,
        entity_cap = limits.entity_cap,
      })
      return
    end
    processed = processed + 1
    if should_reinject then
      liveness_scan.liveness_scan_reinject(core, repo, activation.entity, "issue", event and event.ts)
    end
  end

  liveness_scan.liveness_scan_update_cursor(core, cursor_key, cursor, total, attempted)

  if deferred_by_cap > 0 then
    liveness_scan.liveness_scan_log_deferred(core, "cap", {
      listed_issues = #issues,
      processed = processed,
      deferred = deferred_by_cap,
      entity_cap = limits.entity_cap,
    })
  end
end

return saga.department(spec, {
  done = liveness_scan_done,
  act = act_liveness_scan,
  name = "liveness_scan",
})
