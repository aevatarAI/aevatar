local entity_lib = require("devloop.entity")
local devloop_base = require("devloop.base")
local error_facts = require("contract.error_facts")
local parsers_misc = require("devloop.parsers.misc")
local conv_rounds = require("devloop.convergence.rounds")
local m_facts = require("devloop.markers.facts")
local m_mgw = require("devloop.merge_gate_wait")
local m_rae = require("devloop.restart_actionable_epoch")
local S = {}
local convergence_shared = require("devloop.convergence.shared")
local forge_validators = require("devloop.forge_validators")
local contract_time = require("contract.time")

function S.install(M, shared)
local numeric_minutes = shared.numeric_minutes
local signal_age_from_created_at = shared.signal_age_from_created_at
local marker_attr = shared.marker_attr
local strip_liveness_timeout_suffixes = shared.strip_liveness_timeout_suffixes
local liveness_contract_signal = shared.liveness_contract_signal
local row_liveness_signal = shared.row_liveness_signal

local function newest_matching_marker_age(M, comments, family, matches, now_seconds)
  local pattern_family = tostring(family or ""):gsub("%-", "%%-")
  local marker_pattern = "<!%-%- fkst:github%-devloop:" .. pattern_family .. ":v1.-%-%->"
  local newest_age = nil
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments or {})) do
    local age = signal_age_from_created_at(M, parsers_misc._comment_created_at(M, comment), now_seconds)
    if age ~= nil and (newest_age == nil or age < newest_age) then
      for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
        if matches(marker) then
          newest_age = age
          break
        end
      end
    end
  end
  return newest_age
end

local function matching_marker_age_or_zero(M, comments, family, matches, now_seconds)
  local pattern_family = tostring(family or ""):gsub("%-", "%%-")
  local marker_pattern = "<!%-%- fkst:github%-devloop:" .. pattern_family .. ":v1.-%-%->"
  local newest_age = nil
  local found = false
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments or {})) do
    local age = signal_age_from_created_at(M, parsers_misc._comment_created_at(M, comment), now_seconds)
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      if matches(marker) then
        found = true
        if age ~= nil and (newest_age == nil or age < newest_age) then
          newest_age = age
        end
      end
    end
  end
  if found then
    return newest_age or 0
  end
  return nil
end

local function live_signal_comments(signal, facts)
  if signal and signal.surface == "pr-comment-stream" then
    return facts and facts.current_pr and facts.current_pr.comments or nil
  end
  if signal and signal.surface == "issue-comment-stream" then
    return facts and facts.current and facts.current.comments or nil
  end
  return nil
end

local function live_signal_version(M, signal, version)
  return M.liveness_heartbeat_version(version, signal)
end

local function codex_run_status(M)
  local function one_line(value)
    return error_facts.one_line(value)
  end
  local function fallback(reason)
    if type(M.log_line) == "function" then
      M.log_line("warn", "liveness", "github-devloop/codex-runs", "CODEX_RUNS", {
        "outcome=defer",
        "error_class=codex-runs-unavailable",
        "reason=" .. one_line(reason),
      })
    end
    return {
      running = {},
      recent = {},
      codex_runs_fallback = true,
      codex_runs_error = tostring(reason or "unknown"),
    }
  end
  if type(fkst) ~= "table" or type(fkst.codex_runs) ~= "function" then
    return fallback("fkst.codex_runs SDK primitive is unavailable")
  end
  local ok, status = pcall(fkst.codex_runs)
  if not ok then
    return fallback("fkst.codex_runs failed for restart liveness: " .. tostring(status))
  end
  if type(status) ~= "table" or type(status.running) ~= "table" then
    return fallback("fkst.codex_runs returned invalid restart liveness status")
  end
  return status
end

local function real_execution_expected_value(M, match, key, state, facts)
  local selector = match and match[key] or nil
  if selector == "state.proposal_id" then
    return (facts and facts.proposal_id) or (state and state.proposal_id)
  end
  if selector == "state.version" then
    return strip_liveness_timeout_suffixes(state and state.version)
  end
  return selector
end

local function timestamp_ms(M, value)
  if value == nil or value == "" then
    return nil
  end
  local seconds = contract_time.iso_timestamp_epoch_seconds(value)
  if seconds == nil then
    return nil
  end
  return seconds * 1000
