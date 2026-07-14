local entity_lib = require("devloop.entity")
local base_ids = require("devloop.base_ids")
local m_claims = require("devloop.claims")
local requests_labels = require("devloop.requests.labels")
local requests_lifecycle = require("devloop.requests.lifecycle")
local payloads_builders = require("devloop.payloads.builders")
local conv_attempts = require("devloop.convergence.attempts")
local S = {}
local operator_commands = require("devloop.operator_commands")
local replay_fields_resolver = require("devloop.replay_fields")
local comment_strings = require("devloop.strings")

function S.install(M)

local dependency_gate_rederive = true

function M.build_ready_split_canonicalized_comment_request(repo, issue_number, proposal_id, from_version, to_state, to_version, gate, source_ref)
  local state_effects = to_state == "ready" and "result-marker,ready-label,devloop-ready" or "ready-split-canonicalized"
  local markers = M.ready_split_canonicalized_marker(proposal_id, from_version, to_version, to_state, gate and gate.reason or "ready_split_rederive")
    .. "\n" .. M.state_marker(proposal_id, to_state, to_version, state_effects)
  if to_state == "dependency_wait" then
    markers = markers .. "\n" .. M.dependency_wait_marker(proposal_id, to_version, gate and gate.unmet or {}, gate and gate.kind or "waiting", gate and gate.reason or "waiting-on-dependency")
  end
  local request = m_claims.attach_issue_claim({
    schema = "github-proxy.v1",
    repo = repo,
    issue_number = issue_number,
    body = "github-devloop ready split canonicalized"
      .. "\n\n" .. comment_strings.comment_string(M, "reason_inline_label") .. tostring(gate and gate.reason or "ready_split_rederive")
      .. "\n\n" .. markers,
    dedup_key = base_ids.dedup_key({ "ready-split", "canonicalized", tostring(proposal_id), tostring(from_version), tostring(to_version) }),
    source_ref = base_ids.normalize_source_ref(source_ref),
  }, source_ref)
  if to_state == "ready" then
    request.handoff = {
      kind = "github-devloop.ready",
      proposal_id = proposal_id,
      version = to_version,
      marker_version = to_version,
      source_ref = base_ids.normalize_source_ref(source_ref),
    }
  end
  return request
end

function M.canonicalize_legacy_ready_dependency_wait(dept, issue, state, facts)
  if type(state) ~= "table" or state.state ~= "ready" then
    return false
  end
  local proposal_id = facts and facts.proposal_id or state.proposal_id
  local current = facts and facts.current
  local comments = current and current.comments
  if proposal_id == nil or type(comments) ~= "table" then
    return false
  end
  if M.ready_split_canonicalized_fact(comments, proposal_id, state.version) ~= nil then
    return false
  end
  if M.dependency_hold_fact(comments, proposal_id) == nil then
    return false
  end
  local gate = facts.dependency_gate or M.dependency_gate(issue.repo, issue.number, {
    proposal_id = proposal_id,
    version = state.version,
    comments = comments,
  })
  local to_state = gate.ok and "ready" or "dependency_wait"
  local to_version = M.ready_split_version(state.version)
  local raised = { "github-proxy.github_issue_comment_request" }
  local add_labels = {}
  local remove_labels = {}
  if to_state == "dependency_wait" then
    add_labels = { M._blocked_on_dependency_label }
    table.insert(raised, "github-proxy.github_issue_label_request")
  else
    remove_labels = { M._blocked_on_dependency_label }
    if M.has_label(current.labels, M._blocked_on_dependency_label) then
      table.insert(raised, "github-proxy.github_issue_label_request")
    end
  end
  M.log_cas_decision(dept, proposal_id, state, "ready", to_state, "applied(ready-split-canonicalized)", gate.reason or "ready_split_rederive")
  M.log_apply(dept, proposal_id, to_state, to_version, { add = add_labels, remove = remove_labels }, raised)
  M.log_raise(dept, proposal_id, "github-proxy.github_issue_comment_request", M.build_ready_split_canonicalized_comment_request(
    issue.repo,
    issue.number,
    proposal_id,
    state.version,
    to_state,
    to_version,
    gate,
    issue.source_ref
  ))
  if to_state == "dependency_wait" then
    M.log_raise(dept, proposal_id, "github-proxy.github_issue_label_request", requests_labels.build_label_request(M,
      issue.repo,
      issue.number,
      { M._blocked_on_dependency_label },
      {},
      base_ids.dedup_key({ "dependency", "label", "hold", tostring(proposal_id), tostring(to_version), tostring(gate.kind) }),
      issue.source_ref
    ))
    return true
  end
  if M.has_label(current.labels, M._blocked_on_dependency_label) then
    M.log_raise(dept, proposal_id, "github-proxy.github_issue_label_request", requests_labels.build_label_request(M,
      issue.repo,
      issue.number,
      {},
      { M._blocked_on_dependency_label },
      base_ids.dedup_key({ "dependency", "label", "clear", tostring(proposal_id), tostring(to_version) }),
      issue.source_ref
    ))
  end
  return true
