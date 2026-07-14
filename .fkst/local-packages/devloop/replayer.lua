local git_mechanics = require("devloop.git_mechanics")
local devloop_base = require("devloop.base")
local entity_lib = require("devloop.entity")
local base_ids = require("devloop.base_ids")
local requests_labels = require("devloop.requests.labels")
local requests_review = require("devloop.requests.review")
local parsers_pr = require("devloop.parsers.pr")
local payloads_builders = require("devloop.payloads.builders")
local conv_rounds = require("devloop.convergence.rounds")
local v_validate_proposal = require("devloop.validators.validate_proposal")
local m_facts = require("devloop.markers.facts")
local m_mgw = require("devloop.merge_gate_wait")
local C = {}
local convergence_shared = require("devloop.convergence.shared")
local replay_thinking_convergence = require("devloop.replay_thinking_convergence")
local replay_fields = require("devloop.replay_fields")
local forge_validators = require("devloop.forge_validators")
local transition_version = require("contract.transition_version")
local context_bundle = require("devloop.context_bundle")
local decompose_lib = require("devloop.decompose")

local skip_capture_by_core = setmetatable({}, { __mode = "k" })

local function resolve_payload_fields(M, row, state, facts)
  return replay_fields.resolve(row, state, facts or {}, entity_lib.pr_source_ref)
end

local function restart_row(M, state_name)
  return replay_fields.restart_transition_row(M.restart_transition_table(), state_name)
end

local function raise_effects(M, dept, proposal_id, apply_state, version, label_changes, effects)
  return replay_fields.replay_raise_effects(M.log_apply, M.log_raise, dept, proposal_id, apply_state, version, label_changes, effects)
end

local function find_linked_pr(snapshot, pr_number)
  for _, item in ipairs(snapshot and snapshot.prs or {}) do
    if tostring(item.number or "") == tostring(pr_number or "") then
      return item.current
    end
  end
  return nil
end

local function snapshot_with_pr_comments(current_pr)
  local snapshot = { comments = {}, prs = {} }
  for _, comment in ipairs(current_pr and current_pr.comments or {}) do
    table.insert(snapshot.comments, comment)
  end
  return snapshot
end

local function has_reviewing_marker_for_comments(M, comments, proposal_id, version)
  return M.has_state_marker(comments, proposal_id, "reviewing", version)
end

local function dept_can_direct_reviewing(dept)
  return dept ~= "observe_pr"
end

local function dept_can_direct_fixing(dept)
  return dept ~= "observe_pr"
end

local function fixing_replay_comment_request(M, issue, pr_number, fix_payload, feedback, source_ref)
  local reason = fix_payload.gate_failure_excerpt or feedback.review_reason or feedback.reason or "fixing-replay"
  local request = requests_review.build_merge_gate_fix_comment_request(M,
    issue.repo,
    issue.number,
    {
      proposal_id = fix_payload.proposal_id,
      pr_number = pr_number,
      version = fix_payload.version,
      review_proposal_id = fix_payload.review_proposal_id,
      review_dedup_key = fix_payload.review_dedup_key,
      reviewed_head_sha = fix_payload.reviewed_head_sha,
    },
    fix_payload.version,
    reason,
    fix_payload.gate_baseline_sha,
    source_ref,
    fix_payload.predecessor_set,
    {
      blocking_gap = fix_payload.blocking_gap,
      gate_failure_excerpt = fix_payload.gate_failure_excerpt,
      preserve_nil_gate_failure_excerpt = fix_payload.gate_failure_excerpt == nil,
    }
  )
  request.handoff.dedup_key = fix_payload.dedup_key
  return request
end

local function maybe_terminal_linked_pr_action(tools, dept, issue, state, proposal_id, link, current_pr, facts)
  if tools == nil or type(tools.terminal_linked_pr_action) ~= "function" then
    return nil
  end
  return tools.terminal_linked_pr_action(dept, issue, state, proposal_id, link, current_pr, facts)
end

local function snapshot_from_issue_comments(M, repo, proposal_id, comments)
  return M.linked_pr_surface_snapshot(repo, proposal_id, comments or {})
end

local function validate_required_fact(required)
  if type(required) ~= "table" or type(required.family) ~= "string" or required.family == "" then
    error("github-devloop: invalid replay required fact")
  end
  if required.freshness ~= "marker-read" and required.freshness ~= "fetch-before-compare" then
    error("github-devloop: invalid replay fact freshness")
  end
end

local function has_required_fact(row, family)
  for _, required in ipairs(row.required_facts or {}) do
    validate_required_fact(required)
    if required.family == family then
      return true
    end
  end
  return false
end

local function current_pr_fact(facts)
  local link = facts.link
  if link == nil then
    return nil
  end
  return find_linked_pr(facts.snapshot, link.pr_number)
end

local function child_pr_delegation_fact(M, facts)
  return facts.pr_delegation
    or facts["pr-delegation"]
    or m_facts.pr_delegation_fact(M, facts.snapshot.comments, facts.proposal_id, facts.state and facts.state.version)
end

local function fetch_child_state_fact(M, facts)
  if facts.child_state ~= nil then
    return facts.child_state
  end
  local delegation = child_pr_delegation_fact(M, facts)
  if delegation == nil then
    return nil
  end
  facts.pr_delegation = delegation
  facts["pr-delegation"] = delegation
  if facts.current_pr == nil then
    local view = M.fetch_pr_view_origin(facts.issue.repo, delegation.pr_number, nil, {
      force_fresh = true,
      consumer = "replay_child_state",
    })
    if view.exit_code ~= 0 then
      error("github-devloop: child-state PR view failed: " .. tostring(view.stderr))
    end
    facts.current_pr = parsers_pr.parse_pr_view_origin(M, view.stdout)
    facts.current_pr.number, facts.current_pr.force_fresh = delegation.pr_number, true
  end
  facts.child_state = require("devloop.entity").current_entity_state(M, facts.current_pr.comments, delegation.proposal_id)
  return facts.child_state
