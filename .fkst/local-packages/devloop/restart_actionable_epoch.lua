local base_ids = require("devloop.base_ids")
local parsers_misc = require("devloop.parsers.misc")
local conv_attempts = require("devloop.convergence.attempts")
local contract_time = require("contract.time")
local C = {}

local function comment_created_ms(M, comment)
  local seconds = contract_time.iso_timestamp_epoch_seconds(parsers_misc._comment_created_at(M, comment))
  if seconds == nil then
    return nil
  end
  return seconds * 1000
end

local function state_entry_ms(state)
  local seconds = contract_time.iso_timestamp_epoch_seconds(state and state.marker_created_at)
  if seconds == nil then
    return nil
  end
  return seconds * 1000
end

local function marker_attr(marker, name)
  return tostring(marker or ""):match(name .. '="([^"]*)"')
end

local function marker_family(marker_ref)
  local family = tostring(marker_ref or ""):match("^([^:]+):v%d+$")
  return family
end

local function marker_pattern(family)
  local escaped = tostring(family or ""):gsub("%-", "%%-")
  return "<!%-%- fkst:github%-devloop:" .. escaped .. ":v1.-%-%->"
end

local function live_defer_comments(row, facts)
  local signal = row and row.liveness_contract and row.liveness_contract.signal
  if signal and signal.surface == "pr-comment-stream" then
    return facts and facts.current_pr and facts.current_pr.comments or nil
  end
  return facts and facts.current and facts.current.comments or nil
end

local function signal_version(M, row, state)
  local signal = row and row.liveness_contract and row.liveness_contract.signal
  return M.liveness_heartbeat_version(state and state.version, signal)
end

local function matching_live_defer_marker(M, row, state, facts)
  local family = marker_family(row and row.defer and row.defer.live_marker)
  if family == nil then
    return nil
  end
  local comments = live_defer_comments(row, facts)
  if type(comments) ~= "table" then
    return nil
  end
  local proposal_id = (facts and facts.proposal_id) or (state and state.proposal_id)
  local version = signal_version(M, row, state)
  local newest = nil
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    local created_ms = comment_created_ms(M, comment)
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern(family)) do
      if marker_attr(marker, "proposal") == tostring(proposal_id)
        and marker_attr(marker, "version") == tostring(version or "") then
        if newest == nil or (created_ms ~= nil and newest.created_ms ~= nil and created_ms > newest.created_ms) then
          newest = {
            id = family .. ":v1:" .. tostring(proposal_id) .. ":" .. tostring(version or ""),
            family = family,
            marker = marker,
            comment_created_at = parsers_misc._comment_created_at(M, comment),
            created_ms = created_ms,
            updated_ms = created_ms,
          }
        end
      end
    end
  end
  return newest
end

local function generation_key(M, row, state, eval)
  return base_ids.dedup_key({
    "restart-liveness:v2",
    tostring((state and state.proposal_id) or ""),
    tostring(row and row.from_state or ""),
    tostring(row and row.liveness_class_id or ""),
    tostring(eval.epoch_source or ""),
    tostring(eval.generation_opened_by or ""),
    tostring(eval.epoch_ms or ""),
  })
end

local function with_generation_key(M, row, state, eval)
  eval.generation_key = generation_key(M, row, state, eval)
  return eval
end

local function actionable(M, row, state, epoch_ms, opened_by, reason)
  return with_generation_key(M, row, state, {
    status = "actionable",
    epoch_ms = epoch_ms,
    epoch_source = row.actionable_epoch.source,
    generation_opened_by = opened_by,
    reason = reason,
  })
end

local function deferred(reason)
  return {
    status = "deferred",
    reason = reason,
  }
end

local function invalid(reason)
  return {
    status = "contract_invalid",
    reason = reason,
  }
end

local function clear_fact(M, row, state, facts)
  local comments = live_defer_comments(row, facts)
  local proposal_id = (facts and facts.proposal_id) or (state and state.proposal_id)
  local version = signal_version(M, row, state)
  if row and row.defer and row.defer.clear_fact == "dependency-release:v1" then
    local fact = M.dependency_release_fact(comments, proposal_id, version)
    if fact ~= nil then
      fact.id = "dependency-release:v1:" .. tostring(proposal_id) .. ":" .. tostring(version or "")
    end
    return fact
  end
  return nil