end

local function replay_fields(M, row, state, issue, proposal_id)
  return replay_fields_resolver.resolve(row, state, {
    issue = issue,
    state = state,
    proposal_id = proposal_id,
  }, entity_lib.pr_source_ref)
end

local function read_fact(facts, family)
  if type(facts) ~= "table" then
    return nil
  end
  local direct = facts[family]
  if direct ~= nil then
    return direct
  end
  return facts[tostring(family or ""):gsub("%-", "_")]
end

local function dependency_gate_fact(M, dept, proposal_id, state, facts)
  local gate = read_fact(facts, "dependency-gate")
  if gate ~= nil then
    return gate
  end
  M.log_cas_decision(dept, proposal_id, state, state.state, state.state, "skip-pending(dependency-gate-missing)", "declared dependency-gate fact is not visible")
  return nil
end

function M.replay_row_and_facts_with_declared_dependency_gate(issue, proposal_id, state, current, command)
  local row = replay_fields_resolver.restart_transition_row(M.restart_transition_table(), state.state)
  local facts = { proposal_id = proposal_id, current = current, command = command }
  for _, advancing_fact in ipairs(row and row.advancing_facts or {}) do
    if advancing_fact.fact_family == "dependency-gate" then
      facts.dependency_gate = M.dependency_gate(issue.repo, issue.number, {
        proposal_id = proposal_id, version = state.version, comments = current.comments,
      })
      break
    end
  end
  return row, facts
end

local function raise_dependency_release(M, dept, issue, proposal_id, state, current, command_comment_request, gate, release_fact)
  local ready_version = M.ready_split_version(state.version)
  local raised = { "github-proxy.github_issue_comment_request" }
  local has_blocked_label = M.has_label(current.labels, M._blocked_on_dependency_label)
  if release_fact == nil then table.insert(raised, "github-proxy.github_issue_comment_request") end
  if command_comment_request ~= nil then table.insert(raised, "github-proxy.github_issue_comment_request") end
  if has_blocked_label then table.insert(raised, "github-proxy.github_issue_label_request") end
  M.log_apply(dept, proposal_id, "ready", ready_version, { add = {}, remove = { M._blocked_on_dependency_label } }, raised)
  M.log_raise(dept, proposal_id, "github-proxy.github_issue_comment_request", M.build_ready_split_canonicalized_comment_request(
    issue.repo, issue.number, proposal_id, state.version, "ready", ready_version, gate, issue.source_ref
  ))
  if command_comment_request ~= nil then
    M.log_raise(dept, proposal_id, "github-proxy.github_issue_comment_request", command_comment_request)
  end
  if release_fact == nil then
    M.log_raise(dept, proposal_id, "github-proxy.github_issue_comment_request", requests_lifecycle.build_dependency_release_comment_request(M,
      issue.repo, issue.number, proposal_id, state.version, gate, issue.source_ref
    ))
  end
  if has_blocked_label then
    M.log_raise(dept, proposal_id, "github-proxy.github_issue_label_request", requests_labels.build_label_request(M,
      issue.repo, issue.number, {}, { M._blocked_on_dependency_label },
      base_ids.dedup_key({ "dependency", "label", "clear", tostring(proposal_id), tostring(state.version) }), issue.source_ref
    ))
  end
  return true
end