end

local function timestamp_field_ms(M, run, ms_field, field)
  local direct = tonumber(run and run[ms_field])
  if direct ~= nil then
    return direct
  end
  return timestamp_ms(M, run and run[field])
end

local function run_deadline_ms(M, run)
  local lease_deadline = timestamp_field_ms(M, run, "lease_expires_at_ms", "lease_expires_at")
  if lease_deadline ~= nil then
    return lease_deadline, "lease_expires_at"
  end
  local attempt_deadline = timestamp_field_ms(M, run, "attempt_deadline_ms", "attempt_deadline")
  if attempt_deadline ~= nil then
    return attempt_deadline, "attempt_deadline"
  end
  local started_ms = timestamp_field_ms(M, run, "started_at_ms", "started_at")
  local timeout_seconds = tonumber(run and run.timeout_seconds)
  if started_ms ~= nil and timeout_seconds ~= nil and timeout_seconds > 0 then
    return started_ms + (timeout_seconds * 1000), "started_at_plus_timeout_seconds"
  end
  return nil, "missing-run-deadline"
end

local function run_matches(run, expected_role, expected_proposal_id, expected_dedup_key)
  return type(run) == "table"
    and tostring(run.role or "") == tostring(expected_role or "")
    and tostring(run.proposal_id or "") == tostring(expected_proposal_id or "")
    and tostring(run.dedup_key or "") == tostring(expected_dedup_key or "")
end

local function matching_signal(run, now_ms, collection, reason, deadline_ms, deadline_source)
  return {
    live = true,
    reason = reason,
    family = "codex_run:v1",
    resolver = "fkst.codex_runs",
    run_id = run.run_id,
    role = run.role,
    proposal_id = run.proposal_id,
    dedup_key = run.dedup_key,
    run_status = run.status,
    collection = collection,
    deadline_ms = deadline_ms,
    deadline_source = deadline_source,
    remaining_ms = deadline_ms ~= nil and now_ms ~= nil and (deadline_ms - now_ms) or nil,
  }
end

local function base_codex_run_signal(status, expected_role, expected_proposal_id, expected_dedup_key)
  return {
    live = false,
    reason = status.codex_runs_fallback and "codex-runs-unavailable" or "codex-run-not-running",
    family = "codex_run:v1",
    resolver = "fkst.codex_runs",
    expected_role = expected_role,
    expected_proposal_id = expected_proposal_id,
    expected_dedup_key = expected_dedup_key,
    codex_runs_fallback = status.codex_runs_fallback,
    codex_runs_error = status.codex_runs_error,
  }
end