end

local function require_marker_fact(M, facts, family)
  if family == "state" then
    return facts.state
  end
  if family == "pr-link" then
    return m_facts.pr_link_fact(M, facts.snapshot.comments, facts.proposal_id) or (facts._synthetic_pr_link ~= true and facts.link or nil)
  end
  if family == "pr-delegation" then
    return child_pr_delegation_fact(M, facts)
  end
  if family == "child-state" then
    return fetch_child_state_fact(M, facts)
  end
  if family == "converge-round" then
    local base_version = M.version_loop_round(facts.state.version) > 0 and conv_rounds.converge_base_version(M, facts.state.version) or nil
    return M.latest_complete_converge_round(facts.snapshot.comments, facts.proposal_id, base_version, facts.issue.source_ref)
  end
  if family == "dependency-release" then
    return M.dependency_release_fact(facts.snapshot.comments, facts.proposal_id, facts.state.version)
  end
  if family == "dependency-wait" then
    return M.dependency_hold_fact(facts.snapshot.comments, facts.proposal_id)
  end
  if family == "review-result" then
    return m_facts.review_reject_fact(M, facts.snapshot.comments, facts.proposal_id, facts.state.version)
  end
  if family == "fix-feedback" then
    return M.fixing_replay_feedback_fact(facts.snapshot.comments, facts.proposal_id, facts.state.version)
  end
  if family == "review-meta" then
    local current_pr = current_pr_fact(facts)
    if current_pr ~= nil and forge_validators.is_git_sha(current_pr.head_sha) then
      return M.review_meta_replay_fact(facts.snapshot.comments, facts.proposal_id, facts.state.version, facts.link.pr_number, current_pr.head_sha)
    end
    return m_facts.review_meta_fix_fact(M, facts.snapshot.comments, facts.proposal_id, facts.state.version)
  end
  if family == "fix-reflection" or family == "review-converge-round" then
    local current_pr = current_pr_fact(facts)
    if current_pr == nil or not forge_validators.is_git_sha(current_pr.head_sha) then
      return nil
    end
    return M.review_meta_replay_fact(facts.snapshot.comments, facts.proposal_id, facts.state.version, facts.link.pr_number, current_pr.head_sha)
  end
  if family == "merge-gate" then
    return m_facts.merge_gate_fix_fact(M, facts.snapshot.comments, facts.proposal_id, facts.state.version)
  end
  if family == "merge-gate-wait" then
    local current_pr = current_pr_fact(facts)
    if current_pr == nil or not forge_validators.is_git_sha(current_pr.head_sha) then
      return nil
    end
    return m_mgw.merge_gate_wait_fact(M, facts.snapshot.comments, facts.proposal_id, facts.state.version, facts.link.pr_number, current_pr.head_sha)
  end
  if family == "decomposed" then
    local link = facts.link
    if link == nil then
      return nil
    end
    return decompose_lib.decomposed_fact(M, facts.snapshot.comments, facts.proposal_id, facts.state.version, link.pr_number)
  end
  if family == "implementing" then
    return m_facts.implementing_fact(M, facts.snapshot.comments, facts.proposal_id, facts.state.version)
  end
  if family == "implement-attempt" then
    local attempt_version = facts.state.version
    if facts.state.state == "implementing" then
      attempt_version = tostring(attempt_version or "")
        :gsub("/timeout/implementing/%d+$", "")
        :gsub("%-timeout%-implementing%-%d+$", "")
    end
    return M.latest_implement_attempt_fact(facts.snapshot.comments, facts.proposal_id, attempt_version)
  end
  if family == "impl-failure" then
    return M.impl_failure_fact(facts.snapshot.comments, facts.proposal_id, facts.state.version)
  end
  if family == "merge-ready" then
    local current_pr = current_pr_fact(facts)
    if current_pr == nil or not forge_validators.is_git_sha(current_pr.head_sha) then
      return nil
    end
    return m_facts.merge_ready_fact(M, facts.snapshot.comments, facts.proposal_id, facts.state.version, facts.link.pr_number, current_pr.head_sha)
  end
  if family == "merging" then
    local current_pr = current_pr_fact(facts)
    if current_pr == nil or not forge_validators.is_git_sha(current_pr.head_sha) then
      return nil
    end
    return m_facts.merging_fact(M, facts.snapshot.comments, facts.proposal_id, facts.link.pr_number, facts.state.version, current_pr.head_sha)
  end
  if family == "review-carry-over" then
    return nil
  end
  if rawget(facts, family) ~= nil then
    return rawget(facts, family)
  end
  error("github-devloop: unsupported replay marker fact family: " .. tostring(family))
end

local function gather_fetch_before_compare_fact(M, facts, entity, family)
  if family == "pr-head" then
    if facts.link ~= nil and facts.current_pr ~= nil then
      facts.snapshot = snapshot_with_pr_comments(facts.current_pr)
      for _, comment in ipairs(facts.current and facts.current.comments or {}) do table.insert(facts.snapshot.comments, comment) end
      table.insert(facts.snapshot.prs, { number = facts.link.pr_number, current = facts.current_pr })
      facts.snapshot.state = facts.state
    else
      facts.snapshot = snapshot_from_issue_comments(M, entity.repo, facts.proposal_id, facts.current and facts.current.comments or {})
      facts.link = m_facts.pr_link_fact(M, facts.snapshot.comments, facts.proposal_id)
    end
    return true
  end
  if family == "base-head" or family == "ci-status" then
    return true
  end
  if family == "decompose-children" then
    local child_list = M.gh_issue_list_decompose_children(entity.repo, facts.proposal_id, 30)
    if child_list.exit_code ~= 0 then
      error("github-devloop: gh issue decompose child list failed: " .. tostring(child_list.stderr))
    end
    facts.decompose_children = decompose_lib.parse_decompose_child_issue_list(M, child_list.stdout)
    return facts.decompose_children
  end
  if family == "branch-head" then
    return true
  end
  error("github-devloop: unsupported replay fetch-before-compare fact family: " .. tostring(family))