end

local function observed_fact(M, row, state, facts)
  local comments = live_defer_comments(row, facts)
  local proposal_id = (facts and facts.proposal_id) or (state and state.proposal_id)
  if row and row.defer and row.defer.observed_fact == "dependency-wait-observed:v1" then
    local fact = M.dependency_hold_fact(comments, proposal_id)
    if fact ~= nil then
      fact.id = "dependency-wait-observed:v1:" .. tostring(proposal_id) .. ":" .. tostring(fact.version or "")
    end
    return fact
  end
  return nil
end

local function dependency_gate_fact(row, state, facts)
  if row == nil
    or row.actionable_epoch == nil
    or row.actionable_epoch.allows_state_entry_if_never_deferred ~= true
    or row.defer == nil
    or row.defer.observed_fact ~= "dependency-wait-observed:v1" then
    return nil, "unsupported-never-deferred-signal"
  end
  if type(facts) == "table" and type(facts.dependency_gate) == "table" then
    return facts.dependency_gate, nil
  end
  return nil, "dependency-gate-missing"
end

local function fact_created_ms(fact)
  local seconds = contract_time.iso_timestamp_epoch_seconds(fact and (fact.comment_created_at or fact.created_at))
  if seconds == nil then
    return nil
  end
  return seconds * 1000
end

local function resolve_state_entry(M, row, state)
  local epoch_ms = state_entry_ms(state)
  if epoch_ms == nil then
    return invalid("state entry epoch is missing")
  end
  return actionable(M, row, state, epoch_ms, "state-entry:v1:" .. tostring(state and state.version or ""), "state entry")
end

local function resolve_live_defer_epoch(M, row, state, facts, now_seconds)
  local clear = clear_fact(M, row, state, facts)
  local clear_ms = fact_created_ms(clear)
  local live = matching_live_defer_marker(M, row, state, facts)
  if live ~= nil and clear_ms ~= nil and live.updated_ms ~= nil and live.updated_ms <= clear_ms then
    live = nil
  end
  local freshness_ms = tonumber(row and row.defer and row.defer.freshness_ms)
  local now_ms = tonumber(now_seconds) and tonumber(now_seconds) * 1000 or nil
  if live ~= nil and live.updated_ms ~= nil and freshness_ms ~= nil and now_ms ~= nil then
    local stale_at = live.updated_ms + freshness_ms
    if stale_at > now_ms then
      return deferred("live defer marker fresh")
    end
    return actionable(M, row, state, stale_at, tostring(live.id) .. ":stale", "live defer marker stale")
  end
  if clear ~= nil and clear_ms ~= nil then
    return actionable(M, row, state, clear_ms, tostring(clear.id or "clear-fact"), "live defer clear fact")
  end
  local observed = observed_fact(M, row, state, facts)
  if observed == nil and row.actionable_epoch.allows_state_entry_if_never_deferred == true then
    local gate, gate_error = dependency_gate_fact(row, state, facts)
    if type(gate) ~= "table" then
      return invalid("live-defer-never-deferred-proof-missing:" .. tostring(gate_error or "dependency-gate-missing"))
    end
    if gate.ok == true then
      return resolve_state_entry(M, row, state)
    end
    return invalid("live-defer-clear-absent-after-dependency-gate:" .. tostring(gate.reason or gate.kind or "dependency-held"))
  end
  return invalid("live-defer marker absent but no durable clear fact or never-deferred proof exists")
end

local function heartbeat_freshness_minutes(row)
  local freshness_ms = tonumber(row and row.defer and row.defer.freshness_ms)
  if freshness_ms == nil or freshness_ms <= 0 then
    return nil
  end
  return freshness_ms / (60 * 1000)
end