local function codex_run_liveness_signal(M, row, state, facts, now_seconds)
  local real_execution = row and row.liveness_contract and row.liveness_contract.real_execution
  local match = real_execution and real_execution.match or nil
  if type(real_execution) ~= "table" or type(match) ~= "table" then
    return {
      live = false,
      reason = "missing-real-execution-contract",
      family = "codex_run:v1",
      resolver = "fkst.codex_runs",
    }
  end
  if real_execution.primitive ~= "fkst.codex_runs" then
    return {
      live = false,
      reason = "unsupported-real-execution-primitive",
      family = "codex_run:v1",
      resolver = "fkst.codex_runs",
    }
  end
  local expected_role = real_execution_expected_value(M, match, "role", state, facts)
  local expected_proposal_id = real_execution_expected_value(M, match, "proposal_id", state, facts)
  local expected_dedup_key = real_execution_expected_value(M, match, "dedup_key", state, facts)
  local expected_status = real_execution.status or "running"
  local status = codex_run_status(M)
  local now_ms = tonumber(now_seconds) and tonumber(now_seconds) * 1000 or nil
  local expired_match = nil
  local deadline_missing_match = nil
  for _, run in ipairs(status.running or {}) do
    if run_matches(run, expected_role, expected_proposal_id, expected_dedup_key)
      and tostring(run.status or "running") == tostring(expected_status) then
      local deadline_ms, deadline_source = run_deadline_ms(M, run)
      if deadline_ms ~= nil and now_ms ~= nil and now_ms < deadline_ms then
        return matching_signal(run, now_ms, "running", "codex-run-running", deadline_ms, deadline_source)
      end
      if deadline_ms == nil or now_ms == nil then
        deadline_missing_match = {
          run_id = run.run_id,
          collection = "running",
          deadline_source = now_ms == nil and "missing-now" or deadline_source,
        }
      else
        expired_match = {
          run_id = run.run_id,
          collection = "running",
          deadline_ms = deadline_ms,
          deadline_source = deadline_source,
        }
      end
    end
  end
  for _, run in ipairs(status.recent or {}) do
    if run_matches(run, expected_role, expected_proposal_id, expected_dedup_key) then
      local deadline_ms, deadline_source = run_deadline_ms(M, run)
      if deadline_ms ~= nil and now_ms ~= nil and now_ms < deadline_ms then
        return matching_signal(run, now_ms, "recent", "codex-run-recent-handoff", deadline_ms, deadline_source)
      end
      if deadline_ms ~= nil then
        expired_match = expired_match or {
          run_id = run.run_id,
          collection = "recent",
          deadline_ms = deadline_ms,
          deadline_source = deadline_source,
        }
      end
    end
  end
  local signal = base_codex_run_signal(status, expected_role, expected_proposal_id, expected_dedup_key)
  if deadline_missing_match ~= nil then
    signal.reason = "codex-run-deadline-unavailable"
    signal.run_id = deadline_missing_match.run_id
    signal.collection = deadline_missing_match.collection
    signal.deadline_source = deadline_missing_match.deadline_source
    signal.indeterminate = true
  elseif expired_match ~= nil then
    signal.reason = "codex-run-deadline-expired"
    signal.run_id = expired_match.run_id
    signal.collection = expired_match.collection
    signal.deadline_ms = expired_match.deadline_ms
    signal.deadline_source = expired_match.deadline_source
  end
  return signal
end

local function merge_gate_wait_identity(M, facts, state)
  local source_repo, source_pr = devloop_base.parse_pr_source_ref(facts and facts.source_ref)
  local pr_number = source_pr
  local head_sha = facts and facts.head_sha or nil
  if facts and facts.current_pr ~= nil then
    head_sha = facts.current_pr.head_sha or head_sha
  end
  if pr_number == nil and facts and facts.current_pr ~= nil then
    pr_number = facts.current_pr.number
  end
  if pr_number == nil and facts and facts.link ~= nil then
    pr_number = facts.link.pr_number
  end
  return (facts and facts.proposal_id) or (state and state.proposal_id),
    m_mgw.merge_gate_wait_version_lineage(M, state and state.version),
    pr_number,
    head_sha,
    source_repo
end

local function delegation_comments(facts)
  if facts and facts.current and type(facts.current.comments) == "table" then
    return facts.current.comments
  end
  if facts and facts.snapshot and type(facts.snapshot.comments) == "table" then
    return facts.snapshot.comments
  end
  return nil
end

local function fact_child_state_proposal_id(M, fact, parent_proposal_id, version)
  if type(fact) ~= "table" then
    return nil
  end
  if fact.proposal_id ~= nil and tostring(fact.proposal_id) ~= tostring(parent_proposal_id) then
    return nil
  end
  if fact.version ~= nil and tostring(fact.version) ~= tostring(version or "") then
    return nil
  end
  local child_pr_proposal_id = fact.pr_proposal_id or fact.pr_proposal
  if entity_lib.parse_pr_proposal_id(child_pr_proposal_id) == nil then
    return nil
  end
  return tostring(fact.proposal_id)
end

local function pr_delegation_child_state_proposal_id(M, facts, parent_proposal_id, delegation_version)
  local direct = facts and (facts.pr_delegation or facts["pr-delegation"]) or nil
  local child_state_proposal_id = fact_child_state_proposal_id(M, direct, parent_proposal_id, delegation_version)
  if child_state_proposal_id ~= nil then
    return child_state_proposal_id
  end
  if type(m_facts.pr_delegation_fact) ~= "function" then
    return nil
  end
  return fact_child_state_proposal_id(
    M,
    m_facts.pr_delegation_fact(M, delegation_comments(facts), parent_proposal_id, delegation_version),
    parent_proposal_id,
    delegation_version
  )
end

