local base_ids = require("devloop.base_ids")
local conv_rounds = require("devloop.convergence.rounds")
local conv_reconcile = require("devloop.convergence.reconcile")
local C = {}
local transition_version = require("contract.transition_version")
local devloop_logging = require("devloop.logging")
local v_validate_proposal = require("devloop.validators.validate_proposal")

local function latest_converge_round(caps, comments, proposal_id, state_version, source_ref)
  local base_version = transition_version.strip_suffixes(state_version)
  return caps.latest_complete_converge_round(comments, proposal_id, base_version, source_ref)
end

function C.build_replay_proposal(caps, issue, proposal_id, state, current, event_ts)
  local latest = latest_converge_round(caps, current.comments, proposal_id, state.version, issue.source_ref)
  if latest ~= nil then
    local base_version = conv_rounds.converge_proposal_base_dedup(latest.dedup)
    local replay_n = latest.round + 1
    local replay_dedup = transition_version.loop_at(base_version, replay_n)
    local content_fetch = caps.context_fetch({
      dept = "observe_issue",
      repo = issue.repo,
      issue_number = issue.number,
      proposal_id = proposal_id,
      version = replay_dedup,
      tick = event_ts,
    })
    local proposal = caps.build_board_loop(issue.repo, issue.number, {
      title = issue.title,
      updated_at = issue.updated_at,
    }, issue.source_ref, replay_n, {
      narrowed_question = latest.narrowed_question,
      angle_digests = latest.angle_digests,
      findings_record = latest.findings_record,
    }, event_ts, content_fetch, replay_dedup)
    return v_validate_proposal.validate_proposal(proposal) and proposal or nil
  end

  local replay_issue = {}
  for key, value in pairs(issue) do
    replay_issue[key] = value
  end
  local replay_dedup = transition_version.strip_timeout_suffixes(state.version)
  replay_issue.content_fetch = caps.context_fetch({
    dept = "observe_issue",
    repo = issue.repo,
    issue_number = issue.number,
    proposal_id = proposal_id,
    version = replay_dedup,
    tick = event_ts,
  })
  local proposal = caps.build_board(replay_issue, event_ts)
  proposal.dedup_key = replay_dedup
  local replay_round = transition_version.loop_round(replay_dedup)
  if replay_round > 0 then
    proposal.round = replay_round
  end
  return v_validate_proposal.validate_proposal(proposal) and proposal or nil
end

function C.has_converge_replay(caps, current, proposal_id, state, source_ref)
  if state.state ~= "thinking" then
    return false
  end
  local facts = conv_rounds.converge_round_facts_for_proposal(current.comments, proposal_id)
  local round = conv_rounds.max_converge_round(facts)
  return caps.latest_complete_converge_round(current.comments, proposal_id, nil, source_ref) ~= nil
    or conv_rounds.terminal_cause(facts, round) ~= nil
end

local function visible_true_stall(M, issue, state, facts)
    local current = facts and facts.current or issue
    local proposal_id = facts and facts.proposal_id
    local source_ref = (facts and facts.source_ref) or (issue and issue.source_ref)
    if current == nil or type(state) ~= "table" or state.state ~= "thinking" or proposal_id == nil then
      return nil
    end
    local base_version = transition_version.strip_suffixes(state.version)
    local converge_facts = conv_rounds.converge_round_facts_for_proposal(current.comments, proposal_id)
    local round = conv_rounds.max_converge_round(converge_facts)
    local terminal_cause = conv_rounds.terminal_cause(converge_facts, round)
    if #converge_facts == 0 or terminal_cause == nil then
      return nil
    end
    return conv_reconcile.build_devloop_reconcile_payload({
      proposal_id = proposal_id,
      source_ref = base_ids.normalize_source_ref(source_ref),
    }, round, base_version, terminal_cause)
end

function C.replay_thinking_true_stall_blocked(M, dept, issue, state, facts, log_skip, raise_effects)
    local proposal_id = facts and facts.proposal_id
    local current = facts and facts.current or issue
    local reconcile = visible_true_stall(M, issue, state, facts)
    if reconcile == nil then
      return nil
    end
    if conv_reconcile.has_reconcile_marker(M, current.comments, proposal_id, reconcile.base_version, reconcile.round) then
      return log_skip(dept, proposal_id, state, "thinking", "blocked", "skip-idempotent(reconcile marker already visible)", "reconcile result marker for visible true-stall round is already visible")
    end
    local version = conv_reconcile.reconcile_terminal_state_version(state.version, reconcile.round)
    local transition = M.versioned_transition_status(state, { "thinking" }, "blocked", version)
    if transition == "idempotent" or transition == "stale" then
      return log_skip(dept, proposal_id, state, "thinking", "blocked", M.cas_outcome(state, transition, version), "current marker cannot be reconciled from thinking")
    end
    local action = "drop"
    local reason = tostring(reconcile.terminal_cause) .. "-after-" .. tostring(reconcile.round) .. "-rounds"
    local comment_request = M.build_reconcile_comment_request(issue.repo, issue.number, reconcile, action, reason, version)
    local label_request = M.build_reconcile_label_request(issue.repo, issue.number, reconcile)
    local add_labels, remove_labels = M.state_label_changes("blocked")
    devloop_logging.log_cas_decision(dept, proposal_id, state, "thinking", "blocked", M.cas_outcome(state, transition, version), reason)
    return raise_effects(dept, proposal_id, "blocked", version, { add = add_labels, remove = remove_labels }, {
      { queue = "github-proxy.github_issue_comment_request", payload = comment_request },
      { queue = "github-proxy.github_issue_label_request", payload = label_request },
    })
end

function C.replay(caps, dept, issue, state, row, facts, log_skip, log_defer, raise_effects)
  local proposal_id = facts.proposal_id
  local terminal = caps.replay_true_stall(dept, issue, state, facts, log_skip, raise_effects)
  if terminal ~= nil then
    return terminal
  end
  devloop_logging.log_cas_decision(dept, proposal_id, state, "unmanaged", "thinking", "skip-idempotent(already at to_state)", "trusted thinking state marker is already visible")
  local proposal = C.build_replay_proposal(caps, issue, proposal_id, state, facts.current, facts.event_ts)
  if proposal == nil then
    return log_skip(dept, proposal_id, state, row.from_state, row.driving_queue, "skip-foreign(payload)", "cannot rebuild thinking replay proposal")
  end
  if caps.dispatch_live_run("consensus", proposal_id, proposal.dedup_key, {
    state = {
      state = "thinking",
      version = proposal.dedup_key,
      proposal_id = proposal_id,
      marker_created_at = state.marker_created_at,
    },
    current = facts.current,
    proposal_id = proposal_id,
    now_seconds = facts.now_seconds or now(),
  }) then
    return log_defer(dept, proposal_id, state, row.from_state, row.driving_queue, "skip-idempotent(live-exec-ref)", "matching consensus codex run is still live")
  end
  devloop_logging.log_cas_decision(dept, proposal_id, state, row.from_state, row.driving_queue, "applied(replay)", "replaying consensus proposal from trusted state facts")
  return raise_effects(dept, proposal_id, "thinking", proposal.dedup_key, { add = {}, remove = {} }, {
    { queue = "consensus.proposal", payload = proposal },
  })
end

return C
