local base_ids = require("devloop.base_ids")
local convergence_identity = require("contract.convergence_identity")
local devloop_logging = require("devloop.logging")
local workflow_codex = require("workflow.codex")
local C = {}

function C.dispatch_live_run_exec_ref(role, proposal_id, dedup_key)
  if tostring(role or "") == "implement" then
    return base_ids.dedup_key({
      "implement-exec",
      tostring(proposal_id or ""),
      tostring(dedup_key or ""),
      "implement",
    })
  end
  return base_ids.dedup_key({
    "dispatch-live-run",
    tostring(role or ""),
    tostring(proposal_id or ""),
    tostring(dedup_key or ""),
  })
end

local function row_matches_role(row, role)
  local real_execution = row and row.liveness_contract and row.liveness_contract.real_execution
  local match = real_execution and real_execution.match or nil
  return type(real_execution) == "table"
    and real_execution.primitive == "fkst.codex_runs"
    and type(match) == "table"
    and tostring(match.role or "") == tostring(role or "")
    and match.proposal_id == "state.proposal_id"
    and (match.dedup_key == "state.version" or match.dedup_key == "state.work_unit_key")
end

local function table_opt(value)
  return type(value) == "table" and value or {}
end

local function copy_with_defaults(value, defaults)
  local out = {}
  for key, item in pairs(table_opt(value)) do
    out[key] = item
  end
  for key, item in pairs(defaults or {}) do
    if out[key] == nil then
      out[key] = item
    end
  end
  return out
end

local function matching_liveness_row(liveness, role)
  local rows = type(liveness.restart_transition_table) == "function" and liveness.restart_transition_table() or {}
  for _, row in ipairs(rows) do
    if row_matches_role(row, role) then
      return row
    end
  end
  return nil
end

local function shared_liveness_live(liveness, role, proposal_id, dedup_key, facts)
  local run_identity = convergence_identity.from_parts(role, proposal_id, dedup_key, {
    angle_lane = "worker",
  })
  if workflow_codex.live_run_active(run_identity) then
    return true
  end
  if type(liveness) ~= "table" or type(liveness.restart_row_receiver_liveness) ~= "function" then
    return nil
  end
  local row = matching_liveness_row(liveness, role)
  if row == nil then
    return nil
  end
  local current_facts = table_opt(facts)
  local state = copy_with_defaults(current_facts.state or current_facts.fresh_current_state, {
    state = row.from_state,
    proposal_id = proposal_id,
    version = dedup_key,
    work_unit_key = dedup_key,
  })
  current_facts = copy_with_defaults(current_facts, {
    proposal_id = proposal_id,
    now_seconds = type(now) == "function" and now() or nil,
  })
  local ok, receiver = pcall(liveness.restart_row_receiver_liveness, row, state, current_facts, current_facts.now_seconds)
  if not ok then
    if type(devloop_logging.log_line) == "function" then
      devloop_logging.log_line("warn", "liveness", "github-devloop/codex-runs", "CODEX_RUNS", {
        "outcome=dispatch-live-run-resolver-failed",
        "role=" .. tostring(role or ""),
        "reason=" .. tostring(receiver or ""),
      })
    end
    return true
  end
  if type(receiver) == "table" then
    return receiver.action == "defer"
  end
  return nil
end

function C.dispatch_live_run_dedup(liveness, role, proposal_id, dedup_key, facts)
  if type(role) ~= "string" or role == "" then
    return false
  end
  local live = shared_liveness_live(liveness, role, proposal_id, dedup_key, facts)
  return live == true
end

return C