local function implement_attempt_liveness_signal(M, signal_contract, comments, proposal_id, signal_version, facts)
  local attempt = M.latest_implement_attempt_fact(comments, proposal_id, signal_version)
  if attempt == nil then
    return {
      live = false,
      reason = "missing-implement-attempt",
      family = signal_contract.family,
      resolver = signal_contract.resolver or signal_contract.family,
    }
  end
  if type(attempt.exec_ref) ~= "string" or attempt.exec_ref == "" then
    return {
      live = false,
      reason = "missing-exec-ref",
      attempt = attempt.attempt,
      family = signal_contract.family,
      resolver = signal_contract.resolver or signal_contract.family,
    }
  end
  if M.implement_exec_ref_running(attempt.exec_ref) then
    return {
      live = true,
      reason = "codex-run-running",
      attempt = attempt.attempt,
      exec_ref = attempt.exec_ref,
      family = signal_contract.family,
      resolver = signal_contract.resolver or signal_contract.family,
    }
  end
  local delegated_proposal_id = pr_delegation_child_state_proposal_id(M, facts, proposal_id, signal_version)
  if delegated_proposal_id ~= nil then
    return {
      live = true,
      reason = "pr-delegation-visible",
      attempt = attempt.attempt,
      exec_ref = attempt.exec_ref,
      delegated_proposal_id = delegated_proposal_id,
      family = signal_contract.family,
      resolver = signal_contract.resolver or signal_contract.family,
    }
  end
  return {
    live = false,
    reason = "codex-run-not-running",
    attempt = attempt.attempt,
    exec_ref = attempt.exec_ref,
    family = signal_contract.family,
    resolver = signal_contract.resolver or signal_contract.family,
  }
end

local function live_signal_age(M, row, state, facts, now_seconds)
  local signal = row_liveness_signal(row)
  local resolver = signal and (signal.resolver or signal.family) or nil
  local comments = live_signal_comments(signal, facts)
  local proposal_id = (facts and facts.proposal_id) or (state and state.proposal_id)
  local signal_version = live_signal_version(M, signal, state and state.version)
  if resolver == "dependency-hold" then
    local hold = M.dependency_hold_fact(comments, proposal_id)
    if hold ~= nil and tostring(hold.version or "") == tostring(signal_version or "") then
      return signal_age_from_created_at(M, hold.comment_created_at, now_seconds) or 0
    end
    return matching_marker_age_or_zero(M, comments, "dependency-wait", function(marker)
      return marker_attr(marker, "proposal") == tostring(proposal_id)
        and marker_attr(marker, "version") == tostring(signal_version or "")
    end, now_seconds) or matching_marker_age_or_zero(M, comments, "dependency-cycle", function(marker)
      return marker_attr(marker, "proposal") == tostring(proposal_id)
        and marker_attr(marker, "version") == tostring(signal_version or "")
    end, now_seconds) or matching_marker_age_or_zero(M, comments, "dependency-unresolvable", function(marker)
      return marker_attr(marker, "proposal") == tostring(proposal_id)
        and marker_attr(marker, "version") == tostring(signal_version or "")
    end, now_seconds)
  end
  if resolver == "implement-attempt" then
    return nil
  end
  if resolver == "converge-round" then
    local source_ref = facts and facts.source_ref
    local sr_digest = convergence_shared.source_ref_digest(source_ref)
    local base_version = M.version_loop_round(signal_version) > 0 and conv_rounds.converge_base_version(M, signal_version) or signal_version
    return newest_matching_marker_age(M, comments, "converge-round", function(marker)
      return marker_attr(marker, "proposal") == tostring(proposal_id)
        and marker_attr(marker, "version") == tostring(base_version)
        and marker_attr(marker, "source_ref") == tostring(sr_digest)
    end, now_seconds)
  end
  if resolver == "review-converge-round" then
    local head_sha = facts and facts.head_sha
    local review_proposal_id = facts and facts.review_proposal_id
    local source_repo, source_pr = devloop_base.parse_pr_source_ref(facts and facts.source_ref)
    if source_repo ~= nil
      and source_pr ~= nil
      and forge_validators.is_git_sha(head_sha) then
      review_proposal_id = devloop_base.pr_review_proposal_id(source_repo, source_pr, strip_liveness_timeout_suffixes(state and state.version), head_sha)
    end
    local sr_digest = convergence_shared.source_ref_digest(facts and facts.source_ref)
    return matching_marker_age_or_zero(M, comments, "review-converge-round", function(marker)
      return marker_attr(marker, "proposal") == tostring(review_proposal_id)
        and marker_attr(marker, "issue_proposal") == tostring(proposal_id)
        and marker_attr(marker, "version") == tostring(signal_version)
        and marker_attr(marker, "head_sha") == tostring(head_sha)
        and marker_attr(marker, "source_ref") == tostring(sr_digest)
    end, now_seconds)
  end
  if resolver == "merge-gate-wait" then
    local wait_proposal_id, wait_version, pr_number, head_sha = merge_gate_wait_identity(M, facts, state)
    if wait_proposal_id == nil or pr_number == nil or not forge_validators.is_git_sha(head_sha) then
      return nil
    end
    return newest_matching_marker_age(M, comments, "merge-gate-wait", function(marker)
      return marker_attr(marker, "proposal") == tostring(wait_proposal_id)
        and marker_attr(marker, "version") == tostring(wait_version)
        and marker_attr(marker, "pr") == tostring(pr_number)
        and marker_attr(marker, "head_sha") == tostring(head_sha)
    end, now_seconds)
  end
  if resolver == "child-state" then
    local child_state_proposal_id = pr_delegation_child_state_proposal_id(M, facts, proposal_id, signal_version)
    if child_state_proposal_id == nil then
      return nil
    end
    local terminal_states = {}
    for _, terminal_state in ipairs(row and row.defer and row.defer.terminal_states or {}) do
      terminal_states[tostring(terminal_state)] = true
    end
    local latest = nil
    local pattern_family = "state"
    local marker_pattern = "<!%-%- fkst:github%-devloop:" .. pattern_family .. ":v1.-%-%->"
    for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments or {})) do
      local age = signal_age_from_created_at(M, parsers_misc._comment_created_at(M, comment), now_seconds)
      for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
        if marker_attr(marker, "proposal") == tostring(child_state_proposal_id) then
          local child_state = marker_attr(marker, "state")
          if terminal_states[child_state] ~= true
            and (latest == nil or (age ~= nil and (latest.age == nil or age < latest.age))) then
            latest = {
              age = age or 0,
            }
          end
        end
      end
    end
    return latest and latest.age or nil
  end
  return nil
