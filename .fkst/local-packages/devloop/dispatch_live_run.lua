local base_ids = require("devloop.base_ids")
local error_facts = require("contract.error_facts")
local C = {}

function C.dispatch_live_run_exec_ref(M, role, proposal_id, dedup_key)
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

local function codex_runs_status(M, role)
  local function one_line(value)
    return error_facts.one_line(value)
  end
  local function fallback(reason)
    if type(M.log_line) == "function" then
      M.log_line("warn", "liveness", "github-devloop/codex-runs", "CODEX_RUNS", {
        "outcome=marker-budget-fallback",
        "error_class=codex-runs-unavailable",
        "role=" .. one_line(role),
        "reason=" .. one_line(reason),
      })
    end
    return { running = {}, recent = {}, codex_runs_fallback = true, codex_runs_error = tostring(reason or "unknown") }
  end
  if type(fkst) ~= "table" or type(fkst.codex_runs) ~= "function" then
    return fallback("fkst.codex_runs SDK primitive is unavailable")
  end
  local ok, status = pcall(fkst.codex_runs)
  if not ok then
    return fallback("fkst.codex_runs failed for dispatch live-run dedup: " .. tostring(status))
  end
  if type(status) ~= "table" or type(status.running) ~= "table" then
    return fallback("fkst.codex_runs returned invalid dispatch live-run status")
  end
  return status
end

function C.dispatch_live_run_dedup(M, role, proposal_id, dedup_key, status)
  if type(role) ~= "string" or role == "" then
    return false
  end
  local exec_ref = C.dispatch_live_run_exec_ref(M, role, proposal_id, dedup_key)
  return C.dispatch_live_run_exec_ref_running(M, role, exec_ref, status)
end

function C.dispatch_live_run_exec_ref_running(M, role, exec_ref, status)
  if type(role) ~= "string" or role == "" or type(exec_ref) ~= "string" or exec_ref == "" then
    return false
  end
  local runs = status or codex_runs_status(M, role)
  for _, run in ipairs(runs.running or {}) do
    if type(run) == "table"
      and tostring(run.status or "running") == "running"
      and tostring(run.role or "") == role
      and C.dispatch_live_run_exec_ref(M, run.role, run.proposal_id, run.dedup_key) == exec_ref then
      return true
    end
  end
  return false
end

return C