local function resolve_live_defer_heartbeat(M, row, state, facts, now_seconds)
  if type(M.restart_row_liveness_signal) ~= "function" then
    return invalid("heartbeat liveness signal resolver is unavailable")
  end
  local signal = M.restart_row_liveness_signal(row, state, facts, now_seconds)
  local freshness_minutes = heartbeat_freshness_minutes(row)
  local now_ms = tonumber(now_seconds) and tonumber(now_seconds) * 1000 or nil
  if freshness_minutes == nil or now_ms == nil then
    return invalid("heartbeat defer freshness is invalid")
  end
  if signal.age_minutes ~= nil then
    if signal.age_minutes < freshness_minutes then
      local eval = deferred("heartbeat marker fresh")
      eval.heartbeat_age_minutes = signal.age_minutes
      return eval
    end
    local heartbeat_ms = now_ms - (signal.age_minutes * 60 * 1000)
    local stale_ms = heartbeat_ms + (freshness_minutes * 60 * 1000)
    local eval = actionable(M, row, state, stale_ms, tostring(row.defer.producer) .. ":stale", "heartbeat marker stale")
    eval.heartbeat_age_minutes = signal.age_minutes
    return eval
  end
  local entry_ms = state_entry_ms(state)
  if entry_ms == nil then
    return invalid("heartbeat marker absent and state entry epoch is missing")
  end
  local eval = actionable(M, row, state, entry_ms + (freshness_minutes * 60 * 1000), tostring(row.defer.producer) .. ":missing", "heartbeat marker missing")
  eval.heartbeat_age_minutes = nil
  return eval
end

local function resolve_codex_run(M, row, state, facts, now_seconds)
  if type(M.restart_row_liveness_signal) ~= "function" then
    return invalid("codex run liveness signal resolver is unavailable")
  end
  local signal = M.restart_row_liveness_signal(row, state, facts, now_seconds)
  if signal.live then
    local eval = deferred("codex run is still running")
    eval.signal = signal
    return eval
  end
  if signal.codex_runs_fallback == true or signal.indeterminate == true then
    local entry_ms = state_entry_ms(state)
    if entry_ms == nil then
      return invalid("codex run indeterminate epoch is missing state entry")
    end
    local now_ms = tonumber(now_seconds) and tonumber(now_seconds) * 1000 or nil
    local budget = row and row.budget and tonumber(row.budget.minutes) or nil
    if now_ms == nil or budget == nil or budget <= 0 or now_ms < entry_ms then
      return invalid("codex run indeterminate row budget is invalid")
    end
    local age = math.floor((now_ms - entry_ms) / 60000)
    if age >= budget then
      local eval = actionable(M, row, state, entry_ms, "codex-run:indeterminate", "codex run liveness indeterminate over row budget")
      eval.signal = signal
      eval.codex_runs_fallback = signal.codex_runs_fallback == true
      eval.indeterminate = signal.indeterminate == true
      return eval
    end
    local eval = deferred("codex run liveness is indeterminate")
    eval.signal = signal
    return eval
  end
  local entry_ms = state_entry_ms(state)
  if entry_ms == nil then
    return invalid("codex run fallback epoch is missing state entry")
  end
  local eval = actionable(M, row, state, entry_ms, tostring(row.defer and row.defer.producer or "codex-run") .. ":" .. tostring(signal.reason or "not-running"), "codex run not positively live")
  eval.signal = signal
  eval.codex_runs_fallback = signal.codex_runs_fallback == true
  eval.indeterminate = signal.indeterminate == true
  return eval
end

local function resolve_child_workflow_wait(M, row, state, facts, now_seconds)
  if type(M.restart_row_liveness_signal) ~= "function" then
    return invalid("child workflow liveness signal resolver is unavailable")
  end
  local signal = M.restart_row_liveness_signal(row, state, facts, now_seconds)
  if signal.live then
    local eval = deferred("child workflow state is non-terminal")
    eval.signal = signal
    return eval
  end
  local entry_ms = state_entry_ms(state)
  if entry_ms == nil then
    return invalid("child workflow wait delegation epoch is missing")
  end
  local eval = actionable(M, row, state, entry_ms, "pr-delegation:v1:" .. tostring(state and state.version or ""), "child workflow terminal or absent")
  eval.signal = signal
  return eval
