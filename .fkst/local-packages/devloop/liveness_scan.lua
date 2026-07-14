local devloop_base = require("devloop.base")
local entity_lib = require("devloop.entity")
local base_ids = require("devloop.base_ids")
local parsers_pr = require("devloop.parsers.pr")
local parsers_issue = require("devloop.parsers.issue")
local C, replay_fields, sweep_bounds = {}, require("devloop.replay_fields"), require("devloop.sweep_bounds")
local entity_list_cache = require("devloop.entity_list_cache")

local LIVENESS_SCAN_MAX_PER_TICK = 100
local LIVENESS_SCAN_CALL_TIMEOUT = 10
local LIVENESS_SCAN_WALL_CLOCK_BUDGET = 25

function C.liveness_scan_limits(M)
  return {
    entity_cap = LIVENESS_SCAN_MAX_PER_TICK,
    call_timeout = LIVENESS_SCAN_CALL_TIMEOUT,
    wall_clock_budget = LIVENESS_SCAN_WALL_CLOCK_BUDGET,
  }
end

function C.liveness_scan_read_repo(M)
  local repo = devloop_base.read_env("FKST_GITHUB_REPO")
  if repo == nil or not base_ids.issue_ref_round_trips(repo, 1) then
    return nil
  end
  return repo
end

function C.liveness_scan_cursor_key(M, repo, prefix)
  return tostring(prefix or "github-devloop/liveness-scan/cursor/") .. base_ids.safe_repo(repo)
end

function C.liveness_scan_log_deferred(M, reason, fields)
  M.log_line("info", "liveness_scan", "github-devloop/liveness-scan", "LIVENESS_DEFERRED", {
    "reason=" .. tostring(reason or "budget"),
    "listed_issues=" .. tostring(fields and fields.listed_issues or 0),
    "listed_prs=" .. tostring(fields and fields.listed_prs or 0),
    "processed=" .. tostring(fields and fields.processed or 0),
    "deferred=" .. tostring(fields and fields.deferred or 0),
    "entity_cap=" .. tostring(fields and fields.entity_cap or 0),
  })
end

function C.liveness_scan_is_timeout_result(M, result)
  return type(result) == "table" and result.exit_code ~= 0
    and (tonumber(result.exit_code) == 124 or M.error_fact_class({ message = result.stderr }) == "timeout")
end

function C.liveness_scan_update_cursor(M, cursor_key, cursor, total, processed)
  if cursor_key == nil then
    return
  end
  cache_set(cursor_key, tostring(sweep_bounds.sweep_cursor_advance(cursor, total, processed)))
end

function C.liveness_scan_build_observe_payload(M, repo, entity, kind, tick)
  local number = tostring(entity.number or "")
  local updated_at = tostring(entity.updated_at or "")
  local source_ref = kind == "pr" and entity_lib.pr_source_ref(repo, number) or entity_lib.issue_source_ref(repo, number)
  return {
    schema = "github-proxy.v1",
    type = kind,
    repo = repo,
    number = tonumber(number), title = entity.title,
    state = entity.state,
    updated_at = updated_at,
    dedup_key = base_ids.dedup_key({
      "liveness-scan",
      tostring(repo),
      kind,
      number,
      updated_at,
      tostring(tick or ""),
    }),
    source = "liveness-scan",
    source_ref = source_ref,
  }
end

function C.liveness_scan_state_is_non_terminal(M, state)
  local row = replay_fields.restart_transition_row(M.restart_transition_table(), state and state.state)
  return row ~= nil and row.terminal ~= true
end

function C.liveness_scan_should_reinject_state(M, proposal_id, state)
  if state == nil or state.state == nil then
    M.log_cas_decision("liveness_scan", proposal_id, { state = nil, version = nil }, "tick", "observe", "skip-no-state", "no current restart state marker")
    return false
  end
  if not C.liveness_scan_state_is_non_terminal(M, state) then
    M.log_cas_decision("liveness_scan", proposal_id, state, "tick", "observe", "skip-terminal", "current restart state is terminal or unknown")
    return false
  end
  return true
end

function C.liveness_scan_issue_entity(M, repo, issue_number)
  return {
    repo = repo,
    number = issue_number,
    source_ref = entity_lib.issue_source_ref(repo, issue_number),
  }
end