end

local function store_gathered_marker_fact(facts, family, value)
  facts[family] = value
  if family == "pr-link" then
    facts.link = value
  elseif family == "review-result" then
    facts.feedback = facts.feedback or value
  elseif family == "review-meta" then
    facts.review_meta = facts.review_meta or value
    facts.feedback = facts.feedback or value
  elseif family == "fix-feedback" then
    facts.fix_feedback = value
    facts.feedback = facts.feedback or value
  elseif family == "fix-reflection" or family == "review-converge-round" then
    facts.review_meta = facts.review_meta or value
  elseif family == "merge-gate" then
    facts.feedback = facts.feedback or value
  elseif family == "merge-gate-wait" then
    facts.merge_gate_wait = value
  elseif family == "decomposed" then
    facts.decomposed = value
  elseif family == "impl-failure" then
    facts.impl_failure = value
  elseif family == "merge-ready" then
    facts["merge-ready"] = value
    facts.merge_ready = value
  elseif family == "merging" then
    facts.merging = value
  elseif family == "pr-delegation" then
    facts.pr_delegation = value
    facts["pr-delegation"] = value
  elseif family == "child-state" then
    facts.child_state = value
  end
end

local function gather_required_facts(M, row, entity, state, provided)
  local gathered = {}
  for key, value in pairs(provided or {}) do
    gathered[key] = value
  end
  gathered.issue = entity
  gathered.state = state
  gathered.proposal_id = gathered.proposal_id or replay_fields.marker_value({ state = state }, "state", "proposal")

  gathered.snapshot = gathered.snapshot or { comments = gathered.current and gathered.current.comments or {}, prs = {}, state = state }
  if gathered.current_pr ~= nil and gathered.link ~= nil then
    -- current_pr.comments may be the SAME table as snapshot.comments: callers
    -- (e.g. the PR liveness sweep) pass current_pr === current and a snapshot
    -- whose comments alias current.comments. Appending into a list while
    -- iterating that same list with ipairs never terminates and allocates
    -- unboundedly. When they alias, the PR comments are already present, so the
    -- append is a no-op; only copy across when they are genuinely distinct lists.
    if gathered.current_pr.comments ~= gathered.snapshot.comments then
      for _, comment in ipairs(gathered.current_pr.comments or {}) do table.insert(gathered.snapshot.comments, comment) end
    end
    table.insert(gathered.snapshot.prs, { number = gathered.link.pr_number, current = gathered.current_pr })
  end

  for _, required in ipairs(row.required_facts or {}) do
    validate_required_fact(required)
    if required.freshness == "fetch-before-compare" then
      gather_fetch_before_compare_fact(M, gathered, entity, required.family)
    end
  end

  gathered.link = gathered.link or m_facts.pr_link_fact(M, gathered.snapshot.comments, gathered.proposal_id)

  for _, required in ipairs(row.required_facts or {}) do
    if required.freshness == "marker-read" then
      store_gathered_marker_fact(gathered, required.family, require_marker_fact(M, gathered, required.family))
    end
  end

  return gathered
end

function C.gather_replay_required_facts(M, row, entity, state, facts)
  return gather_required_facts(M, row, entity, state, facts or {})
end

local function log_skip(M, dept, proposal_id, state, from_state, to_state, outcome, reason)
  if skip_capture_by_core[M] ~= nil then
    skip_capture_by_core[M].outcome = outcome
    skip_capture_by_core[M].reason = reason
    skip_capture_by_core[M].from_state = from_state
    skip_capture_by_core[M].to_state = to_state
  end
  M.log_cas_decision(dept, proposal_id, state, from_state, to_state, outcome, reason)
  return false
end

function C.replay_log_skip(M, dept, proposal_id, state, from_state, to_state, outcome, reason)
  return log_skip(M, dept, proposal_id, state, from_state, to_state, outcome, reason)
end