end

function M.restart_row_liveness_signal(row, state, facts, now_seconds)
  local contract = row and row.liveness_contract
  if type(contract) ~= "table" then
    return { live = false, reason = "missing-contract" }
  end
  if row
    and row.actionable_epoch
    and row.actionable_epoch.source == "codex_run:v1" then
    return codex_run_liveness_signal(M, row, state, facts, now_seconds)
  end
  local signal_contract = liveness_contract_signal(contract)
  if type(signal_contract) ~= "table" then
    return { live = false, reason = "no-liveness-signal" }
  end
  local resolver = signal_contract.resolver or signal_contract.family
  if resolver == "implement-attempt" then
    local comments = live_signal_comments(signal_contract, facts)
    local proposal_id = (facts and facts.proposal_id) or (state and state.proposal_id)
    local signal_version = live_signal_version(M, signal_contract, state and state.version)
    return implement_attempt_liveness_signal(M, signal_contract, comments, proposal_id, signal_version, facts)
  end
  local max_age = numeric_minutes(signal_contract.max_age_minutes)
  if max_age == nil then
    return { live = false, reason = "invalid-liveness-signal" }
  end
  local age = live_signal_age(M, row, state, facts, now_seconds)
  if age ~= nil and age < max_age then
    return {
      live = true,
      age_minutes = age,
      max_age_minutes = max_age,
      family = signal_contract.family,
      resolver = signal_contract.resolver or signal_contract.family,
    }
  end
  return {
    live = false,
    age_minutes = age,
    max_age_minutes = max_age,
    family = signal_contract.family,
    resolver = signal_contract.resolver or signal_contract.family,
  }
end