function C.liveness_scan_maybe_timeout_action(M, entity, state, facts)
  local row = replay_fields.restart_transition_row(M.restart_transition_table(), state and state.state)
  if row == nil or row.terminal == true then
    return nil
  end
  local epoch = row.actionable_epoch
  if type(epoch) == "table"
    and epoch.allows_state_entry_if_never_deferred == true
    and type(facts.dependency_gate) ~= "table" then
    facts.dependency_gate = M.dependency_gate(entity and entity.repo, entity and entity.number, {
      proposal_id = facts.proposal_id or state.proposal_id,
      version = state and state.version,
      comments = facts.current and facts.current.comments,
    })
  end
  if state.state == "ready" then
    facts.dependency_gate = facts.dependency_gate or M.dependency_gate(entity and entity.repo, entity and entity.number, {
      proposal_id = facts.proposal_id or state.proposal_id,
      version = state and state.version,
      comments = facts.current and facts.current.comments,
    })
    if M.canonicalize_legacy_ready_dependency_wait("liveness_scan", entity, state, facts) then
      return "handled"
    end
  end
  local proposal_id = facts.proposal_id or state.proposal_id
  if M.restart_row_liveness_deferred(row, state, facts, facts.now_seconds or now()) then
    M.log_cas_decision("liveness_scan", proposal_id, state, row.from_state, row.driving_queue, "skip-active-output-obligation", "receiver liveness contract signal is still fresh")
    return nil
  end
  if M.maybe_timeout_redrive_from_table("liveness_scan", entity, state, row, facts) then
    return "handled"
  end
  return nil
end

function C.liveness_scan_observe_queue(M, kind)
  if kind == "pr" then
    return "devloop_observe_pr"
  end
  return "devloop_observe_issue"
end

function C.liveness_scan_list_open_issues(M, repo, timeout, poll_key)
  local list = entity_list_cache.fetch_shared_issue_observe_list(M, repo, {
    timeout = timeout or 60,
    poll_key = poll_key,
  })
  if list.exit_code ~= 0 then
    error("github-devloop: liveness-scan-issue-list-failed: " .. tostring(list.stderr))
  end
  return parsers_issue.parse_issue_list_observe(M, list.stdout)
end

function C.liveness_scan_list_open_prs(M, repo, timeout, poll_key)
  local list = entity_list_cache.fetch_shared_pr_observe_list(M, repo, {
    timeout = timeout or 60,
    poll_key = poll_key,
  })
  if list.exit_code ~= 0 then
    error("github-devloop: liveness-scan-pr-list-failed: " .. tostring(list.stderr))
  end
  return parsers_pr.parse_pr_list_observe(M, list.stdout)
end

local function sort_by_number(items)
  table.sort(items, function(left, right)
    return tonumber(left.number or 0) < tonumber(right.number or 0)
  end)
  return items
end

function C.liveness_scan_activation_slice(M, repo, kind, items, cursor_prefix)
  local activations = {}
  for _, entity in ipairs(sort_by_number(items or {})) do
    table.insert(activations, { kind = kind, entity = entity })
  end
  local total = #activations
  if total > LIVENESS_SCAN_MAX_PER_TICK then
    local cursor_key = C.liveness_scan_cursor_key(M, repo, cursor_prefix)
    local cursor = cache_get(cursor_key)
    local bounded, deferred = sweep_bounds.sweep_cursor_batch(
      activations,
      cursor,
      LIVENESS_SCAN_MAX_PER_TICK,
      LIVENESS_SCAN_MAX_PER_TICK
    )
    M.log_cas_decision("liveness_scan", "github-devloop/liveness-scan", { state = nil, version = nil }, "tick", "observe", "deferred-cap", tostring(total - LIVENESS_SCAN_MAX_PER_TICK) .. " open entities deferred by LIVENESS_SCAN_MAX_PER_TICK")
    return bounded, deferred, cursor_key, cursor, total
  end
  cache_set(C.liveness_scan_cursor_key(M, repo, cursor_prefix), "0")
  return activations, 0, nil, nil, total
end

function C.liveness_scan_reinject(M, repo, entity, kind, tick)
  local proposal_id = kind == "pr" and entity_lib.pr_proposal_id(repo, entity.number) or base_ids.proposal_id(repo, entity.number)
  local payload = C.liveness_scan_build_observe_payload(M, repo, entity, kind, tick)
  local queue = C.liveness_scan_observe_queue(M, kind)
  M.log_apply("liveness_scan", proposal_id, nil, nil, { add = {}, remove = {} }, {
    queue,
  })
  M.log_raise("liveness_scan", proposal_id, queue, payload)
end

return C