local function build_thinking_replay_proposal(M, issue, proposal_id, state, current, event_ts)
  local stable_version = transition_version.strip_suffixes(state.version)
  local latest = M.latest_complete_converge_round(current.comments, proposal_id, stable_version, issue.source_ref)
  if latest ~= nil then
    local base_version = conv_rounds.converge_proposal_base_dedup(M, latest.dedup)
    local next_n = latest.round + 1
    local next_dedup = base_version .. "/loop/" .. tostring(next_n)
    local content_fetch = context_bundle.context_fetch_ref_from_bundle(M, {
      dept = "observe_issue",
      repo = issue.repo,
      issue_number = issue.number,
      proposal_id = proposal_id,
      version = next_dedup,
      tick = event_ts,
    })
    local proposal = payloads_builders.build_board_loop_proposal(M, issue.repo, issue.number, {
      title = issue.title,
      updated_at = issue.updated_at,
    }, issue.source_ref, next_n, {
      narrowed_question = latest.narrowed_question,
      angle_digests = latest.angle_digests,
    }, event_ts, content_fetch, next_dedup)
    return v_validate_proposal.validate_proposal(M, proposal) and proposal or nil
  end

  local replay_issue = {}
  for key, value in pairs(issue) do
    replay_issue[key] = value
  end
  local replay_dedup = devloop_base.proposal_dedup_key(proposal_id, issue.updated_at)
    .. "/replay"
    .. tostring(state.version or ""):sub(#stable_version + 1)
  replay_issue.content_fetch = context_bundle.context_fetch_ref_from_bundle(M, {
    dept = "observe_issue",
    repo = issue.repo,
    issue_number = issue.number,
    proposal_id = proposal_id,
    version = replay_dedup,
    tick = event_ts,
  })
  local proposal = payloads_builders.build_board_proposal(M, replay_issue, event_ts)
  proposal.dedup_key = replay_dedup
  return v_validate_proposal.validate_proposal(M, proposal) and proposal or nil
end

function C.build_thinking_replay_proposal(M, issue, proposal_id, state, current, event_ts)
  return build_thinking_replay_proposal(M, issue, proposal_id, state, current, event_ts)
end

function C.has_thinking_converge_replay(M, current, proposal_id, state, source_ref)
  if state.state ~= "thinking" then
    return false
  end
  local base_version = transition_version.strip_suffixes(state.version)
  local sr_digest = convergence_shared.source_ref_digest(source_ref)
  local facts = conv_rounds.converge_round_facts(M, current.comments, proposal_id, base_version, sr_digest)
  local round = conv_rounds.max_converge_round(M, facts)
  return M.latest_complete_converge_round(current.comments, proposal_id, base_version, source_ref) ~= nil
    or conv_rounds.is_true_stall(M, facts, round)
end

local function replay_thinking(M, dept, issue, state, row, facts)
  local proposal_id = facts.proposal_id
  local terminal = replay_thinking_convergence.replay_thinking_true_stall_blocked(M, dept, issue, state, facts, function(...)
    return log_skip(M, ...)
  end, function(...)
    return raise_effects(M, ...)
  end)
  if terminal ~= nil then
    return terminal
  end
  M.log_cas_decision(dept, proposal_id, state, "unmanaged", "thinking", "skip-idempotent(already at to_state)", "trusted thinking state marker is already visible")
  local proposal = build_thinking_replay_proposal(M, issue, proposal_id, state, facts.current, facts.event_ts)
  if proposal == nil then
    return log_skip(M, dept, proposal_id, state, row.from_state, row.driving_queue, "skip-foreign(payload)", "cannot rebuild thinking replay proposal")
  end
  M.log_cas_decision(dept, proposal_id, state, row.from_state, row.driving_queue, "applied(replay)", "replaying consensus proposal from trusted state facts")
  return raise_effects(M, dept, proposal_id, "thinking", proposal.dedup_key, { add = {}, remove = {} }, {
    { queue = "consensus.proposal", payload = proposal },
  })
end

local function replay_implementing(M, dept, issue, state, row, facts)
  local proposal_id = facts.proposal_id
  local attempt = facts["implement-attempt"]
  if attempt == nil and facts.implementing == nil then
    return log_skip(M, dept, proposal_id, state, "implementing", row.driving_queue, "skip-pending(no-implementing-fact)", "neither implement attempt nor implementing progress marker is visible")
  end
  local current_now = facts.now_seconds or now()
  local receiver_liveness = M.restart_row_receiver_liveness(row, state, facts, current_now)
  if receiver_liveness.action == "defer" then
    return log_skip(M, dept, proposal_id, state, "implementing", row.driving_queue, "skip-pending(codex-run-live)", "matching implement codex run is still running")
  end
  local decision = M.liveness_timeout_decision_with_facts(row, state, facts, current_now)
  local age, budget = tonumber(decision.age_minutes), tonumber(row.budget and row.budget.minutes)
  if attempt == nil and (budget == nil or age == nil or age < budget) then
    return log_skip(M, dept, proposal_id, state, "implementing", row.driving_queue, "skip-pending(liveness-budget)", "implementing progress marker is not over row budget")
  end
  if decision.action ~= "redrive" then
    return log_skip(M, dept, proposal_id, state, "implementing", row.driving_queue, "skip-pending(" .. tostring(decision.action or "liveness") .. ")", "implementing receiver liveness is not redriveable")
  end
  -- Pass the INNER (unwrapped) version: build_devloop_ready_payload re-applies
  -- the "ready/" wrapper, so re-wrapping the already-wrapped state.version would
  -- double-wrap it ("ready/ready/..."). Preserve the retry suffix as structured
  -- attempt metadata so re-drives reproduce frozen "ready/.../reimplement/N"
  -- markers exactly.
  local payload = payloads_builders.build_devloop_ready_payload(M, {
    proposal_id = proposal_id,
    dedup_key = M.ready_payload_inner_version(state.version),
    source_ref = issue.source_ref,
    impl_retry_attempt = M.implementation_retry_attempt(state.version),
  })
  M.log_cas_decision(dept, proposal_id, state, "implementing", "implementing", "applied(codex-run-absent)", "no matching implement codex run is running")
  return raise_effects(M, dept, proposal_id, "implementing", state.version, { add = {}, remove = {} }, {
    { queue = "devloop_ready", payload = payload },
  })
end

local function replay_impl_failed(M, dept, issue, state, row, facts)
  local proposal_id = facts.proposal_id
  local failure = facts.impl_failure
  if not M.impl_failure_retry_allowed(failure) then
    return log_skip(M, dept, proposal_id, state, "impl-failed", "implementing", "skip-idempotent(retry-limit)", "implementation failure is not a bounded codex retry candidate")
  end
  local fields = resolve_payload_fields(M, row, state, {
    issue = issue,
    state = state,
    proposal_id = proposal_id,
    ["impl-failure"] = failure,
  })
  local payload = payloads_builders.build_devloop_ready_payload(M, {
    proposal_id = fields.proposal_id,
    dedup_key = M.ready_payload_inner_version(fields.dedup_key),
    source_ref = fields.source_ref,
    impl_retry_attempt = M.next_impl_retry_attempt(failure),
  })
  M.log_cas_decision(dept, proposal_id, state, "impl-failed", "implementing", "applied(replay)", "retryable implementation failure is below the retry ceiling")
  return raise_effects(M, dept, proposal_id, nil, nil, { add = {}, remove = {} }, {
    { queue = "devloop_ready", payload = payload },
  })
end

local function replay_fixing_to_reviewing(M, dept, issue, state, proposal_id, link, current_pr, feedback, source_ref)
  local intended_head_sha = git_mechanics.current_branch_head_sha(M.git, link.branch)
  if intended_head_sha == nil then
    M.log_cas_decision(dept, proposal_id, state, "fixing", "reviewing", "retry-pending(head-advanced)", "PR head changed and deterministic branch head is not readable")
    error("github-devloop: PR head changed before fix replay and deterministic branch head is not readable")
  end
  if tostring(current_pr.head_sha or "") ~= intended_head_sha then
    return log_skip(M, dept, proposal_id, state, "fixing", "fixing", "skip-stale(head-advanced)", "PR head advanced since rejected review")
  end
  local reviewing_version = M.next_fix_version(state.version)
  local comments = (issue._replay_issue_comments ~= nil and issue._replay_issue_comments) or {}
  if has_reviewing_marker_for_comments(M, comments, proposal_id, reviewing_version)
    or has_reviewing_marker_for_comments(M, current_pr.comments, proposal_id, reviewing_version) then
    return log_skip(M, dept, proposal_id, state, "fixing", "reviewing", "skip-idempotent(reviewing marker already visible)", "reviewing state marker for recovered head is already visible")
  end
  local fix = {
    proposal_id = proposal_id,
    pr_number = link.pr_number,
    version = state.version,
    review_proposal_id = feedback.review_proposal_id,
    review_dedup_key = feedback.review_dedup_key,
    reviewed_head_sha = feedback.reviewed_head_sha,
    source_ref = source_ref,
  }
  requests_review.raise_fix_reviewing(M, {
    dept = dept,
    repo = issue.repo,
    issue_number = issue.number,
    fix = fix,
    old_head_sha = feedback.reviewed_head_sha,
    new_head_sha = current_pr.head_sha,
    new_version = reviewing_version,
    reason = "push already visible; self-healing missing reviewing marker",
    current_state = state,
  })
  return true
end

local function replay_fixing(M, tools, dept, issue, state, row, facts)
  local proposal_id = facts.proposal_id
  local link = facts.link
  if link == nil or not M.fixing_version_matches_link(state.version, link.impl_version) then
    return log_skip(M, dept, proposal_id, state, "fixing", "fixing|reviewing", "skip-foreign(pr-link)", "fixing recovery requires a same-version pr-link marker")
  end
  local current_pr = find_linked_pr(facts.snapshot, link.pr_number)
  if current_pr == nil then
    local terminal = maybe_terminal_linked_pr_action(tools, dept, issue, state, proposal_id, link, nil, facts)
    if terminal ~= nil then return terminal end
    return log_skip(M, dept, proposal_id, state, "fixing", "fixing|reviewing", "skip-foreign(pr-link)", "linked PR fact is not visible")
  end
  local terminal = maybe_terminal_linked_pr_action(tools, dept, issue, state, proposal_id, link, current_pr, facts)
  if terminal ~= nil then return terminal end
  if tostring(current_pr.state or ""):lower() ~= "open" then
    return log_skip(M, dept, proposal_id, state, "fixing", "fixing|reviewing", "skip-stale(pr-closed)", "linked PR is not open")
  end
  if not forge_validators.is_git_sha(current_pr.head_sha) then
    return log_skip(M, dept, proposal_id, state, "fixing", "fixing|reviewing", "skip-foreign(head)", "linked PR head sha is missing")
  end

  local feedback = facts.feedback or M.fixing_replay_feedback_fact(facts.snapshot.comments, proposal_id, state.version)
  if feedback ~= nil then
    if feedback.review_proposal_id == nil or feedback.reviewed_head_sha == nil then
      return log_skip(M, dept, proposal_id, state, "fixing", "fixing", "skip-foreign(fix-feedback-binding)", "trusted fix feedback marker lacks review binding")
    end
    if tostring(current_pr.head_sha or "") ~= tostring(feedback.reviewed_head_sha or "") then
      return replay_fixing_to_reviewing(M, dept, issue, state, proposal_id, link, current_pr, feedback, facts.source_ref or entity_lib.pr_source_ref(issue.repo, link.pr_number))
    end
    local reviewing_version = M.next_fix_version(state.version)
    if has_reviewing_marker_for_comments(M, facts.snapshot.comments, proposal_id, reviewing_version)
      or has_reviewing_marker_for_comments(M, current_pr.comments, proposal_id, reviewing_version) then
      return log_skip(M, dept, proposal_id, state, "fixing", "reviewing", "skip-idempotent(reviewing marker already visible)", "reviewing state marker for fix is already visible")
    end
    local fields = resolve_payload_fields(M, row, state, {
      issue = issue,
      state = state,
      link = link,
      feedback = feedback,
      proposal_id = proposal_id,
    })
    local fix_payload = payloads_builders.build_replayed_fixing_payload(M, {
      proposal_id = fields.proposal_id,
      impl_version = fields.version,
    }, fields.pr_number, feedback, fields.source_ref)
    M.log_cas_decision(dept, proposal_id, state, "fixing", "fixing", "applied(replay)", "trusted feedback fact is visible")
    if not dept_can_direct_fixing(dept) then
      local comment_request = fixing_replay_comment_request(M, issue, fields.pr_number, fix_payload, feedback, fields.source_ref)
      return raise_effects(M, dept, proposal_id, "fixing", state.version, { add = {}, remove = {} }, {
        { queue = "github-proxy.github_pr_comment_request", payload = comment_request },
      })
    end
    return raise_effects(M, dept, proposal_id, "fixing", state.version, { add = {}, remove = {} }, {
      { queue = M.pr_package_queue("devloop_fixing"), payload = fix_payload },
    })
  end

  if dept ~= "observe_pr" then
    local new_version = M.next_fix_version(state.version)
    local source_ref = entity_lib.pr_source_ref(issue.repo, link.pr_number)
    local comment_request = requests_review.build_merge_head_reviewing_comment_request(M,
      issue.repo,
      issue.number,
      {
        proposal_id = proposal_id,
        pr_number = link.pr_number,
      },
      current_pr.head_sha,
      current_pr.head_sha,
      new_version,
      source_ref
    )
    local label_request = requests_labels.build_state_label_request(M, issue.repo, issue.number, "reviewing", base_ids.dedup_key({
      "observe",
      "fixing",
      "renormalize",
      tostring(proposal_id),
      tostring(new_version),
      tostring(link.pr_number),
    }), issue.source_ref)
    M.log_cas_decision(dept, proposal_id, state, "fixing", "reviewing", "applied(replay)", "no feedback fact is visible; re-entering review for current PR head")
    return raise_effects(M, dept, proposal_id, "reviewing", new_version, { add = { "fkst-dev:reviewing" }, remove = { "fkst-dev:fixing" } }, {
      { queue = "github-proxy.github_pr_comment_request", payload = comment_request },
      { queue = "github-proxy.github_issue_label_request", payload = label_request },
    })
  end

  return log_skip(M, dept, proposal_id, state, "fixing", "fixing", "skip-stale(no-trusted-fix-feedback)", "trusted fix feedback marker is not visible")
end

local function replay_review_meta(M, tools, dept, issue, state, row, facts)
  local proposal_id = facts.proposal_id
  local link = facts.link
  if link == nil then
    return log_skip(M, dept, proposal_id, state, "review-meta", "review-meta", "skip-foreign(pr-link)", "review-meta recovery requires a pr-link marker")
  end
  local current_pr = find_linked_pr(facts.snapshot, link.pr_number)
  if current_pr == nil then
    local terminal = maybe_terminal_linked_pr_action(tools, dept, issue, state, proposal_id, link, nil, facts)
    if terminal ~= nil then return terminal end
    return log_skip(M, dept, proposal_id, state, "review-meta", "review-meta", "skip-foreign(pr-link)", "linked PR fact is not visible")
  end
  local terminal = maybe_terminal_linked_pr_action(tools, dept, issue, state, proposal_id, link, current_pr, facts)
  if terminal ~= nil then return terminal end
  if tostring(current_pr.state or ""):lower() ~= "open" then
    return log_skip(M, dept, proposal_id, state, "review-meta", "review-meta", "skip-stale(pr-closed)", "linked PR is not open")
  end
  if tostring(current_pr.head_ref_name or "") ~= tostring(link.branch or "") then
    return log_skip(M, dept, proposal_id, state, "review-meta", "review-meta", "skip-foreign(head)", "linked PR head branch does not match pr-link marker")
  end
  if tostring(current_pr.base_ref_name or "") ~= tostring(link.base_branch or "") then
    return log_skip(M, dept, proposal_id, state, "review-meta", "review-meta", "skip-foreign(base)", "linked PR base branch does not match pr-link marker")
  end
  if not forge_validators.is_git_sha(current_pr.head_sha) then
    return log_skip(M, dept, proposal_id, state, "review-meta", "review-meta", "skip-foreign(head)", "linked PR head sha is missing")
  end
  local fact = M.review_meta_replay_fact(facts.snapshot.comments, proposal_id, state.version, link.pr_number, current_pr.head_sha)
  if fact == nil then
    return log_skip(M, dept, proposal_id, state, "review-meta", "review-meta", "skip-foreign(review-meta)", "review-meta recovery facts are not visible")
  end
  local fields = resolve_payload_fields(M, row, state, {
    issue = issue,
    state = state,
    link = link,
    review_meta = fact,
    proposal_id = proposal_id,
  })
  local payload = nil
  if fact.mode == "fix-reflection" then
    payload = payloads_builders.build_devloop_fix_reflection_payload(M, fact, proposal_id, fields.version, fields.pr_number, fact.fix_round or fact.n, fields.source_ref)
    payload.blocking_gap = fact.blocking_gap
  else
    payload = payloads_builders.build_devloop_review_meta_payload(M, fact, proposal_id, fields.version, fields.pr_number, fact.n, fields.source_ref)
  end
  M.log_cas_decision(dept, proposal_id, state, "review-meta", "review-meta", "applied(replay)", "trusted review-meta fact is visible")
  return raise_effects(M, dept, proposal_id, "review-meta", state.version, { add = {}, remove = {} }, {
    { queue = M.pr_package_queue("devloop_review_meta"), payload = payload },
  })
end

local function raise_reviewing_for_current_head(M, dept, issue, state, proposal_id, link, current_pr, outcome, reason)
  if tostring(current_pr.state or ""):lower() ~= "open" then
    return log_skip(M, dept, proposal_id, state, "merge-ready", "reviewing", "skip-stale(pr-closed)", "linked PR is not open")
  end
  if not forge_validators.is_git_sha(current_pr.head_sha) then
    return log_skip(M, dept, proposal_id, state, "merge-ready", "reviewing", "skip-foreign(head)", "linked PR head sha is missing")
  end
  local reviewing_payload = payloads_builders.build_current_head_reviewing_payload(M, { repo = issue.repo, proposal_id = proposal_id }, link.pr_number, current_pr, state, entity_lib.pr_source_ref(issue.repo, link.pr_number))
  M.log_cas_decision(dept, proposal_id, state, "merge-ready", "reviewing", outcome, reason)
  if reviewing_payload == nil then
    return false
  end
  if not dept_can_direct_reviewing(dept) then
    local merge_ready = m_facts.merge_ready_fact(M, current_pr.comments, proposal_id, state.version, link.pr_number)
    local comment_request = requests_review.build_merge_head_reviewing_comment_request(M,
      issue.repo,
      issue.number,
      {
        proposal_id = proposal_id,
        pr_number = link.pr_number,
      },
      merge_ready and merge_ready.head_sha or current_pr.head_sha,
      current_pr.head_sha,
      state.version,
      entity_lib.pr_source_ref(issue.repo, link.pr_number)
    )
    return raise_effects(M, dept, proposal_id, nil, nil, { add = {}, remove = {} }, {
      { queue = "github-proxy.github_pr_comment_request", payload = comment_request },
    })
  end
  return raise_effects(M, dept, proposal_id, nil, nil, { add = {}, remove = {} }, {
    { queue = M.pr_package_queue("devloop_reviewing"), payload = reviewing_payload },
  })
end

local function maybe_replay_review_carry_over(M, dept, issue, state, row, facts, link, current_pr)
  local proposal_id = facts.proposal_id
  if state.state ~= "merge-ready" then
    return false
  end
  if tostring(current_pr.state or ""):lower() ~= "open" or not require("devloop.pr_safety").is_safe_head_sha(current_pr.head_sha) then
    return false
  end
  local carry, carry_reason = M.approved_lineage_carry_over(
    issue.repo,
    link.pr_number,
    proposal_id,
    state.version,
    facts.snapshot.comments,
    link.base_branch,
    current_pr.head_sha
  )
  if carry_reason == "missing-merge-ready-fact" or carry_reason == "head-unchanged" then
    return false
  end
  if carry_reason == "missing-review-result-approve" then
    return false
  end
  if carry == nil then
    local outcome = "skip-stale(" .. tostring(carry_reason):match("^([^:]+)") .. ")"
    return raise_reviewing_for_current_head(M, dept, issue, state, proposal_id, link, current_pr, outcome, tostring(carry_reason))
  end
  if m_facts.has_any_review_result_marker(M, current_pr.comments, carry.new_review_proposal_id, proposal_id) then
    return false
  end
  local source_ref = entity_lib.pr_source_ref(issue.repo, link.pr_number)
  local comment_request = requests_review.build_review_carry_over_comment_request(M, issue.repo, link.pr_number, proposal_id, state.version, carry, source_ref)
  M.log_cas_decision(dept, proposal_id, state, "merge-ready", "merge-ready", "applied(review-carry-over)", "resolution delta is empty")
  return raise_effects(M, dept, proposal_id, "merge-ready", state.version, { add = {}, remove = {} }, {
    { queue = "github-proxy.github_pr_comment_request", payload = comment_request },
  })
end

local function replay_merge_ready_like(M, tools, dept, issue, state, row, facts)
  local proposal_id = facts.proposal_id
  local link = facts.link
  if link == nil then
    return log_skip(M, dept, proposal_id, state, row.from_state, "merge-ready", "skip-foreign(pr-link)", "merge-ready recovery requires a pr-link marker")
  end
  local current_pr = find_linked_pr(facts.snapshot, link.pr_number)
  if current_pr == nil then
    local terminal = maybe_terminal_linked_pr_action(tools, dept, issue, state, proposal_id, link, nil, facts)
    if terminal ~= nil then return terminal end
    return log_skip(M, dept, proposal_id, state, row.from_state, "merge-ready", "skip-foreign(pr-link)", "linked PR fact is not visible")
  end
  local terminal = maybe_terminal_linked_pr_action(tools, dept, issue, state, proposal_id, link, current_pr, facts)
  if terminal ~= nil then return terminal end
  if maybe_replay_review_carry_over(M, dept, issue, state, row, facts, link, current_pr) then
    return true
  end
  local fact = m_facts.merge_ready_fact(M, facts.snapshot.comments, proposal_id, state.version, link.pr_number, current_pr.head_sha)
  if fact == nil then
    return log_skip(M, dept, proposal_id, state, row.from_state, "merge-ready", "skip-foreign(merge-ready)", "head-bound merge-ready marker is not visible")
  end
  local fields = resolve_payload_fields(M, row, state, {
    issue = issue,
    state = state,
    link = link,
    merge_ready = fact,
    ["merge-ready"] = fact,
    proposal_id = proposal_id,
  })
  local payload = payloads_builders.build_devloop_merge_ready_payload(M, fields.proposal_id, fields.pr_number, fields.version, {
    review_proposal_id = fields.review_proposal_id,
    review_dedup_key = fields.review_dedup_key,
    reviewed_head_sha = fields.reviewed_head_sha,
    current_head_sha = current_pr.head_sha,
  }, fields.source_ref)
  M.log_cas_decision(dept, proposal_id, state, row.from_state, "merge-ready", "applied(replay)", "trusted head-bound merge-ready fact is visible")
  return raise_effects(M, dept, proposal_id, nil, nil, { add = {}, remove = {} }, {
    { queue = M.pr_package_queue("devloop_merge_ready"), payload = payload },
  })
end

local function replay_blocked(M, dept, issue, state, row, facts)
  local proposal_id = facts.proposal_id
  local link = facts.link
  if link == nil then
    return log_skip(M, dept, proposal_id, state, "blocked", "decomposed", "skip-foreign(pr-link)", "decompose recovery requires a pr-link marker")
  end
  local current_pr = find_linked_pr(facts.snapshot, link.pr_number)
  local decomposed = facts.decomposed
  if decomposed == nil then
    return log_skip(M, dept, proposal_id, state, "blocked", "decomposed", "skip-foreign(decomposed)", "decomposed marker is not visible")
  end
  local complete, completed_count = decompose_lib.decompose_children_complete(M,
    nil,
    facts.decompose_children or {},
    proposal_id,
    decomposed.version,
    decomposed.pr_number,
    decomposed.count
  )
  if complete then
    return log_skip(M, dept, proposal_id, state, "blocked", "decomposed", "skip-idempotent(decomposed children already visible)", "decompose children are complete")
  end
  local fields = resolve_payload_fields(M, row, state, {
    issue = issue,
    state = state,
    link = link,
    decomposed = decomposed,
    proposal_id = proposal_id,
  })
  local payload = decompose_lib.build_decompose_replay_payload(M, decomposed, facts.fix_feedback, fields.source_ref, completed_count)
  if payload == nil then
    return log_skip(M, dept, proposal_id, state, "blocked", "decomposed", "skip-foreign(decompose-binding)", "trusted fix feedback for decomposed replay is not visible")
  end
  M.log_cas_decision(dept, proposal_id, state, "blocked", "decomposed", "applied(decomposed-children-missing)", "decomposed marker count exceeds derived child count " .. tostring(completed_count))
  local queue = type(M.decompose_package_queue) == "function" and M.decompose_package_queue() or "devloop_decompose"
  return raise_effects(M, dept, proposal_id, "blocked", state.version, { add = {}, remove = {} }, {
    { queue = queue, payload = payload },
  })
end

local function merge_replayer_registry(target, source)
  if source == nil then
    return
  end
  if type(source) ~= "table" then
    error("github-devloop: invalid restart replayer registry")
  end
  for state_name, replay in pairs(source) do
    if type(state_name) ~= "string" or state_name == "" or type(replay) ~= "function" then
      error("github-devloop: invalid restart replayer registration")
    end
    target[state_name] = replay
  end
end

local function replayer_tools(M)
  return {
    find_linked_pr = find_linked_pr,
    log_skip = function(...)
      return log_skip(M, ...)
    end,
    raise_effects = function(...)
      return raise_effects(M, ...)
    end,
    resolve_payload_fields = function(row, state, facts)
      return resolve_payload_fields(M, row, state, facts)
    end,
  }
end

local function restart_replayers(M)
  local replayers = {
    thinking = function(...)
      return replay_thinking(M, ...)
    end,
    implementing = function(...)
      return replay_implementing(M, ...)
    end,
    ["impl-failed"] = function(...)
      return replay_impl_failed(M, ...)
    end,
    blocked = function(...)
      return replay_blocked(M, ...)
    end,
  }
  merge_replayer_registry(replayers, M.replayer_registry)
  if M.replayer_review_registry ~= nil then
    local tools = replayer_tools(M)
    -- Library-owned PR-review replayers are PRE-SEEDED here (behavior-preserving): the
    -- pre-extraction hook always set these before install_pr_review_replayers ran, so the
    -- fixing / review-meta / merge-ready / merging states stay replayable (otherwise they
    -- strand at skip-foreign(replayer)). The package's review_replayers add reviewing/pr-open
    -- on top.
    replayers.fixing = function(...)
      return replay_fixing(M, tools, ...)
    end
    replayers["review-meta"] = function(...)
      return replay_review_meta(M, tools, ...)
    end
    replayers["merge-ready"] = function(...)
      return replay_merge_ready_like(M, tools, ...)
    end
    replayers.merging = function(...)
      return replay_merge_ready_like(M, tools, ...)
    end
    local review_replayers = M.replayer_review_registry
    if type(review_replayers) == "function" then
      review_replayers = review_replayers(tools)
    end
    merge_replayer_registry(replayers, review_replayers)
  end
  return replayers
end

function C.replay_from_table(M, dept, entity, state, table_row, facts)
  local row = table_row or restart_row(M, state and state.state)
  local proposal_id = facts and facts.proposal_id or nil
  if row == nil then
    return log_skip(M, dept, proposal_id, state, "unknown", "unknown", "skip-foreign(table-row)", "no restart transition table row is declared")
  end
  if type(state) ~= "table" or state.state ~= row.from_state then
    return log_skip(M, dept, proposal_id, state, row.from_state, row.driving_queue, "skip-foreign(state)", "current state does not match restart transition table row")
  end
  local replayers = restart_replayers(M)
  local replay = replayers[row.from_state]
  if replay == nil then return log_skip(M, dept, proposal_id, state, row.from_state, row.driving_queue, "skip-foreign(replayer)", "restart transition table row is not replayable by this department") end
  local replay_facts = gather_required_facts(M, row, entity, state, facts or {})
  local ok, issued = pcall(function()
    return replay(dept, entity, state, row, replay_facts)
  end)
  if not ok then error(issued) end
  return issued
end

function C.replay_from_table_classified(M, dept, entity, state, table_row, facts)
  local capture = {}
  local previous = skip_capture_by_core[M]
  skip_capture_by_core[M] = capture
  local ok, issued = pcall(function() return C.replay_from_table(M, dept, entity, state, table_row, facts) end)
  skip_capture_by_core[M] = previous
  if not ok then error(issued) end
  if issued then
    return { kind = "issued", issued = true }
  end
  return { kind = "stuck", issued = false, outcome = capture.outcome, reason = capture.reason }
end

return C
