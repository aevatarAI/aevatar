local error_facts = require("contract.error_facts")
local devloop_base = require("devloop.base")
local core, saga = require("core"), require("workflow.saga")
local common = require("departments.observability.common")
local avm_scoreboard = require("departments.observability.avm_scoreboard")
local census = require("departments.observability.census")
local dashboard = require("departments.observability.dashboard")
local failure_triage_cap = require("failure_triage_cap")
local queue_starvation = require("devloop.queue_starvation")
local reaper = require("departments.observability.reaper")
local topology = require("departments.observability.topology")
local devloop_logging = require("devloop.logging")


local spec = {
  consumes = { "devloop_observe_tick" },
  produces = { "github-proxy.github_issue_create_request" },
  graph_json = true,
  retry = false,
  stall_window = "2m",
}

common.install_common(core)
avm_scoreboard.install_avm_scoreboard(core)
census.install_census(core)
reaper.install_reaper(core)
dashboard.install_dashboard(core)

function core.observability_topology_mermaid()
  if type(graph_json) ~= "function" then
    return nil
  end
  local ok, result = pcall(function()
    local decoded = json.decode(graph_json())
    return topology.render_mermaid(decoded)
  end)
  if not ok then
    local reason = devloop_base._one_line and error_facts.one_line(result) or tostring(result or "")
    log.warn("github-devloop dept=observability tag=TOPOLOGY_UNAVAILABLE reason=" .. tostring(reason))
    return nil
  end
  return result
end

function core.observe_devloop_entities(event)
  common.require_observe_bot(core)
  local repo = common.require_observe_repo(core)
  local limits = core.observability_limits()
  local deadline = core.observability_deadline(now(), limits)
  local observed = core.collect_observability_entities(event, repo, limits, deadline)
  local recent_merged_prs = core.collect_recent_merged_prs(repo, limits, deadline)
  local recent_merged_issues = core.collect_recent_merged_issues(repo, limits, deadline)

  core.reap_orphan_prs(repo, observed.list)
  local queue_starvation_result = queue_starvation.observe_queue_starvation(core, repo, observed.list, limits, deadline, observed.now_seconds)
  for _, entity in ipairs(observed.list or {}) do
    for _, raised in ipairs(failure_triage_cap.blocked_obligation_patrol_once(entity, observed.list, recent_merged_issues)) do
      devloop_logging.log_raise("blocked_obligation_patrol", raised.fact.proposal_id, raised.queue, raised.payload)
    end
  end
  local conflict_hotspot = core.observe_conflict_hotspots(repo, core.observability_call_timeout(limits, deadline))
  local rendered_dashboard = core.render_observability_dashboard({
    entities = observed.list,
    counts = observed.counts,
    stalls = observed.stalls,
    state_gap_report = observed.state_gap_report,
    recent_merged_prs = recent_merged_prs,
    recent_merged_issues = recent_merged_issues,
    now_seconds = observed.now_seconds,
    topology_mermaid = core.observability_topology_mermaid(),
  })
  core.publish_observability_dashboard(repo, rendered_dashboard, limits, deadline)

  return {
    entity_count = #observed.list,
    counts = observed.counts,
    queue_starvation = queue_starvation_result,
    conflict_hotspot = conflict_hotspot,
    state_gap_report = observed.state_gap_report,
    dashboard_hash = rendered_dashboard.hash,
  }
end

local department = saga.department(spec, { done = function() return false end, act = function(event)
  devloop_logging.log_entry("observability", event, "github-devloop/observability", "tick")
  core.observe_devloop_entities(event)
end, wrap = devloop_logging.wrap_pipeline_failure, name = "observability" })
department.spec.graph_json = true

return department