end

function C.actionable_epoch_generation_key(M, row, state, eval)
  if type(eval) ~= "table" or eval.status ~= "actionable" then
    return nil
  end
  return generation_key(M, row, state, eval)
end

function C.actionable_epoch_resolve(M, row, state, facts, now_seconds)
  if type(row) ~= "table" or type(row.actionable_epoch) ~= "table" then
    return invalid("row does not declare actionable_epoch")
  end
  local sources = M.restart_liveness_epoch_sources()
  if sources[row.actionable_epoch.source] == nil then
    return invalid("unregistered actionable_epoch.source")
  end
  if row.actionable_epoch.source == "state_entry:v1" then
    return resolve_state_entry(M, row, state)
  end
  if row.actionable_epoch.source == "live_defer_epoch:v1" then
    return resolve_live_defer_epoch(M, row, state, facts, now_seconds)
  end
  if row.actionable_epoch.source == "live_defer_heartbeat:v1" then
    return resolve_live_defer_heartbeat(M, row, state, facts, now_seconds)
  end
  if row.actionable_epoch.source == "codex_run:v1" then
    return resolve_codex_run(M, row, state, facts, now_seconds)
  end
  if row.actionable_epoch.source == "child_workflow_wait:v1" then
    return resolve_child_workflow_wait(M, row, state, facts, now_seconds)
  end
  return invalid("unsupported actionable_epoch.source")
end

function C.actionable_epoch_timeout_due(M, row, state, facts, now_seconds)
  local eval = C.actionable_epoch_resolve(M, row, state, facts, now_seconds)
  if type(facts) == "table" then
    facts.actionable_epoch_eval = eval
  end
  if row.actionable_epoch.source == "live_defer_heartbeat:v1" then
    if eval.status ~= "actionable" then
      return false, eval.heartbeat_age_minutes
    end
    local now_ms = tonumber(now_seconds) and tonumber(now_seconds) * 1000 or nil
    local epoch_ms = tonumber(eval.epoch_ms)
    if now_ms == nil or epoch_ms == nil or now_ms < epoch_ms then
      return false, eval.heartbeat_age_minutes
    end
    local age = math.floor((now_ms - epoch_ms) / 60000)
    local budget = row.budget and tonumber(row.budget.minutes) or nil
    if budget == nil or age < budget then
      return false, eval.heartbeat_age_minutes or age
    end
    return true, eval.heartbeat_age_minutes or age
  end
  if row.actionable_epoch.source == "codex_run:v1" then
    if eval.status ~= "actionable" then
      return false, nil
    end
    local now_ms = tonumber(now_seconds) and tonumber(now_seconds) * 1000 or nil
    local epoch_ms = tonumber(eval.epoch_ms)
    if now_ms == nil or epoch_ms == nil or now_ms < epoch_ms then
      return false, nil
    end
    local age = math.floor((now_ms - epoch_ms) / 60000)
    local budget = row.budget and tonumber(row.budget.minutes) or nil
    if budget == nil or age < budget then
      return false, age
    end
    return true, age
  end
  if eval.status ~= "actionable" then
    return false, nil
  end
  local now_ms = tonumber(now_seconds) and tonumber(now_seconds) * 1000 or nil
  local epoch_ms = tonumber(eval.epoch_ms)
  if now_ms == nil or epoch_ms == nil or now_ms < epoch_ms then
    return false, nil
  end
  local age = math.floor((now_ms - epoch_ms) / 60000)
  local budget = row.budget and tonumber(row.budget.minutes) or nil
  if budget == nil or age < budget then
    return false, age
  end
  return true, age
end

