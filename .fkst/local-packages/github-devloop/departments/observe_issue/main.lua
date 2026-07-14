local entity_lib = require("devloop.entity")
local devloop_base = require("devloop.base")
local base_ids = require("devloop.base_ids")
local m_claims = require("devloop.claims")
local requests_labels = require("devloop.requests.labels")
local requests_lifecycle = require("devloop.requests.lifecycle")
local parsers_pr = require("devloop.parsers.pr")
local parsers_issue = require("devloop.parsers.issue")
local m_facts = require("devloop.markers.facts")
local core, saga, replay_fields = require("core"), require("workflow.saga"), require("devloop.replay_fields")
local contract_time = require("contract.time")
local operator_commands = require("devloop.operator_commands")
local queue = require("devloop.queue")
local transition_version = require("contract.transition_version")
local context_bundle = require("devloop.context_bundle")
local replayer = require("devloop.replayer")

local payloads_builders = require("devloop.payloads.builders")
local conv_reconcile = require("devloop.convergence.reconcile")
local v_issue = require("devloop.validators.issue")
local v_validate_proposal = require("devloop.validators.validate_proposal")
local v_pr = require("devloop.validators.pr")
local m_builders = require("devloop.markers.builders")
local devloop_entity_view = require("devloop.github_proxy_entity_view")
local M = {}

local spec = {
  consumes = { "github-proxy.github_entity_changed", "devloop_observe_issue" },
  produces = {
    "consensus.proposal",
    "github-proxy.github_issue_label_request",
    "github-proxy.github_issue_comment_request",
    "github-proxy.github_issue_create_request",
    "github-proxy.github_pr_comment_request",
    "devloop_ready",
    "github-devloop-decompose.devloop_decompose",
    "devloop_reconcile",
    "devloop_timeout_reconcile",
  },
  fanout = { "github-proxy.github_entity_changed" },
  stall_window = "30s",
}

local function issue_label_state(issue_state)
  if issue_state ~= nil
    and (issue_state.state == "blocked" or issue_state.state == "merged") then
    return issue_state
  end
  return issue_state
end

local function linked_open_pr(snapshot, pr_number)
  for _, item in ipairs(snapshot and snapshot.prs or {}) do
    if tostring(item.number or "") == tostring(pr_number or "") then
      local current = item.current or {}
      if tostring(current.state or ""):lower() == "open" then
        return current
      end
    end
  end
  return nil
end

local function linked_pr(snapshot, pr_number)
  for _, item in ipairs(snapshot and snapshot.prs or {}) do
    if tostring(item.number or "") == tostring(pr_number or "") then
      return item.current
    end
  end
  return nil
end

local function issue_local_pr_bound_state_matches_link(issue_state, link)
  if issue_state == nil or link == nil then
    return false
  end
  if issue_state.state == "pr-open" or issue_state.state == "reviewing" then
    return transition_version.strip_suffixes(issue_state.version) == transition_version.strip_suffixes(link.impl_version)
  end
  if issue_state.state == "fixing" then
    return core.fixing_version_matches_link(issue_state.version, link.impl_version)
  end
  if issue_state.state == "review-meta" or issue_state.state == "merge-ready" or issue_state.state == "merging" then
    return core.fixing_version_matches_link(issue_state.version, link.impl_version)
  end
  return false
end

local function maybe_reconcile_issue_local_orphaned_pr(issue, proposal_id, current, issue_state, link, snapshot)
  if not issue_local_pr_bound_state_matches_link(issue_state, link) then
    return false
  end
  local row = replay_fields.restart_transition_row(core.restart_transition_table(), issue_state.state)
  if row == nil or row.terminal == true then
    return false
  end
  local facts = {
    proposal_id = proposal_id,
    current = current,
    link = link,
    snapshot = snapshot,
  }
  local current_pr = linked_pr(snapshot, link.pr_number)
  if current_pr == nil then
    if snapshot.absent_prs ~= nil and snapshot.absent_prs[tostring(link.pr_number or "")] == true then
      return core.terminal_linked_pr_action("observe_issue", issue, issue_state, proposal_id, link, nil, facts)
    end
    return false
  end
  if tostring(current_pr.state or ""):lower() == "open" then
    return false
  end
  return core.terminal_linked_pr_action("observe_issue", issue, issue_state, proposal_id, link, current_pr, facts)
