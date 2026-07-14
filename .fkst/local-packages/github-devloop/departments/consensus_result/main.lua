local devloop_base = require("devloop.base")
local base_ids = require("devloop.base_ids")
local requests_labels = require("devloop.requests.labels")
local requests_lifecycle = require("devloop.requests.lifecycle")
local core = require("core")
local ports_seam = require("forge.ports")
local saga = require("workflow.saga")
local v_result = require("devloop.validators.result")
local entity_lib = require("devloop.entity")

local spec = {
  consumes = { "consensus.consensus_reached" },
  produces = {
    "github-proxy.github_issue_label_request",
    "github-proxy.github_issue_comment_request",
  },
  fanout = { "consensus.consensus_reached" },
  stall_window = "30s",
  retry = { max_attempts = 12, base = "5s", cap = "30s" },
}

local function result_version(reached)
  return tostring(reached.effect_version or reached.dedup_key)
end

local function dependency_hold_effects_complete(current, reached, version)
  if type(current) ~= "table" or type(reached) ~= "table" then
    return false
  end
  return core.has_state_marker(current.comments, reached.proposal_id, "dependency_wait", version)
    and core.dependency_hold_fact(current.comments, reached.proposal_id) ~= nil
    and core.state_label_hint_matches(current.labels, "dependency_wait")
    and core.has_label(current.labels, core._blocked_on_dependency_label)
end

local function raise_result_effects(repo, issue_number, reached, current, state, gate, reason, version, to_state)
  version = version or result_version(reached)
  to_state = to_state or (gate and gate.ok and "ready" or "dependency_wait")
  local comment_request = requests_lifecycle.build_result_comment_request(core, repo, issue_number, reached, to_state)
  local label_request = requests_labels.build_result_label_request(core, repo, issue_number, reached)
  local dependency_comment_request = nil
  local dependency_label_request = nil
  local dependency_release_comment_request = nil
  if not gate.ok then
    local marker = gate.kind == "cycle"
      and core.dependency_cycle_marker(reached.proposal_id, version)
      or (gate.kind == "unresolvable"
        and core.dependency_unresolvable_marker(reached.proposal_id, version, gate.unmet, gate.kind, gate.reason)
        or core.dependency_wait_marker(reached.proposal_id, version, gate.unmet, gate.kind, gate.reason))
    dependency_comment_request = requests_lifecycle.build_dependency_hold_comment_request(core,
      repo,
      issue_number,
      reached.proposal_id,
      version,
      gate,
      marker,
      reached.source_ref
    )
    dependency_label_request = requests_labels.build_label_request(core,
      repo,
      issue_number,
      { core._blocked_on_dependency_label },
      {},
      base_ids.dedup_key({ "dependency", "label", "hold", tostring(reached.proposal_id), version, tostring(gate.kind) }),
      reached.source_ref
    )
  elseif core.dependency_gate_has_notes(gate) then
    dependency_release_comment_request = requests_lifecycle.build_dependency_release_comment_request(core,
      repo,
      issue_number,
      reached.proposal_id,
      tostring(reached.dedup_key),
      gate,
      reached.source_ref
    )
  end
  table.insert(label_request.remove_labels, core._blocked_on_dependency_label)

  local raised = {}
  if not core.has_result_marker(current.comments, reached.proposal_id, reached.decision, reached.dedup_key) then
    table.insert(raised, "github-proxy.github_issue_comment_request")
  end
  if not core.state_label_hint_matches(current.labels, "ready") then
    table.insert(raised, "github-proxy.github_issue_label_request")
  end
  if gate.ok then
    if dependency_release_comment_request ~= nil then
      table.insert(raised, "github-proxy.github_issue_comment_request")
    end
  else
    if dependency_comment_request ~= nil then
      table.insert(raised, "github-proxy.github_issue_comment_request")
    end
    if dependency_label_request ~= nil then
      table.insert(raised, "github-proxy.github_issue_label_request")
    end
  end
  core.log_apply("consensus_result", reached.proposal_id, to_state, version, { add = { "fkst-dev:ready" }, remove = { "fkst-dev:thinking" } }, raised)

  if not core.has_result_marker(current.comments, reached.proposal_id, reached.decision, reached.dedup_key) then
    core.log_raise("consensus_result", reached.proposal_id, "github-proxy.github_issue_comment_request", comment_request)
  end
  if not core.state_label_hint_matches(current.labels, "ready") then
    core.log_raise("consensus_result", reached.proposal_id, "github-proxy.github_issue_label_request", label_request)
  end
  if not gate.ok then
    core.log_cas_decision("consensus_result", reached.proposal_id, state, "thinking", "dependency_wait", "hold-dependency", gate.reason)
    if dependency_comment_request ~= nil then
      core.log_raise("consensus_result", reached.proposal_id, "github-proxy.github_issue_comment_request", dependency_comment_request)
    end
    if dependency_label_request ~= nil then
      core.log_raise("consensus_result", reached.proposal_id, "github-proxy.github_issue_label_request", dependency_label_request)
    end
    return
  end
  if dependency_release_comment_request ~= nil then
    core.log_raise("consensus_result", reached.proposal_id, "github-proxy.github_issue_comment_request", dependency_release_comment_request)
  end
  core.log_cas_decision("consensus_result", reached.proposal_id, state, "thinking", "ready", reason, "result effects complete or recoverable")
