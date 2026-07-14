local devloop_base = require("devloop.base")
local base_ids = require("devloop.base_ids")
local strings = require("contract.strings")
local conv_reconcile = require("devloop.convergence.reconcile")
local conv_attempts = require("devloop.convergence.attempts")
local m_rae = require("devloop.restart_actionable_epoch")
local S = {}
local contract_time = require("contract.time")
local source_refs = require("contract.source_ref")
local replay_fields = require("devloop.replay_fields")
local replayer = require("devloop.replayer")

function S.install(M, shared)
local max_timeout_attempts = shared.max_timeout_attempts
local numeric_minutes = shared.numeric_minutes
local row_liveness_signal = shared.row_liveness_signal

function M.liveness_budget_minutes(state_name)
  local row = replay_fields.restart_transition_row(M.restart_transition_table(), state_name)
  return row and row.budget and tonumber(row.budget.minutes) or nil
end

function M.liveness_state_age_minutes(state, now_seconds)
  if type(state) ~= "table" then
    return nil
  end
  if state.marker_created_at ~= nil and state.marker_created_at ~= "" then
    local created_seconds = contract_time.iso_timestamp_epoch_seconds(state.marker_created_at)
    local current_seconds = tonumber(now_seconds)
    if created_seconds ~= nil and current_seconds ~= nil and current_seconds >= created_seconds then
      return math.floor((current_seconds - created_seconds) / 60)
    end
  end
  return M.stall_suspect_age_minutes(state.version, now_seconds)
end

function M.liveness_timeout_attempt(row, state, facts)
  local eval = facts and facts.actionable_epoch_eval
  if m_rae.restart_row_has_registered_actionable_epoch(M, row) then
    return m_rae.actionable_epoch_timeout_attempt(M, row, state, facts)
  end
  local proposal_id = (facts and facts.proposal_id) or (state and state.proposal_id)
  local comments = facts and facts.current and facts.current.comments or nil
  local from_state = row and row.from_state
  local version = state and state.version
  local durable_round = conv_attempts.timeout_attempt_round(M, comments, proposal_id, version, from_state)
  local version_round = M.version_timeout_round(version, from_state)
  return math.max(durable_round or 0, version_round or 0)
end

function M.next_liveness_timeout_version(row, state, facts)
  local from = tostring(row.from_state)
  local escaped = from:gsub("%-", "%%-")
  local base = tostring(state and state.version or "")
  -- Replace, not stack, the trailing timeout segment for this state so the version
  -- stays bounded as attempts climb: V -> V/timeout/<state>/1 -> V/timeout/<state>/2.
  -- The attempt count itself is read from the full (pre-strip) version, so it keeps
  -- advancing across sweeps even though the suffix never accumulates.
  local previous = nil
  while previous ~= base do
    previous = base
    base = base:gsub("/timeout/" .. escaped .. "/%d+$", "")
  end
  return base .. "/timeout/" .. from .. "/" .. tostring(M.liveness_timeout_attempt(row, state, facts) + 1)
end

function M.liveness_timeout_due(row, state, now_seconds)
  if row == nil or row.terminal == true then
    return false, nil
  end
  local budget = row.budget and tonumber(row.budget.minutes) or nil
  local age = M.liveness_state_age_minutes(state, now_seconds)
  if budget == nil or age == nil or age < budget then
    return false, age
  end
  return true, age
end

local function live_signal_max_age(row)
  return numeric_minutes(row_liveness_signal(row) and row_liveness_signal(row).max_age_minutes)
end

function M.liveness_timeout_due_with_facts(row, state, facts, now_seconds)
  if row == nil or row.terminal == true then
    return false, nil
  end
  if m_rae.restart_row_has_registered_actionable_epoch(M, row) then
    return m_rae.actionable_epoch_timeout_due(M, row, state, facts, now_seconds)
  end
  local contract = row.liveness_contract
  if type(contract) == "table" and contract.mode == "row-budget-bounds-receiver" then
    return M.liveness_timeout_due(row, state, now_seconds)
  end
  local signal_max_age = live_signal_max_age(row)
  if signal_max_age ~= nil then
    local signal = M.restart_row_liveness_signal(row, state, facts, now_seconds)
    if signal.age_minutes ~= nil then
      if signal.age_minutes < signal_max_age then
        return false, signal.age_minutes
      end
      return true, signal.age_minutes
    end
  end
  return M.liveness_timeout_due(row, state, now_seconds)