end

local function issue_label_projection_state(issue_state, link, snapshot)
  if issue_state ~= nil
    and issue_state.state == "pr-open"
    and link ~= nil
    and tostring(link.impl_version or "") == tostring(issue_state.version or "")
    and linked_open_pr(snapshot, link.pr_number) ~= nil then
    return issue_state
  end
  return issue_label_state(issue_state)
end

local function thinking_state_budget_exceeded(state)
  local threshold = core.stall_suspect_threshold_minutes("thinking")
  local marker_seconds = contract_time.iso_timestamp_epoch_seconds(state and state.marker_created_at)
  if threshold == nil or marker_seconds == nil then
    return false
  end
  return now() - marker_seconds >= threshold * 60
end

local function replay_or_timeout(issue, proposal_id, current, link, snapshot, state, event_ts, issue_state)
  local row = replay_fields.restart_transition_row(core.restart_transition_table(), state.state)
  local facts = {
    proposal_id = proposal_id,
    current = current,
    link = link,
    snapshot = snapshot,
    event_ts = event_ts,
    fresh_current_state = state,
  }
  local epoch = row and row.actionable_epoch
  if issue.source == "liveness-scan"
    and type(epoch) == "table"
    and epoch.allows_state_entry_if_never_deferred == true then
    facts.dependency_gate = core.dependency_gate(issue.repo, issue.number, {
      proposal_id = proposal_id,
      version = state.version,
      comments = current.comments,
    })
  end
  for _, advancing_fact in ipairs(row and row.advancing_facts or {}) do
    if advancing_fact.fact_family == "dependency-gate" and facts.dependency_gate == nil then
      facts.dependency_gate = core.dependency_gate(issue.repo, issue.number, {
        proposal_id = proposal_id, version = state.version, comments = current.comments,
      })
    end
  end
  if core.canonicalize_legacy_ready_dependency_wait("observe_issue", issue, state, facts) then
    return true
  end
  local state_is_issue_local = issue_state ~= nil
    and issue_state.state == state.state
    and tostring(issue_state.version or "") == tostring(state.version or "")
  local timeout_surface = issue.source == "liveness-scan" and "issue_liveness_scan" or "issue"
  if state_is_issue_local and core.restart_observe_timeout_due(row, timeout_surface, state, facts, now()) then
    return core.maybe_timeout_redrive_from_table("observe_issue", issue, state, row, facts)
  end
  if issue.source ~= "liveness-scan"
    and state_is_issue_local
    and core.restart_observe_replay_due(row, "issue", state, facts, now()) then
    return replayer.replay_from_table(core, "observe_issue", issue, state, row, facts)
  end
  if core.restart_row_observable_on(row, "issue")
    and state_is_issue_local
    and replayer.replay_from_table(core, "observe_issue", issue, state, row, facts) then
    return true
  end
  if core.restart_row_observable_on(row, "issue") then
    return false
  end
  if issue_state == nil
    or issue_state.state ~= state.state
    or tostring(issue_state.version or "") ~= tostring(state.version or "") then
    return false
  end
  return core.maybe_timeout_redrive_from_table("observe_issue", issue, state, row, facts)
end

local function ensure_managed_issue_claim(issue, proposal_id, current, state)
  local claim_state = m_claims.issue_claim_state(core, current.assignees, m_claims.claim_owner(), current.labels)
  if claim_state == "other" then
    core.log_cas_decision("observe_issue", proposal_id, state, state.state, state.state, "skip-claim-lost", "CLAIM lost before managed issue handling")
    return false
  end
  if claim_state == "self" then
    return true
  end
  return m_claims.claim_issue_for_management(core, "observe_issue", issue.repo, issue.number, current, proposal_id)
end