local function raise_dependency_wait_hold(M, dept, issue, proposal_id, state, current, gate, command, dependency_hold)
  local marker = gate.kind == "cycle"
    and M.dependency_cycle_marker(proposal_id, state.version)
    or (gate.kind == "unresolvable"
      and M.dependency_unresolvable_marker(proposal_id, state.version, gate.unmet, gate.kind, gate.reason)
      or M.dependency_wait_marker(proposal_id, state.version, gate.unmet, gate.kind, gate.reason))
  M.log_cas_decision(dept, proposal_id, state, "dependency_wait", "dependency_wait", "retry-pending(dependency-hold)", gate.reason)
  local raised = {}
  if dependency_hold == nil then
    table.insert(raised, "github-proxy.github_issue_comment_request")
    table.insert(raised, "github-proxy.github_issue_label_request")
  end
  local command_comment_request = nil
  if command ~= nil then
    command_comment_request = operator_commands.build_operator_issue_reready_comment_request(M, issue.repo, issue.number, command, "dependency-hold", issue.source_ref)
    table.insert(raised, "github-proxy.github_issue_comment_request")
  end
  if #raised > 0 then
    M.log_apply(dept, proposal_id, "dependency_wait", state.version, { add = { M._blocked_on_dependency_label }, remove = {} }, raised)
  end
  if command_comment_request ~= nil then
    M.log_raise(dept, proposal_id, "github-proxy.github_issue_comment_request", command_comment_request)
  end
  if dependency_hold == nil then
    M.log_raise(dept, proposal_id, "github-proxy.github_issue_comment_request", requests_lifecycle.build_dependency_hold_comment_request(M, issue.repo, issue.number, proposal_id, state.version, gate, marker, issue.source_ref))
    M.log_raise(dept, proposal_id, "github-proxy.github_issue_label_request", requests_labels.build_label_request(M,
      issue.repo, issue.number, { M._blocked_on_dependency_label }, {},
      base_ids.dedup_key({ "dependency", "label", "hold", tostring(proposal_id), tostring(state.version), tostring(gate.kind) }), issue.source_ref
    ))
  end
  return #raised > 0
end

local function raise_dependency_gate_blocked(M, dept, issue, proposal_id, state, gate)
  local add_labels, remove_labels = M.state_label_changes("blocked")
  table.insert(remove_labels, M._blocked_on_dependency_label)
  local comment_request = m_claims.attach_issue_claim({
    schema = "github-proxy.v1",
    repo = issue.repo,
    issue_number = issue.number,
    body = "github-devloop dependency gate blocked"
      .. "\n\n" .. comment_strings.comment_string(M, "reason_block_label") .. "\n" .. tostring(gate.reason or "dependency-gate-unresolvable")
      .. "\n\n" .. M.state_marker(proposal_id, "blocked", state.version),
    dedup_key = base_ids.dedup_key({ "dependency", "blocked", tostring(proposal_id), tostring(state.version), tostring(gate.kind), tostring(gate.reason) }),
    source_ref = base_ids.normalize_source_ref(issue.source_ref),
  }, issue.source_ref)
  local label_request = requests_labels.build_label_request(M,
    issue.repo,
    issue.number,
    add_labels,
    remove_labels,
    base_ids.dedup_key({ "dependency", "blocked", "label", tostring(proposal_id), tostring(state.version), tostring(gate.kind), tostring(gate.reason) }),
    issue.source_ref
  )
  M.log_cas_decision(dept, proposal_id, state, "dependency_wait", "blocked", "applied(dependency-gate-unresolvable)", gate.reason)
  return replay_fields_resolver.replay_raise_effects(M.log_apply, M.log_raise, dept, proposal_id, "blocked", state.version, { add = add_labels, remove = remove_labels }, {
    { queue = "github-proxy.github_issue_comment_request", payload = comment_request },
    { queue = "github-proxy.github_issue_label_request", payload = label_request },
  })
end