end

local function timeout_escalation(row, state, age, facts)
  local attempt = M.liveness_timeout_attempt(row, state, facts)
  local limit = tonumber(row.on_timeout and row.on_timeout.escalate_after_attempts) or max_timeout_attempts
  local next_version = M.next_liveness_timeout_version(row, state, facts)
  if attempt >= limit then
    return {
      action = "escalate",
      attempt = attempt,
      age_minutes = age,
    }
  end
  if attempt + 1 >= limit then
    return {
      action = "escalate",
      attempt = attempt + 1,
      age_minutes = age,
    }
  end
  return {
    action = "redrive",
    attempt = attempt + 1,
    age_minutes = age,
    version = next_version,
  }
end

local function build_timeout_reconcile(row, entity, state, facts, decision)
  local source_ref = (facts and facts.source_ref) or (entity and entity.source_ref) or (state and state.source_ref)
  local proposal_id = (facts and facts.proposal_id) or (state and state.proposal_id)
  if source_refs.has_bounded_source_ref(source_ref, M._max_key_len)
    and strings.is_path_safe_key(proposal_id, M._max_key_len)
    and strings.is_bounded_string(state and state.version, M._max_dedup_len) then
    return "devloop_timeout_reconcile", conv_reconcile.build_devloop_timeout_reconcile_payload(M, row, state, proposal_id, source_ref, decision.attempt)
  end
  return nil, nil
end

function M.build_liveness_timeout_reconcile_payload(row, entity, state, facts, decision)
  return build_timeout_reconcile(row, entity, state, facts, decision)
end

function M.liveness_timeout_decision(row, state, now_seconds)
  local due, age = M.liveness_timeout_due(row, state, now_seconds)
  if not due then
    return {
      action = "wait",
      age_minutes = age,
    }
  end
  return timeout_escalation(row, state, age)
end

function M.liveness_timeout_decision_with_facts(row, state, facts, now_seconds)
  local due, age = M.liveness_timeout_due_with_facts(row, state, facts, now_seconds)
  local limit = tonumber(row and row.on_timeout and row.on_timeout.escalate_after_attempts) or max_timeout_attempts
  local heartbeat = m_rae.actionable_epoch_heartbeat_decision(M, row, state, facts, due, age, limit)
  if heartbeat ~= nil then return heartbeat end
  local codex_run = m_rae.actionable_epoch_codex_run_decision(M, row, state, facts, due, age)
  if codex_run ~= nil then return codex_run end
  local child_workflow = m_rae.actionable_epoch_child_workflow_decision(M, row, state, facts, due, age)
  if child_workflow ~= nil then return child_workflow end
  if not due then
    return { action = "wait", age_minutes = age }
  end
  return timeout_escalation(row, state, age, facts)
end

local function timeout_attempt_target(entity, facts)
  local source_ref = (facts and facts.source_ref) or (entity and entity.source_ref)
  local kind = "issue"
  local repo = entity and entity.repo
  local number = entity and entity.number
  local _, pr_number = devloop_base.parse_pr_source_ref(source_ref)
  if pr_number ~= nil then
    local parsed_repo = select(1, base_ids.parse_proposal_id(facts and facts.proposal_id))
    kind = "pr"
    repo = parsed_repo or repo
    number = pr_number
  end
  if kind == "issue" then
    local parsed_repo, issue_number = base_ids.parse_proposal_id(facts and facts.proposal_id)
    repo = parsed_repo or repo
    number = issue_number or number
  end
  if repo == nil or number == nil then
    return nil
  end
  return {
    kind = kind,
    repo = repo,
    number = number,
  }
end

local function emit_timeout_attempt_marker(dept, entity, state, row, facts, proposal_id, attempt)
  local target = timeout_attempt_target(entity, facts)
  local source_ref = (facts and facts.source_ref) or (entity and entity.source_ref) or (state and state.source_ref)
  if target ~= nil then
    local eval = facts and facts.actionable_epoch_eval
    if m_rae.restart_row_has_registered_actionable_epoch(M, row)
      and type(eval) == "table"
      and eval.status == "actionable"
      and eval.generation_key ~= nil then
      M.log_raise(dept, proposal_id, target.kind == "pr" and "github-proxy.github_pr_comment_request" or "github-proxy.github_issue_comment_request", conv_attempts.build_timeout_attempt_v2_comment_request(M, target, proposal_id, state, row, source_ref, attempt, eval.generation_key))
    else
      M.log_raise(dept, proposal_id, target.kind == "pr" and "github-proxy.github_pr_comment_request" or "github-proxy.github_issue_comment_request", conv_attempts.build_timeout_attempt_comment_request(M, target, proposal_id, state, row, source_ref, attempt))
    end
  end