local function maybe_apply_issue_rereview_command(issue, proposal_id, current, state, event_ts)
  local command = operator_commands.operator_command_fact(core, current.comments, "rereview")
  if command == nil then
    return false
  end
  if operator_commands.has_operator_command_response(core, current.comments, command) then
    core.log_cas_decision("observe_issue", proposal_id, state, "stalled-thinking", "thinking", "skip-idempotent(command-response-visible)", "operator command response marker is already visible")
    return false
  end
  if state.state ~= "thinking" then
    core.log_cas_decision("observe_issue", proposal_id, state, "thinking", "thinking", "refused(invalid-state)", "operator rereview requires thinking")
    local refusal = operator_commands.build_operator_issue_command_refusal_request(
      core,
      issue.repo,
      issue.number,
      command,
      "rereview requires thinking state",
      issue.source_ref
    )
    core.log_raise("observe_issue", proposal_id, "github-proxy.github_issue_comment_request", refusal)
    return true
  end
  if not replayer.has_thinking_converge_replay(core, current, proposal_id, state, issue.source_ref)
    and not thinking_state_budget_exceeded(state) then
    core.log_cas_decision("observe_issue", proposal_id, state, "stalled-thinking", "thinking", "refused(active-thinking)", "operator rereview requires stalled thinking")
    local refusal = operator_commands.build_operator_issue_command_refusal_request(
      core,
      issue.repo,
      issue.number,
      command,
      "rereview requires stalled thinking state",
      issue.source_ref
    )
    core.log_raise("observe_issue", proposal_id, "github-proxy.github_issue_comment_request", refusal)
    return true
  end

  local proposal = replayer.build_thinking_replay_proposal(core, issue, proposal_id, state, current, event_ts)
  if proposal == nil then
    core.log_cas_decision("observe_issue", proposal_id, state, "stalled-thinking", "thinking", "refused(cannot-rebuild-proposal)", "operator rereview could not rebuild thinking proposal")
    local refusal = operator_commands.build_operator_issue_command_refusal_request(
      core,
      issue.repo,
      issue.number,
      command,
      "rereview could not rebuild the current thinking proposal",
      issue.source_ref
    )
    core.log_raise("observe_issue", proposal_id, "github-proxy.github_issue_comment_request", refusal)
    return true
  end

  local comment_request = operator_commands.build_operator_issue_rereview_comment_request(
    core,
    issue.repo,
    issue.number,
    command,
    proposal,
    issue.source_ref
  )
  core.log_cas_decision("observe_issue", proposal_id, state, "stalled-thinking", "thinking", "applied(operator-rereview)", "trusted operator command requested issue rereview")
  core.log_apply("observe_issue", proposal_id, "thinking", proposal.dedup_key, { add = {}, remove = {} }, {
    "github-proxy.github_issue_comment_request",
    "consensus.proposal",
  })
  core.log_raise("observe_issue", proposal_id, "github-proxy.github_issue_comment_request", comment_request)
  core.log_raise("observe_issue", proposal_id, "consensus.proposal", proposal)
  return true
end

local function raise_stale_dependency_label_clear(issue, proposal_id, state, labels)
  if state.state == "ready" or state.state == "dependency_wait" or not core.has_label(labels, core._blocked_on_dependency_label) then
    return false
  end
  core.log_apply("observe_issue", proposal_id, state.state, state.version, { add = {}, remove = { core._blocked_on_dependency_label } }, {
    "github-proxy.github_issue_label_request",
  })
  core.log_raise("observe_issue", proposal_id, "github-proxy.github_issue_label_request", requests_labels.build_label_request(core,
    issue.repo,
    issue.number,
    {},
    { core._blocked_on_dependency_label },
    base_ids.dedup_key({ "dependency", "label", "clear", tostring(proposal_id), tostring(state.version or "unversioned") }),
    issue.source_ref
  ))
  return true
end

local function timeout_reconcile_reready_reentry_state(current, proposal_id, state, source_ref, link)
  if state.state ~= "blocked" or link ~= nil then
    return nil, "reready requires ready or dependency_wait state"
  end
  local fact = conv_reconcile.timeout_reconcile_fact_for_terminal_version(core, current.comments, proposal_id, state.version)
  if fact == nil then
    return nil, "reready requires ready or dependency_wait state"
  end
  if fact.from_state ~= "ready" and fact.from_state ~= "dependency_wait" then
    return nil, "reready requires timeout-reconcile from ready or dependency_wait state"
  end
  local marker_source = fact.source_ref or {}
  if tostring(marker_source.kind or "") ~= tostring(source_ref and source_ref.kind or "")
    or tostring(marker_source.ref or "") ~= tostring(source_ref and source_ref.ref or "") then
    return nil, "reready requires timeout-reconcile source_ref to match the issue"
  end
  return {
    state = fact.from_state,
    version = fact.from_version,
    stage_rank = core.stage_rank(fact.from_state),
    marker_created_at = fact.comment_created_at,
    operator_reentry = {
      command = "reready",
      from_state = "blocked",
      terminal_version = state.version,
      timeout_round = fact.round,
    },
  }, nil