end

local function make_department(ports)
  local function result_done(_event)
    return false
  end

  local function act_result(event)
    local reached = event.payload or {}
    if type(reached) == "table" and reached.schema == "consensus.consensus_reached.v1"
      and reached.decision == "reject" then
      core.log_entry("consensus_result", event, tostring(reached.proposal_id or "unknown"), reached.dedup_key)
      core.log_cas_decision("consensus_result", tostring(reached.proposal_id or "unknown"), { state = nil, version = nil }, "thinking", "ready", "skip-unsupported(decision)", "issue consensus does not support reject")
      return
    end
    if not v_result.is_supported_result(core, reached) then
      core.log_entry("consensus_result", event, "unknown", core.payload_field(reached, "dedup_key"))
      core.log_cas_decision("consensus_result", "unknown", { state = nil, version = nil }, "thinking", "ready", "skip-foreign(proposal_id)", "unsupported event payload")
      return
    end

    core.log_entry("consensus_result", event, reached.proposal_id, reached.dedup_key)
    local version = result_version(reached)
    local repo, issue_number = base_ids.parse_proposal_id(reached.proposal_id)
    if repo == nil then
      core.log_cas_decision("consensus_result", reached.proposal_id, { state = nil, version = nil }, "thinking", "ready", "skip-foreign(proposal_id)", "proposal_id is outside github-devloop")
      return
    end

    local lock_key = entity_lib.result_lock_key(reached.proposal_id)
    if lock_key == nil then
      core.log_cas_decision("consensus_result", reached.proposal_id, { state = nil, version = nil }, "thinking", "ready", "skip-foreign(proposal_id)", "no transition lock key")
      return
    end

    with_lock(lock_key, function()
      devloop_base.assert_trusted_bot_configured()

      local current = ports.github.read_issue({
        kind = "external",
        ref = repo .. "#issue/" .. tostring(issue_number),
      }, {
        consumer = "consensus_result",
        force_fresh = true,
      })
      core.log_forged_markers("consensus_result", reached.proposal_id, current.comments)
      local state = core.current_state(current.comments, reached.proposal_id)
      local gate = core.dependency_gate(repo, issue_number, {
        proposal_id = reached.proposal_id,
        version = version,
        comments = current.comments,
      })
      local to_state = gate.ok and "ready" or "dependency_wait"
      local transition = core.versioned_transition_status(state, { "thinking" }, to_state, version)
      if transition == "idempotent" or transition == "stale" then
        if transition == "idempotent" and tostring(state.version or "") == tostring(version) then
          local complete = gate.ok
            and requests_lifecycle.result_effects_complete(core, current, reached)
            or dependency_hold_effects_complete(current, reached, version)
          if complete then
            core.log_cas_decision("consensus_result", reached.proposal_id, state, "thinking", to_state, "skip-idempotent(result effects complete)", "all declared result effects are derivable")
            return
          end
          raise_result_effects(
            repo,
            issue_number,
            reached,
            current,
            state,
            gate,
            "applied(result effects incomplete)",
            version,
            to_state
          )
          return
        end
        core.log_cas_decision("consensus_result", reached.proposal_id, state, "thinking", to_state, core.cas_outcome(state, transition, version), "consensus result cannot advance current marker")
        return
      end
      if transition == "pending" then
        core.log_cas_decision("consensus_result", reached.proposal_id, state, "thinking", to_state, core.cas_outcome(state, transition, version), "thinking state marker not yet visible")
        error("github-devloop: thinking state marker not yet visible for consensus result; retrying")
      end
      core.log_cas_decision("consensus_result", reached.proposal_id, state, "thinking", to_state, core.cas_outcome(state, transition, version), "consensus decision=" .. tostring(reached.decision))

      raise_result_effects(repo, issue_number, reached, current, state, gate, core.cas_outcome(state, transition, version), version, to_state)
    end)
  end

  local previous_pipeline = _G.pipeline
  local department = saga.department(spec, {
    done = result_done,
    act = act_result,
    wrap = core.wrap_pipeline_failure,
    name = "consensus_result",
  })
  department.pipeline = _G.pipeline
  _G.pipeline = previous_pipeline
  return department
end

local M = ports_seam.install(make_department)
_G.pipeline = M.pipeline

return M