end

local function emit_decompose_exhausted_marker(dept, entity, state, facts, proposal_id, attempt)
  local target = timeout_attempt_target(entity, facts)
  local source_ref = (facts and facts.source_ref) or (entity and entity.source_ref) or (state and state.source_ref)
  if target ~= nil then
    local request = conv_attempts.build_decompose_exhausted_comment_request(M, target, proposal_id, state, source_ref, attempt)
    M.log_apply(dept, proposal_id, nil, nil, { add = {}, remove = {} }, {
      target.kind == "pr" and "github-proxy.github_pr_comment_request" or "github-proxy.github_issue_comment_request",
    })
    M.log_raise(dept, proposal_id, target.kind == "pr" and "github-proxy.github_pr_comment_request" or "github-proxy.github_issue_comment_request", request)
    return true
  end
  return false
end

function M.maybe_timeout_redrive_from_table(dept, entity, state, table_row, facts)
  local row = table_row or replay_fields.restart_transition_row(M.restart_transition_table(), state and state.state)
  if row == nil or row.terminal == true then
    return false
  end
  local comments = facts and facts.current and facts.current.comments or nil
  local proposal_id = facts and facts.proposal_id or state and state.proposal_id
  local matches, mismatch = M.timeout_lineage_matches_current(state, facts and facts.fresh_current_state)
  if not matches then
    M.log_cas_decision(dept, proposal_id, facts and facts.fresh_current_state or state, row.from_state, row.driving_queue, "stale_timeout_noop(" .. tostring(mismatch) .. ")", "timeout watchdog lineage no longer matches freshly derived current state")
    return true
  end
  if row.from_state == "blocked" and conv_attempts.has_decompose_exhausted_marker(M, comments, proposal_id, state and state.version) then
    M.log_cas_decision(dept, proposal_id, state, "blocked", row.driving_queue, "skip-idempotent(decompose-exhausted)", "blocked decompose output obligation already reached terminal stop")
    return true
  end
  local receiver_liveness = M.restart_row_receiver_liveness(row, state, facts, (facts and facts.now_seconds) or now())
  if receiver_liveness.action == "defer" then
    local signal = receiver_liveness.signal or {}
    local reason = signal.family == "codex_run:v1"
      and "deferred: receiver still executing"
      or "receiver liveness contract signal is still fresh"
    M.log_cas_decision(dept, proposal_id, state, row.from_state, row.driving_queue, "skip-timeout-count(live-signal:" .. tostring(signal.family or "unknown") .. ")", reason)
    return true
  end
  local decision = M.liveness_timeout_decision_with_facts(row, state, facts, (facts and facts.now_seconds) or now())
  if decision.action == "wait" then
    return false
  end
  M.log_cas_decision(dept, proposal_id, state, row.from_state, row.driving_queue, "timeout-" .. decision.action, "state output obligation exceeded budget")
  if decision.action == "escalate" then
    if row.from_state == "blocked" then
      return emit_decompose_exhausted_marker(dept, entity, state, facts, proposal_id, decision.attempt)
    end
    local queue, payload = build_timeout_reconcile(row, entity, state, facts, decision)
    if queue ~= nil then
      M.log_apply(dept, proposal_id, nil, nil, { add = {}, remove = {} }, { queue })
      M.log_raise(dept, proposal_id, queue, payload)
      return true
    end
    return false
  end
  local replay = replayer.replay_from_table_classified(M, dept, entity, {
    state = state.state,
    version = state.version,
    proposal_id = state.proposal_id,
    stage_rank = state.stage_rank,
    marker_created_at = state.marker_created_at,
  }, row, facts)
  if replay.kind == "stuck" then
    M.log_cas_decision(dept, proposal_id, state, row.from_state, row.driving_queue, "timeout-stuck(" .. tostring(replay.outcome or "replay-declined") .. ")", "state output obligation is unmet and replay did not emit a consumable redrive")
  end
  if replay.kind == "issued" or replay.kind == "stuck" then
    emit_timeout_attempt_marker(dept, entity, state, row, facts, proposal_id, decision.attempt)
    return true
  end
  return false
end

end

return S