function C.actionable_epoch_timeout_attempt(M, row, state, facts)
  local eval = facts and facts.actionable_epoch_eval
  if type(eval) ~= "table" or eval.status ~= "actionable" or eval.generation_key == nil then
    return 0
  end
  local comments = facts and facts.current and facts.current.comments or nil
  local proposal_id = (facts and facts.proposal_id) or (state and state.proposal_id)
  local current = conv_attempts.timeout_attempt_v2_round(M, comments, proposal_id, row, eval.generation_key)
  if row and row.actionable_epoch and row.actionable_epoch.source == "live_defer_heartbeat:v1" then
    return math.max(
      current,
      conv_attempts.timeout_attempt_round(M, comments, proposal_id, state and state.version, row and row.from_state) or 0,
      M.version_timeout_round(state and state.version, row and row.from_state) or 0
    )
  end
  if row and row.actionable_epoch and row.actionable_epoch.source == "codex_run:v1" then
    return math.max(
      current,
      conv_attempts.timeout_attempt_round(M, comments, proposal_id, state and state.version, row and row.from_state) or 0,
      M.version_timeout_round(state and state.version, row and row.from_state) or 0
    )
  end
  if row and row.actionable_epoch and row.actionable_epoch.source == "child_workflow_wait:v1" then
    return math.max(
      current,
      conv_attempts.timeout_attempt_round(M, comments, proposal_id, state and state.version, row and row.from_state) or 0,
      M.version_timeout_round(state and state.version, row and row.from_state) or 0
    )
  end
  if tostring(eval.generation_opened_by or ""):find("^state%-entry:v1:") then
    return math.max(current, conv_attempts.timeout_attempt_round(M, comments, proposal_id, state and state.version, row and row.from_state) or 0, M.version_timeout_round(state and state.version, row and row.from_state) or 0)
  end
  return current
end

function C.actionable_epoch_codex_run_decision(M, row, state, facts, due, age)
  local eval = facts and facts.actionable_epoch_eval
  if not (row
    and row.actionable_epoch
    and row.actionable_epoch.source == "codex_run:v1"
    and type(eval) == "table"
    and eval.status == "actionable") then
    return nil
  end
  if due then
    return nil
  end
  local attempt = M.liveness_timeout_attempt(row, state, facts)
  return {
    action = "redrive",
    attempt = attempt + 1,
    age_minutes = age,
    version = M.next_liveness_timeout_version(row, state, facts),
  }
end

function C.actionable_epoch_child_workflow_decision(M, row, state, facts, due, age)
  local eval = facts and facts.actionable_epoch_eval
  if not (row
    and row.actionable_epoch
    and row.actionable_epoch.source == "child_workflow_wait:v1"
    and type(eval) == "table"
    and eval.status == "actionable") then
    return nil
  end
  if due then
    return nil
  end
  return { action = "wait", age_minutes = age }
end

function C.actionable_epoch_heartbeat_decision(M, row, state, facts, due, age, limit)
  local eval = facts and facts.actionable_epoch_eval
  if not (row
    and row.actionable_epoch
    and row.actionable_epoch.source == "live_defer_heartbeat:v1"
    and type(eval) == "table"
    and eval.status == "actionable") then
    return nil
  end
  local missing = tostring(eval.generation_opened_by or ""):find(":missing$", 1, false) ~= nil
  if missing then
    if age == nil then return { action = "wait", age_minutes = age } end
    if due then return nil end
    local attempt = M.liveness_timeout_attempt(row, state, facts)
    return {
      action = "redrive",
      attempt = attempt + 1,
      age_minutes = age,
      version = M.next_liveness_timeout_version(row, state, facts),
    }
  end
  if not due then
    if M.liveness_timeout_attempt(row, state, facts) <= 0 then
      return {
        action = "redrive",
        attempt = 1,
        age_minutes = age,
        version = M.next_liveness_timeout_version(row, state, facts),
      }
    end
    return { action = "wait", age_minutes = age }
  end
  return {
    action = "escalate",
    attempt = limit,
    age_minutes = age,
  }
end

function C.restart_row_has_registered_actionable_epoch(M, row)
  if type(row) ~= "table" or type(row.actionable_epoch) ~= "table" then
    return false
  end
  local source = row.actionable_epoch.source
  return (source == "live_defer_epoch:v1" or source == "live_defer_heartbeat:v1" or source == "codex_run:v1" or source == "child_workflow_wait:v1")
    and M.restart_liveness_epoch_sources()[source] ~= nil
end

return C