end

local function maybe_apply_issue_reready_command(issue, proposal_id, current, state, link)
  local command = operator_commands.operator_command_fact(core, current.comments, "reready")
  if command == nil then
    return false
  end
  if operator_commands.has_operator_command_response(core, current.comments, command) then
    core.log_cas_decision("observe_issue", proposal_id, state, "ready", "ready", "skip-idempotent(command-response-visible)", "operator command response marker is already visible")
    return false
  end
  local replay_state = state
  local refusal_reason = nil
  if state.state ~= "ready" and state.state ~= "dependency_wait" then
    replay_state, refusal_reason = timeout_reconcile_reready_reentry_state(current, proposal_id, state, issue.source_ref, link)
  end
  if replay_state == nil then
    core.log_cas_decision("observe_issue", proposal_id, state, "ready", "ready", "refused(invalid-state)", "operator reready requires ready state")
    local refusal = operator_commands.build_operator_issue_command_refusal_request(
      core,
      issue.repo,
      issue.number,
      command,
      refusal_reason or "reready requires ready or dependency_wait state",
      issue.source_ref
    )
    core.log_raise("observe_issue", proposal_id, "github-proxy.github_issue_comment_request", refusal)
    return true
  end
  local row, replay_facts = core.replay_row_and_facts_with_declared_dependency_gate(
    issue, proposal_id, replay_state,
    current, command
  )
  replayer.replay_from_table(core, "observe_issue", issue, replay_state, row, replay_facts)
  return true
end

local function has_unmet_blocker(gate, blocker_number)
  if type(gate) ~= "table" or type(gate.unmet) ~= "table" then
    return false
  end
  for _, number in ipairs(gate.unmet) do
    if tonumber(number) == tonumber(blocker_number) then
      return true
    end
  end
  return false
end

local function maybe_apply_issue_dependency_waiver_command(issue, proposal_id, current, state)
  local command = operator_commands.operator_command_fact(core, current.comments, "dependency-waiver")
  if command == nil then
    return false
  end
  if operator_commands.has_operator_command_response(core, current.comments, command) then
    core.log_cas_decision("observe_issue", proposal_id, state, "ready", "ready", "skip-idempotent(command-response-visible)", "operator command response marker is already visible")
    return false
  end
  if state.state ~= "dependency_wait" then
    core.log_cas_decision("observe_issue", proposal_id, state, "dependency_wait", "ready", "refused(invalid-state)", "operator dependency waiver requires dependency_wait state")
    local refusal = operator_commands.build_operator_issue_command_refusal_request(
      core,
      issue.repo,
      issue.number,
      command,
      "dependency-waiver requires dependency_wait state",
      issue.source_ref
    )
    core.log_raise("observe_issue", proposal_id, "github-proxy.github_issue_comment_request", refusal)
    return true
  end

  local blocker_number = command.blocker_number
  local gate = core.dependency_gate(issue.repo, issue.number, {
    proposal_id = proposal_id,
    version = state.version,
    comments = current.comments,
  })
  if gate.kind ~= "waiting"
    or gate.reason ~= "dependency-waiver-required"
    or not has_unmet_blocker(gate, blocker_number) then
    core.log_cas_decision("observe_issue", proposal_id, state, "ready", "ready", "refused(invalid-dependency-waiver)", "operator dependency waiver requires a matching completed blocker without merged marker")
    local refusal = operator_commands.build_operator_issue_command_refusal_request(
      core,
      issue.repo,
      issue.number,
      command,
      "dependency-waiver requires a matching completed blocker without merged marker",
      issue.source_ref
    )
    core.log_raise("observe_issue", proposal_id, "github-proxy.github_issue_comment_request", refusal)
    return true
  end

  local comment_request = operator_commands.build_operator_issue_dependency_waiver_comment_request(
    core,
    issue.repo,
    issue.number,
    command,
    proposal_id,
    state.version,
    blocker_number,
    issue.source_ref
  )
  core.log_cas_decision("observe_issue", proposal_id, state, "dependency_wait", "ready", "applied(operator-dependency-waiver)", "trusted operator command created dependency waiver")
  replayer.replay_from_table(core, "observe_issue", issue, state, replay_fields.restart_transition_row(core.restart_transition_table(), "dependency_wait"), {
    proposal_id = proposal_id,
    current = current,
    command_comment_request = comment_request,
    dependency_gate = {
      ok = true,
      kind = "satisfied",
      reason = "dependency-waiver",
      notes = {
        {
          kind = "dependency-waiver",
          blocker_number = blocker_number,
          reason = "completed_without_merged_marker",
        },
      },
      unmet = {},
    },
  })
  return true