function M.restart_row_receiver_liveness(row, state, facts, now_seconds)
  if m_rae.restart_row_has_registered_actionable_epoch(M, row)
    and row
    and row.watchdog
    and row.watchdog.mode == "live-defer" then
    local eval = m_rae.actionable_epoch_resolve(M, row, state, facts, now_seconds)
    if type(facts) == "table" then
      facts.actionable_epoch_eval = eval
    end
    if eval.status == "deferred" then
      local signal = eval.signal or {
        family = row.defer and (row.defer.live_marker or row.defer.kind),
        resolver = row.actionable_epoch and row.actionable_epoch.source,
      }
      if row.actionable_epoch.source == "codex_run:v1" then
        signal.family = signal.family or "codex_run:v1"
        signal.resolver = signal.resolver or "fkst.codex_runs"
      end
      return {
        action = "defer",
        reason = "actionable-epoch-deferred",
        signal = signal,
      }
    end
    return {
      action = "stuck",
      reason = eval.status == "contract_invalid" and "actionable-epoch-contract-invalid" or "actionable-epoch-actionable",
      actionable_epoch = eval,
    }
  end
  local contract = row and row.liveness_contract
  if type(contract) ~= "table" then
    return { action = "stuck", reason = "missing-contract" }
  end
  if contract.mode == "live-defer" then
    local signal = M.restart_row_liveness_signal(row, state, facts, now_seconds)
    if signal.live then
      return {
        action = "defer",
        reason = "live-signal",
        signal = signal,
      }
    end
    return {
      action = "stuck",
      reason = "signal-stale-or-missing",
      signal = signal,
    }
  end
  if contract.mode == "row-budget-bounds-receiver" then
    local absolute_due, state_age = M.liveness_timeout_due(row, state, now_seconds)
    if absolute_due then
      return {
        action = "stuck",
        reason = "row-budget-absolute-cap",
        age_minutes = state_age,
        receiver_bound_minutes = contract.receiver_bound_minutes,
        external_wait_bound_minutes = contract.external_wait_bound_minutes,
      }
    end
    if type(contract.progress_signal) == "table" then
      local signal = M.restart_row_liveness_signal(row, state, facts, now_seconds)
      if signal.live then
        return {
          action = "defer",
          reason = "live-signal",
          signal = signal,
          receiver_bound_minutes = contract.receiver_bound_minutes,
          external_wait_bound_minutes = contract.external_wait_bound_minutes,
        }
      end
      return {
        action = "stuck",
        reason = "signal-stale-or-missing",
        signal = signal,
        receiver_bound_minutes = contract.receiver_bound_minutes,
        external_wait_bound_minutes = contract.external_wait_bound_minutes,
      }
    end
    return {
      action = "stuck",
      reason = "row-budget-bounds-receiver",
      receiver_bound_minutes = contract.receiver_bound_minutes,
      external_wait_bound_minutes = contract.external_wait_bound_minutes,
    }
  end
  return { action = "stuck", reason = "unsupported-contract" }
end
function M.restart_row_liveness_deferred(row, state, facts, now_seconds)
  return M.restart_row_receiver_liveness(row, state, facts, now_seconds).action == "defer"
end

function M.restart_row_observable_on(row, surface)
  return type(row) == "table"
    and row.terminal == false
    and type(row.observe_surfaces) == "table"
    and row.observe_surfaces[tostring(surface or "")] == true
end

function M.restart_observe_replay_due(row, surface, state, facts, now_seconds)
  if not M.restart_row_observable_on(row, surface) then
    return false
  end
  if surface == "issue" and row.from_state == "thinking" then
    return true
  end
  if surface == "liveness_scan" then
    return not M.restart_row_liveness_deferred(row, state, facts, now_seconds)
  end
  return false
end

function M.restart_observe_timeout_due(row, surface, state, facts, now_seconds)
  if type(row) ~= "table" or row.terminal == true then
    return false
  end
  if M.restart_row_liveness_deferred(row, state, facts, now_seconds) then
    return false
  end
  local due = M.liveness_timeout_due_with_facts(row, state, facts, now_seconds) == true
  if not due then
    local scan = surface == "liveness_scan" or surface == "issue_liveness_scan"
    return scan and M.liveness_timeout_decision_with_facts(row, state, facts, now_seconds).action == "redrive"
  end
  if type(row.timeout_surfaces) == "table" and row.timeout_surfaces[tostring(surface or "")] == true then
    return true
  end
  return M.liveness_timeout_decision_with_facts(row, state, facts, now_seconds).action == "escalate"
end

end

return S