function M.replay_dependency_wait_state(dept, issue, state, row, facts)
  local proposal_id = facts.proposal_id
  local gate = dependency_gate_fact(M, dept, proposal_id, state, facts)
  if gate == nil then
    return false
  end
  if gate.kind == "unresolvable" or gate.kind == "cycle" then
    return raise_dependency_gate_blocked(M, dept, issue, proposal_id, state, gate)
  end
  if not gate.ok then
    return raise_dependency_wait_hold(M, dept, issue, proposal_id, state, facts.current, gate, facts.command, read_fact(facts, "dependency-wait"))
  end
  M.log_cas_decision(dept, proposal_id, state, "dependency_wait", "ready", "release-dependency-hold", gate.reason)
  local command_comment_request = facts.command_comment_request or (facts.command ~= nil
    and operator_commands.build_operator_issue_reready_comment_request(M, issue.repo, issue.number, facts.command, "dependency-release", issue.source_ref)
    or nil)
  return raise_dependency_release(M, dept, issue, proposal_id, state, facts.current, command_comment_request, gate, read_fact(facts, "dependency-release"))
end

local function next_ready_redrive_version(marker_version, round)
  return tostring(marker_version or "") .. "/redrive/ready/" .. tostring(round)
end

local function ready_redrive_round(M, comments, proposal_id, marker_version, row)
  local timeout_round = conv_attempts.timeout_attempt_round(M,
    comments,
    proposal_id,
    marker_version,
    row.from_state
  ) or 0
  local command_round = operator_commands.operator_command_response_count(M, comments, "reready", "applied", "ready")
  return math.max(timeout_round, command_round) + 1
end

function M.replay_ready_state(dept, issue, state, row, facts)
  local proposal_id = facts.proposal_id
  local fields = replay_fields(M, row, state, issue, proposal_id)
  local gate = dependency_gate_fact(M, dept, proposal_id, state, facts)
  if gate == nil then
    return false
  end
  if not gate.ok then
    local dep_version = M.ready_split_version(state.version)
    M.log_cas_decision(dept, proposal_id, state, "ready", "dependency_wait", "hold-dependency-reappeared", gate.reason)
    M.log_apply(dept, proposal_id, "dependency_wait", dep_version, { add = { M._blocked_on_dependency_label }, remove = {} }, {
      "github-proxy.github_issue_comment_request",
      "github-proxy.github_issue_label_request",
    })
    M.log_raise(dept, proposal_id, "github-proxy.github_issue_comment_request", M.build_ready_split_canonicalized_comment_request(
      issue.repo, issue.number, proposal_id, state.version, "dependency_wait", dep_version, gate, issue.source_ref
    ))
    M.log_raise(dept, proposal_id, "github-proxy.github_issue_label_request", requests_labels.build_label_request(M,
      issue.repo, issue.number, { M._blocked_on_dependency_label }, {},
      base_ids.dedup_key({ "dependency", "label", "hold", tostring(proposal_id), tostring(dep_version), tostring(gate.kind) }), issue.source_ref
    ))
    return true
  end
  local ready_comment_id = M.ready_hand_off_comment_id(
    facts.current.comments,
    proposal_id,
    state.version
  )
  if ready_comment_id == nil then
    M.log_cas_decision(dept, proposal_id, state, "ready", "implementing", "skip-pending(ready-marker-comment-not-visible)", "trusted ready state marker comment id is not visible")
    return false
  end
  local redrive_round = ready_redrive_round(
    M,
    facts.current.comments,
    proposal_id,
    state.version,
    row
  )
  local ready_payload = payloads_builders.build_devloop_ready_payload(M, {
    proposal_id = fields.proposal_id,
    dedup_key = next_ready_redrive_version(state.version, redrive_round),
    source_ref = fields.source_ref,
    effect_version = state.version,
    include_ready_hand_off = true,
    ready_comment_id = ready_comment_id,
  })
  local raised = { "devloop_ready" }
  local command_comment_request = nil
  if facts.command ~= nil then
    command_comment_request = operator_commands.build_operator_issue_reready_comment_request(M, issue.repo, issue.number, facts.command, "ready", issue.source_ref)
    table.insert(raised, "github-proxy.github_issue_comment_request")
  end
  M.log_cas_decision(dept, proposal_id, state, "ready", "implementing", "applied(replay)", "dependency gate is satisfied")
  M.log_apply(dept, proposal_id, nil, nil, { add = {}, remove = {} }, raised)
  if command_comment_request ~= nil then
    M.log_raise(dept, proposal_id, "github-proxy.github_issue_comment_request", command_comment_request)
  end
  M.log_raise(dept, proposal_id, "devloop_ready", ready_payload)
  return true
end

return {
  dependency_wait = M.replay_dependency_wait_state,
  ready = M.replay_ready_state,
}
end

return S