end

local function maybe_apply_issue_reimplement_command(issue, proposal_id, current, state, snapshot)
  local command = operator_commands.operator_command_fact(core, current.comments, "reimplement")
  if command == nil then
    return false
  end
  if operator_commands.has_operator_command_response(core, current.comments, command) then
    core.log_cas_decision("observe_issue", proposal_id, state, "impl-failed", "implementing", "skip-idempotent(command-response-visible)", "operator command response marker is already visible")
    return false
  end
  local link = m_facts.pr_link_fact(core, current.comments, proposal_id)
  local blocked_reentry = state.state == "blocked" and linked_open_pr(snapshot, link and link.pr_number) ~= nil
  if state.state ~= "impl-failed" and not blocked_reentry then
    core.log_cas_decision("observe_issue", proposal_id, state, "impl-failed|blocked(open-pr)", "implementing", "refused(invalid-state)", "operator reimplement requires impl-failed or blocked state with an open linked PR")
    local refusal = operator_commands.build_operator_issue_command_refusal_request(
      core,
      issue.repo,
      issue.number,
      command,
      "reimplement requires impl-failed or blocked state with an open linked PR",
      issue.source_ref
    )
    core.log_raise("observe_issue", proposal_id, "github-proxy.github_issue_comment_request", refusal)
    return true
  end

  local attempt = 1
  local failure = core.impl_failure_fact(current.comments, proposal_id, state.version)
  if failure ~= nil then
    attempt = tonumber(failure.attempt or 1) + 1
  elseif blocked_reentry then
    attempt = (core.implementation_retry_attempt(link.impl_version) or 1) + 1
  end
  local retry_version = blocked_reentry and link.impl_version or state.version
  local payload_source = {
    proposal_id = proposal_id,
    dedup_key = core.ready_payload_inner_version(retry_version),
    source_ref = issue.source_ref,
    impl_retry_attempt = attempt,
  }
  if blocked_reentry then
    payload_source.operator_reentry = {
      command = "reimplement",
      from_state = "blocked",
      pr_number = link.pr_number,
      state_version = state.version,
      impl_version = link.impl_version,
    }
  end
  local payload = payloads_builders.build_devloop_ready_payload(core, payload_source)
  local comment_request = operator_commands.build_operator_issue_reimplement_comment_request(
    core,
    issue.repo,
    issue.number,
    command,
    attempt,
    issue.source_ref
  )
  core.log_cas_decision("observe_issue", proposal_id, state, "impl-failed|blocked(open-pr)", "implementing", "applied(operator-reimplement)", "trusted operator command requested implementation retry")
  core.log_apply("observe_issue", proposal_id, nil, nil, { add = {}, remove = {} }, {
    "github-proxy.github_issue_comment_request",
    "devloop_ready",
  })
  core.log_raise("observe_issue", proposal_id, "github-proxy.github_issue_comment_request", comment_request)
  core.log_raise("observe_issue", proposal_id, "devloop_ready", payload)
  return true
end

local function process_issue_event(event)
  local issue = event.payload or {}
  if not v_issue.is_supported_issue(core, issue) then
    core.log_entry("observe_issue", event, "unknown", core.payload_field(issue, "dedup_key"))
    core.log_cas_decision("observe_issue", "unknown", { state = nil, version = nil }, "unmanaged", "thinking", "skip-foreign(proposal_id)", "unsupported event payload")
    return
  end

  local proposal_id = base_ids.proposal_id(issue.repo, issue.number)
  core.log_entry("observe_issue", event, proposal_id, issue.dedup_key)
  local lock_key = entity_lib.observe_lock_key(issue.repo, issue.number)
  with_lock(lock_key, function()
    devloop_base.assert_trusted_bot_configured()

    local state_view = require("devloop.github_proxy_entity_view").fetch_issue_view_state(core, issue.repo, issue.number, issue.updated_at, {
      force_fresh = true,
    })
    if state_view.exit_code ~= 0 then
      error("github-devloop: gh issue state view failed: " .. tostring(state_view.stderr))
    end

    local current = parsers_issue.parse_issue_view_state(core, state_view.stdout)
    current.updated_at = current.updated_at or issue.updated_at
    if current.state ~= "OPEN" then
      core.log_cas_decision("observe_issue", proposal_id, { state = nil, version = nil }, "unmanaged", "thinking", "skip-advanced-or-diverged", "issue is not open")
      return
    end
    if not devloop_base.is_opted_in(current.labels) then
      core.log_cas_decision("observe_issue", proposal_id, { state = nil, version = nil }, "unmanaged", "thinking", "skip-not-opted-in", "fkst-dev:enabled label is absent")
      return
    end
    core.log_forged_markers("observe_issue", proposal_id, current.comments)
    local link = m_facts.pr_link_fact(core, current.comments, proposal_id)
    local issue_state = core.current_state(current.comments, proposal_id)
    if devloop_base.is_intake_held(current.labels) then
      core.log_cas_decision("observe_issue", proposal_id, { state = nil, version = nil }, "unmanaged", "thinking", "skip-held", "fkst-dev:hold label is present")
      return
    end
    if issue.source == "pr-entity-change" then
      if issue_state.state ~= "awaiting-pr" then
        core.log_cas_decision("observe_issue", proposal_id, issue_state, "awaiting-pr", "awaiting-pr", "skip-foreign(parent-not-awaiting-pr)", "PR entity change only replays parent awaiting-pr")
        return
      end
      if not ensure_managed_issue_claim(issue, proposal_id, current, issue_state) then
        return
      end
      local row = replay_fields.restart_transition_row(core.restart_transition_table(), "awaiting-pr")
      replayer.replay_from_table(core, "observe_issue", issue, issue_state, row, {
        proposal_id = proposal_id,
        current = current,
        current_issue = current,
        current_pr = issue.child_pr,
        fresh_current_state = issue_state,
      })
      return
    end
    local claim_checked = false
    if issue_state.state ~= nil then
      if not ensure_managed_issue_claim(issue, proposal_id, current, issue_state) then
        return
      end
      claim_checked = true
      if maybe_apply_issue_reready_command(issue, proposal_id, current, issue_state, link) then
        return
      end
    end
    local snapshot = core.linked_pr_surface_snapshot(issue.repo, proposal_id, current.comments)
    snapshot.fresh = true
    local state = issue_state
    local function maybe_canonicalize_legacy_pr_open_issue()
      if issue_state == nil or issue_state.state ~= "pr-open" then
        return false
      end
      if link == nil
        or tonumber(link.pr_number) == nil
        or tostring(link.impl_version or "") ~= tostring(issue_state.version or "") then
        core.log_cas_decision("observe_issue", proposal_id, issue_state, "pr-open", "awaiting-pr", "skip-stale(pr-link-missing)", "legacy pr-open canonicalization requires a matching visible PR link")
        return false
      end
      if linked_open_pr(snapshot, link.pr_number) == nil then
        core.log_cas_decision("observe_issue", proposal_id, issue_state, "pr-open", "awaiting-pr", "skip-pending(open-pr-missing)", "legacy pr-open canonicalization requires an open linked PR")
        return false
      end
      local pr_proposal_id = entity_lib.pr_proposal_id(issue.repo, link.pr_number)
      local delegation = "g" .. tostring(core.implementation_retry_attempt(issue_state.version) or 1)
      local comment_body = "github-devloop canonicalized legacy issue PR state to delegated PR child"
        .. "\n\n" .. core.state_marker(proposal_id, "awaiting-pr", issue_state.version)
        .. "\n" .. m_builders.pr_delegation_marker(core, proposal_id, pr_proposal_id, link.pr_number, issue_state.version, delegation)
      local comment_request = entity_lib.build_entity_comment_request({
        kind = "issue",
        repo = issue.repo,
        number = issue.number,
      }, comment_body, base_ids.dedup_key({
        "canonicalize",
        "pr-open",
        tostring(proposal_id),
        tostring(issue_state.version),
        tostring(link.pr_number),
      }), issue.source_ref)
      local label_request = requests_labels.build_state_label_request(core, issue.repo, issue.number, "awaiting-pr", base_ids.dedup_key({
        "canonicalize",
        "pr-open",
        "label",
        tostring(proposal_id),
        tostring(issue_state.version),
        tostring(link.pr_number),
      }), issue.source_ref)
      local add_labels, remove_labels = core.state_label_changes("awaiting-pr")
      core.log_cas_decision("observe_issue", proposal_id, issue_state, "pr-open", "awaiting-pr", "applied(legacy-pr-open-canonicalized)", "open linked PR preserved as delegated child")
      core.log_apply("observe_issue", proposal_id, "awaiting-pr", issue_state.version, { add = add_labels, remove = remove_labels }, {
        "github-proxy.github_issue_comment_request",
        "github-proxy.github_issue_label_request",
      })
      core.log_raise("observe_issue", proposal_id, "github-proxy.github_issue_comment_request", comment_request)
      core.log_raise("observe_issue", proposal_id, "github-proxy.github_issue_label_request", label_request)
      return true
    end
    if state.state ~= nil then
      if not claim_checked and not ensure_managed_issue_claim(issue, proposal_id, current, state) then
        return
      end
      if maybe_apply_issue_rereview_command(issue, proposal_id, current, state, event.ts) then
        return
      end
      if maybe_apply_issue_dependency_waiver_command(issue, proposal_id, current, state) then
        return
      end
      if maybe_apply_issue_reimplement_command(issue, proposal_id, current, state, snapshot) then
        return
      end
      if maybe_canonicalize_legacy_pr_open_issue() then
        return
      end
      local label_state = issue_label_projection_state(issue_state, link, snapshot)
      local add_labels, remove_labels = core.state_label_reconcile_changes(current.labels, label_state.state)
      if #add_labels > 0 or #remove_labels > 0 then
        local label_request = requests_labels.build_label_request(core,
          issue.repo,
          issue.number,
          add_labels,
          remove_labels,
          base_ids.dedup_key({
            "reconcile",
            "label",
            proposal_id,
            label_state.state,
            tostring(label_state.version or "unversioned"),
          }),
          issue.source_ref
        )
        core.log_apply("observe_issue", proposal_id, label_state.state, label_state.version, { add = add_labels, remove = remove_labels }, {
          "github-proxy.github_issue_label_request",
        })
        core.log_raise("observe_issue", proposal_id, "github-proxy.github_issue_label_request", label_request)
      end
      raise_stale_dependency_label_clear(issue, proposal_id, state, current.labels)
      if maybe_reconcile_issue_local_orphaned_pr(issue, proposal_id, current, issue_state, link, snapshot) then
        return
      end
      if replay_or_timeout(issue, proposal_id, current, link, snapshot, state, event.ts, issue_state) then
        return
      end
    end
    local transition = core.versioned_transition_status(state, { "unmanaged" }, "thinking", issue.dedup_key)
    if transition == "stale" then
      core.log_cas_decision("observe_issue", proposal_id, state, "unmanaged", "thinking", core.cas_outcome(state, transition, issue.dedup_key), "current marker is not an unmanaged start")
      return
    end
    if transition == "pending" then
      core.log_cas_decision("observe_issue", proposal_id, state, "unmanaged", "thinking", core.cas_outcome(state, transition, issue.dedup_key), "unmanaged state marker pending for observe")
      error("github-devloop: unmanaged state marker pending for observe; retrying")
    end
    if not m_claims.claim_issue_for_management(core, "observe_issue", issue.repo, issue.number, current, proposal_id) then
      return
    end
    core.log_cas_decision("observe_issue", proposal_id, state, "unmanaged", "thinking", core.cas_outcome(state, transition, issue.dedup_key), "starting consensus for opted-in issue")

    issue.content_fetch = context_bundle.context_fetch_ref_from_bundle(core, {
      dept = "observe_issue",
      repo = issue.repo,
      issue_number = issue.number,
      proposal_id = proposal_id,
      version = issue.dedup_key,
      tick = event.ts,
    })
    local proposal = payloads_builders.build_board_proposal(core, issue, event.ts)
    if not v_validate_proposal.validate_proposal(core, proposal) then
      log.warn("github-devloop dept=observe_issue proposal_id=" .. tostring(proposal_id) .. " tag=SKIP reason=cannot-build-valid-proposal")
      return
    end

    local comment_request = requests_lifecycle.build_observe_comment_request(core, issue, proposal)
    local label_request = requests_labels.build_thinking_label_request(core, issue, proposal)
    local add_labels, remove_labels = core.state_label_changes("thinking")
    core.log_apply("observe_issue", proposal_id, "thinking", proposal.dedup_key, { add = add_labels, remove = remove_labels }, {
      "consensus.proposal",
      "github-proxy.github_issue_comment_request",
      "github-proxy.github_issue_label_request",
    })
    core.log_raise("observe_issue", proposal_id, "consensus.proposal", proposal)
    core.log_raise("observe_issue", proposal_id, "github-proxy.github_issue_comment_request", comment_request)
    core.log_raise("observe_issue", proposal_id, "github-proxy.github_issue_label_request", label_request)
  end)
end

local function process_pr_event(event)
  local pr = event.payload or {}
  if not v_pr.is_supported_pr(core, pr) then
    core.log_entry("observe_issue", event, "unknown", core.payload_field(pr, "dedup_key"))
    core.log_cas_decision("observe_issue", "unknown", { state = nil, version = nil }, "awaiting-pr", "awaiting-pr", "skip-foreign(pr)", "unsupported PR payload")
    return
  end

  local pr_view = devloop_entity_view.fetch_pr_view_origin(pr.repo, pr.number, pr.updated_at, {
    force_fresh = true,
    consumer = "observe_issue",
  })
  if pr_view.exit_code ~= 0 then
    error("github-devloop: observe-issue-pr-view-failed: " .. tostring(pr_view.stderr))
  end
  local current_pr = parsers_pr.parse_pr_view_origin(core, pr_view.stdout)
  current_pr.number = pr.number
  current_pr.force_fresh = true
  local origin = m_facts.pr_origin_fact(core, current_pr.comments)
  if origin == nil or origin.pr_native == true or origin.repo ~= pr.repo or tonumber(origin.issue_number) == nil then
    core.log_entry("observe_issue", event, "unknown", core.payload_field(pr, "dedup_key"))
    core.log_cas_decision("observe_issue", "unknown", { state = nil, version = nil }, "awaiting-pr", "awaiting-pr", "skip-foreign(pr-origin)", "PR entity change has no issue-backed devloop origin")
    return
  end
  if tostring(origin.branch or "") ~= tostring(current_pr.head_ref_name or "")
    or tostring(origin.base_branch or "") ~= tostring(current_pr.base_ref_name or "") then
    core.log_entry("observe_issue", event, origin.proposal_id, core.payload_field(pr, "dedup_key"))
    core.log_cas_decision("observe_issue", origin.proposal_id, { state = nil, version = nil }, "awaiting-pr", "awaiting-pr", "skip-stale(pr-origin)", "PR origin no longer matches current PR head/base")
    return
  end

  return process_issue_event({
    queue = event.queue,
    ts = event.ts,
    payload = {
      schema = "github-proxy.v1",
      type = "issue",
      repo = origin.repo,
      number = tonumber(origin.issue_number),
      title = "PR-backed parent issue",
      state = "OPEN",
      updated_at = pr.updated_at,
      dedup_key = tostring(pr.dedup_key or "") .. "/parent-awaiting-pr",
      source_ref = entity_lib.issue_source_ref(origin.repo, origin.issue_number),
      source = "pr-entity-change",
      child_pr = current_pr,
    },
  })
end

return saga.department(spec, { done = function() return false end, act = function(event)
  queue.dispatch_consumed_queue("observe_issue", spec, event, {
    ["github-proxy.github_entity_changed"] = function(e)
      if core.payload_field(e and e.payload, "type") == "pr" then
        return process_pr_event(e)
      end
      return process_issue_event(e)
    end,
    devloop_observe_issue = process_issue_event,
  })
end, wrap = core.wrap_pipeline_failure, name = "observe_issue" })
