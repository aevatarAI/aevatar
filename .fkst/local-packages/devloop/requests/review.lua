local entity_lib = require("devloop.entity")
local devloop_base = require("devloop.base")
local base_ids = require("devloop.base_ids")
local m_claims = require("devloop.claims")
local payloads_builders = require("devloop.payloads.builders")
local C = {}
local forge_validators = require("devloop.forge_validators")
local operator_commands = require("devloop.operator_commands")
local config = require("devloop.config")
local comment_strings = require("devloop.strings")
local labels = require("devloop.requests.labels")
local shared = require("devloop.requests.shared")
local m_builders = require("devloop.markers.builders")

local ai_sentinel = shared.ai_sentinel

function C.attach_reviewing_handoff(M, request, proposal_id, pr_number, version, source_ref)
  request.handoff = {
    kind = "github-devloop.reviewing",
    proposal_id = proposal_id,
    pr_number = pr_number,
    version = version,
    source_ref = base_ids.normalize_source_ref(source_ref),
  }
  return request
end

function C.attach_blocked_handoff(M, request, proposal_id, pr_number, version, source_ref)
  request.handoff = {
    kind = "github-devloop.blocked",
    proposal_id = proposal_id,
    pr_number = pr_number,
    version = version,
    source_ref = base_ids.normalize_source_ref(source_ref),
  }
  return request
end

function C.attach_fixing_handoff(M, request, proposal_id, pr_number, version, review_fact, source_ref)
  local normalized = payloads_builders.build_devloop_fixing_payload(M, {
    proposal_id = proposal_id,
    impl_version = version,
  }, pr_number, review_fact, source_ref)
  request.handoff = {
    kind = "github-devloop.fixing",
    proposal_id = normalized.proposal_id,
    pr_number = normalized.pr_number,
    version = normalized.version,
    review_proposal_id = normalized.review_proposal_id,
    review_dedup_key = normalized.review_dedup_key,
    reviewed_head_sha = normalized.reviewed_head_sha,
    source_ref = normalized.source_ref,
  }
  for _, field in ipairs({
    "framing",
    "blocking_gap",
    "gate_baseline_sha",
    "predecessor_set",
    "gate_failure_excerpt",
  }) do
    if normalized[field] ~= nil then
      request.handoff[field] = normalized[field]
    end
  end
  if review_fact.current_head_sha ~= nil then
    if not require("devloop.pr_safety").is_safe_head_sha(review_fact.current_head_sha) then
      error("github-devloop: invalid fixing handoff current head sha")
    end
    request.handoff.current_head_sha = tostring(review_fact.current_head_sha)
  end
  return request
end

function C.build_review_converge_round_comment_request(M, repo, issue_number, unresolved, issue_proposal_id, round, marker_body, source_ref)
  return entity_lib.build_entity_comment_request({
    kind = "pr",
    repo = repo,
    number = unresolved.pr_number or select(2, devloop_base.parse_pr_source_ref(unresolved.source_ref)),
  }, shared.build_convergence_display(M, comment_strings.comment_string(M, "pr_review_convergence_round_prefix"), unresolved, round)
    .. "\n\n" .. tostring(marker_body)
    .. "\n" .. ai_sentinel, base_ids.dedup_key({
    "review-converge-round",
    "comment",
    tostring(issue_proposal_id),
    tostring(round),
    tostring(unresolved.dedup_key),
  }), source_ref or unresolved.source_ref)
end

function C.build_issue_review_converge_round_comment_request(M, repo, issue_number, unresolved, issue_proposal_id, round, marker_body, source_ref)
  return m_claims.attach_issue_claim({
    schema = "github-proxy.v1",
    repo = repo,
    issue_number = issue_number,
    body = shared.build_convergence_display(M, comment_strings.comment_string(M, "pr_review_convergence_round_prefix"), unresolved, round)
      .. "\n\n" .. tostring(marker_body)
      .. "\n" .. ai_sentinel,
    dedup_key = base_ids.dedup_key({
      "review-converge-round",
      "comment",
      tostring(issue_proposal_id),
      tostring(round),
      tostring(unresolved.dedup_key),
    }),
    source_ref = base_ids.normalize_source_ref(source_ref or unresolved.source_ref),
  }, source_ref or unresolved.source_ref)
end

function C.build_reviewing_comment_request(M, repo, issue_number, origin, pr_number, source_ref)
  local state_marker = M.state_marker(origin.proposal_id, "reviewing", origin.impl_version)
  local request = entity_lib.build_entity_comment_request({
    kind = "pr",
    repo = repo,
    number = pr_number,
  }, comment_strings.comment_string(M, "pr_ready_for_review")
    .. "\n\n" .. state_marker, base_ids.dedup_key({
    "observe-pr",
    "comment",
    tostring(origin.proposal_id),
    tostring(origin.impl_version),
    tostring(pr_number),
  }), source_ref)
  return C.attach_reviewing_handoff(M, request, origin.proposal_id, pr_number, origin.impl_version, source_ref)
end

function C.build_operator_rereview_comment_request(M, repo, pr_number, proposal_id, new_version, command, source_ref)
  local state_marker = M.state_marker(proposal_id, "reviewing", new_version)
  local marker = operator_commands.operator_command_marker(M, command, "applied", "rereview")
  local request = entity_lib.build_entity_comment_request({
    kind = "pr",
    repo = repo,
    number = pr_number,
  }, "github-devloop operator command accepted: rereview"
    .. "\n\n" .. state_marker
    .. "\n" .. marker
    .. "\n" .. ai_sentinel, base_ids.dedup_key({
    "operator-command",
    "comment",
    tostring(command.key),
    "applied",
    tostring(new_version),
  }), source_ref)
  return C.attach_reviewing_handoff(M, request, proposal_id, pr_number, new_version, source_ref)
end

function C.pr_base_unmanaged_blocked_version(M, version)
  return tostring(version or "") .. "/blocked/pr-base-unmanaged"
end

function C.build_pr_base_unmanaged_comment_request(M, repo, pr_number, origin, integration_branch, source_ref)
  local blocked_version = C.pr_base_unmanaged_blocked_version(M, origin.impl_version)
  local state_marker = M.state_marker(origin.proposal_id, "blocked", blocked_version)
  local reason_marker = m_builders.pr_base_unmanaged_marker(M, origin.proposal_id, pr_number, origin.base_branch, integration_branch)
  return C.attach_blocked_handoff(M, entity_lib.build_entity_comment_request({
    kind = "pr",
    repo = repo,
    number = pr_number,
  }, "github-devloop blocked PR because its base branch is not managed by this instance."
    .. "\n\nReason: pr-base-unmanaged"
    .. "\nPR base: " .. tostring(origin.base_branch)
    .. "\nConfigured integration branch: " .. tostring(integration_branch)
    .. "\n\n" .. state_marker
    .. "\n" .. reason_marker
    .. "\n" .. ai_sentinel, base_ids.dedup_key({
    "observe-pr",
    "blocked",
    "pr-base-unmanaged",
    tostring(origin.proposal_id),
    tostring(origin.impl_version),
    tostring(pr_number),
    tostring(origin.base_branch),
    tostring(integration_branch),
  }), source_ref), origin.proposal_id, pr_number, blocked_version, source_ref)
end

function C.build_review_result_comment_request(M, repo, issue_number, issue_proposal_id, issue_version, reached, source_ref)
  local to_state = reached.reflection_checkpoint and "review-meta"
    or reached.decision == "approve" and "merge-ready"
    or "fixing"
  local state_marker = M.state_marker(issue_proposal_id, to_state, issue_version)
  local fix_round = nil
  if reached.decision == "reject" then
    fix_round = M.version_fix_round(issue_version)
  end
  local blocking_gap = shared.bounded_blocking_gap(M, reached)
  local marker = m_builders.review_result_marker(M, reached.proposal_id, issue_proposal_id, reached.decision, reached.dedup_key, fix_round, blocking_gap)
  local reflection_marker = ""
  if reached.reflection_checkpoint then
    reflection_marker = "\n" .. m_builders.fix_reflection_marker(M, issue_proposal_id, reached.dedup_key, "checkpoint", issue_version, fix_round)
  end
  local merge_marker = ""
  if reached.decision == "approve" then
    local _, pr_number, _, reviewed_head_sha = devloop_base.parse_pr_review_proposal_id(reached.proposal_id)
    merge_marker = "\n" .. m_builders.merge_ready_marker(M, issue_proposal_id, pr_number, issue_version, reached.proposal_id, reached.dedup_key, reviewed_head_sha)
  end
  local body_text = devloop_base.neutralize_untrusted_comment_text(reached.body or "")
  local verdict_summary = shared.build_verdict_summary(M, reached.angle_results)
  local body = comment_strings.comment_string(M, "pr_review_decision_prefix") .. tostring(reached.decision)
  if verdict_summary ~= nil then
    body = body .. "\n" .. verdict_summary
  end
  if reached.decision == "reject" and blocking_gap ~= nil then
    body = body .. "\n" .. comment_strings.comment_string(M, "blocking_gap_label") .. devloop_base.neutralize_untrusted_comment_text(blocking_gap)
  end
  local _, pr_number = devloop_base.parse_pr_source_ref(source_ref)
  local request = entity_lib.build_entity_comment_request({
    kind = "pr",
    repo = repo,
    number = pr_number,
  }, body
    .. "\n\n" .. body_text
    .. "\n\n" .. state_marker
    .. "\n" .. marker
    .. reflection_marker
    .. merge_marker
    .. "\n" .. ai_sentinel, base_ids.dedup_key({
    "review-result",
    "comment",
    tostring(issue_proposal_id),
    tostring(reached.decision),
    tostring(reached.dedup_key),
  }), source_ref)
  if reached.decision == "approve" then
    local _, _, _, reviewed_head_sha = devloop_base.parse_pr_review_proposal_id(reached.proposal_id)
    request.handoff = {
      kind = "github-devloop.merge_ready",
      proposal_id = issue_proposal_id,
      pr_number = pr_number,
      version = issue_version,
      review_proposal_id = reached.proposal_id,
      review_dedup_key = reached.dedup_key,
      reviewed_head_sha = reviewed_head_sha,
      current_head_sha = reached.current_head_sha or reviewed_head_sha,
      source_ref = base_ids.normalize_source_ref(source_ref),
    }
  elseif reached.decision == "reject" and not reached.reflection_checkpoint then
    local _, _, _, reviewed_head_sha = devloop_base.parse_pr_review_proposal_id(reached.proposal_id)
    C.attach_fixing_handoff(M, request, issue_proposal_id, pr_number, issue_version, {
      review_proposal_id = reached.proposal_id,
      review_dedup_key = reached.dedup_key,
      reviewed_head_sha = reviewed_head_sha,
      framing = reached.framing,
      blocking_gap = reached.blocking_gap,
      current_head_sha = reached.current_head_sha,
    }, source_ref)
  end
  return request
end

function C.build_high_risk_review_evidence_comment_request(M, repo, issue_proposal_id, issue_version, reached, pr_number, reviewed_head_sha, paths_digest, angle_digest, source_ref)
  local marker = m_builders.high_risk_review_evidence_marker(M,
    issue_proposal_id,
    issue_version,
    pr_number,
    reviewed_head_sha,
    reached.proposal_id,
    reached.dedup_key,
    paths_digest,
    angle_digest
  )
  return entity_lib.build_entity_comment_request({
    kind = "pr",
    repo = repo,
    number = pr_number,
  }, "github-devloop high-risk PR review evidence"
    .. "\n\n" .. marker
    .. "\n" .. ai_sentinel, base_ids.dedup_key({
    "high-risk-review-evidence",
    "comment",
    tostring(issue_proposal_id),
    tostring(issue_version),
    tostring(reached.proposal_id),
    tostring(reached.dedup_key),
    tostring(paths_digest),
    tostring(angle_digest),
  }), source_ref)
end

function C.build_merge_gate_fix_comment_request(M, repo, issue_number, merge_ready, fix_version, reason, gate_baseline_sha, source_ref, predecessor_set, handoff_fields)
  local safe_reason = M.merge_gate_reason_class(reason)
  local display_reason = devloop_base.neutralize_untrusted_comment_text(reason or "gate-failed")
  if display_reason == "" then
    display_reason = "gate-failed"
  end
  if gate_baseline_sha ~= nil and not forge_validators.is_git_sha(gate_baseline_sha) then
    error("github-devloop: invalid merge-gate baseline sha")
  end
  local test_command = devloop_base.neutralize_untrusted_comment_text(config.test_command(M))
  local state_marker = M.state_marker(merge_ready.proposal_id, "fixing", fix_version)
  local marker = m_builders.merge_gate_marker(M,
    merge_ready.proposal_id,
    merge_ready.pr_number,
    fix_version,
    merge_ready.review_proposal_id,
    merge_ready.review_dedup_key,
    merge_ready.reviewed_head_sha,
    gate_baseline_sha,
    safe_reason,
    predecessor_set
  )
  local request = entity_lib.build_entity_comment_request({
    kind = "pr",
    repo = repo,
    number = merge_ready.pr_number,
  }, comment_strings.comment_string(M, "merge_gate_failed_prefix") .. display_reason
    .. "\n" .. comment_strings.comment_string(M, "reproduce_locally_prefix") .. test_command .. comment_strings.comment_string(M, "reproduce_locally_suffix")
    .. "\n\n" .. state_marker
    .. "\n" .. marker, base_ids.dedup_key({
    "merge",
    "comment",
    "fixing",
    tostring(merge_ready.proposal_id),
    tostring(merge_ready.version),
    tostring(fix_version),
    tostring(predecessor_set or "nopred"),
    safe_reason,
  }), source_ref)
  handoff_fields = handoff_fields or {}
  local gate_failure_excerpt = handoff_fields.gate_failure_excerpt
  if gate_failure_excerpt == nil and handoff_fields.preserve_nil_gate_failure_excerpt ~= true then
    gate_failure_excerpt = reason
  end
  return C.attach_fixing_handoff(M, request, merge_ready.proposal_id, merge_ready.pr_number, fix_version, {
    review_proposal_id = merge_ready.review_proposal_id,
    review_dedup_key = merge_ready.review_dedup_key,
    reviewed_head_sha = merge_ready.reviewed_head_sha,
    blocking_gap = handoff_fields.blocking_gap,
    gate_baseline_sha = gate_baseline_sha,
    predecessor_set = predecessor_set,
    gate_failure_excerpt = gate_failure_excerpt,
    current_head_sha = handoff_fields.current_head_sha,
  }, source_ref)
end

function C.build_fix_reviewing_comment_request(M, repo, issue_number, fix, old_head_sha, new_head_sha, new_version)
  local state_marker = M.state_marker(fix.proposal_id, "reviewing", new_version or fix.version)
  local marker = m_builders.fix_marker(M, fix.proposal_id, fix.review_proposal_id, fix.review_dedup_key, old_head_sha, new_head_sha)
  local summary = ""
  if fix.fix_summary ~= nil and tostring(fix.fix_summary) ~= "" then
    summary = "\n" .. comment_strings.comment_string(M, "fix_round_summary_label") .. devloop_base.neutralize_untrusted_comment_text(fix.fix_summary)
  end
  local request = entity_lib.build_entity_comment_request({
    kind = "pr",
    repo = repo,
    number = fix.pr_number,
  }, comment_strings.comment_string(M, "fix_pushed_for_rereview")
    .. "\n\n" .. comment_strings.comment_string(M, "previous_reviewed_head_label") .. tostring(old_head_sha)
    .. "\n" .. comment_strings.comment_string(M, "new_head_label") .. tostring(new_head_sha)
    .. summary
    .. "\n\n" .. state_marker
    .. "\n" .. marker, base_ids.dedup_key({
    "fix",
    "comment",
    tostring(fix.proposal_id),
    tostring(fix.review_dedup_key),
    tostring(new_head_sha),
  }), fix.source_ref)
  return C.attach_reviewing_handoff(M, request, fix.proposal_id, fix.pr_number, new_version or fix.version, fix.source_ref)
end

function C.raise_fix_reviewing(M, opts)
  opts = opts or {}
  local dept = tostring(opts.dept or "unknown")
  local repo = opts.repo
  local issue_number = opts.issue_number
  local fix = opts.fix or {}
  local old_head_sha = opts.old_head_sha
  local new_head_sha = opts.new_head_sha
  local new_version = opts.new_version or M.next_fix_version(fix.version)
  local reason = opts.reason
  local current_state = opts.current_state or { state = "fixing", version = fix.version }
  if opts.fix_summary ~= nil or opts.clear_fix_summary == true then
    fix.fix_summary = opts.fix_summary
  end

  M.log_cas_decision(dept, fix.proposal_id, current_state, "fixing", "reviewing", "applied", reason)
  local comment_request = C.build_fix_reviewing_comment_request(M, repo, issue_number, fix, old_head_sha, new_head_sha, new_version)
  local label_request = labels.build_fix_reviewing_label_request(M, repo, issue_number, fix, new_head_sha, new_version)
  local add_labels, remove_labels = M.state_label_changes("reviewing")
  local raised = {
    "github-proxy.github_pr_comment_request",
  }
  if issue_number ~= nil then
    table.insert(raised, "github-proxy.github_issue_label_request")
  end
  M.log_apply(dept, fix.proposal_id, "reviewing", new_version, { add = add_labels, remove = remove_labels }, raised)
  M.log_raise(dept, fix.proposal_id, "github-proxy.github_pr_comment_request", comment_request)
  if issue_number ~= nil then
    M.log_raise(dept, fix.proposal_id, "github-proxy.github_issue_label_request", label_request)
  end
end

function C.build_merge_head_reviewing_comment_request(M, repo, issue_number, merge_ready, old_head_sha, new_head_sha, new_version, source_ref)
  local state_marker = M.state_marker(merge_ready.proposal_id, "reviewing", new_version)
  local request = entity_lib.build_entity_comment_request({
    kind = "pr",
    repo = repo,
    number = merge_ready.pr_number,
  }, comment_strings.comment_string(M, "pr_head_advanced")
    .. "\n\n" .. comment_strings.comment_string(M, "previous_reviewed_head_label") .. tostring(old_head_sha)
    .. "\n" .. comment_strings.comment_string(M, "current_head_label") .. tostring(new_head_sha)
    .. "\n\n" .. state_marker, base_ids.dedup_key({
    "merge",
    "comment",
    "reviewing",
    tostring(merge_ready.proposal_id),
    tostring(new_version),
    tostring(new_head_sha),
  }), source_ref)
  return C.attach_reviewing_handoff(M, request, merge_ready.proposal_id, merge_ready.pr_number, new_version, source_ref)
end

function C.build_review_carry_over_comment_request(M, repo, pr_number, issue_proposal_id, version, carry, source_ref)
  local state_marker = M.state_marker(issue_proposal_id, "merge-ready", version)
  local review_marker = m_builders.review_result_marker(M, carry.new_review_proposal_id, issue_proposal_id, "approve", carry.new_review_dedup_key)
  local merge_marker = m_builders.merge_ready_marker(M, issue_proposal_id, pr_number, version, carry.new_review_proposal_id, carry.new_review_dedup_key, carry.new_head_sha)
  local carry_marker = m_builders.review_carry_over_marker(M,
    issue_proposal_id,
    version,
    carry.old_review_proposal_id,
    carry.old_review_dedup_key,
    carry.approved_head_sha,
    carry.new_review_proposal_id,
    carry.new_review_dedup_key,
    carry.new_head_sha,
    carry.base_head_sha
  )
  local request = entity_lib.build_entity_comment_request({
    kind = "pr",
    repo = repo,
    number = pr_number,
  }, "github-devloop PR review approval carried over"
    .. "\nResolution delta proof: merge-tree-empty-delta"
    .. "\nApproved head: " .. tostring(carry.approved_head_sha)
    .. "\nNew head: " .. tostring(carry.new_head_sha)
    .. "\nBase head: " .. tostring(carry.base_head_sha)
    .. "\n\n" .. state_marker
    .. "\n" .. review_marker
    .. "\n" .. merge_marker
    .. "\n" .. carry_marker
    .. "\n" .. ai_sentinel, base_ids.dedup_key({
    "review-carry-over",
    "comment",
    tostring(issue_proposal_id),
    tostring(version),
    tostring(carry.approved_head_sha),
    tostring(carry.new_head_sha),
  }), source_ref)
  request.handoff = {
    kind = "github-devloop.merge_ready",
    proposal_id = issue_proposal_id,
    pr_number = pr_number,
    version = version,
    review_proposal_id = carry.new_review_proposal_id,
    review_dedup_key = carry.new_review_dedup_key,
    reviewed_head_sha = carry.new_head_sha,
    current_head_sha = carry.new_head_sha,
    source_ref = base_ids.normalize_source_ref(source_ref),
  }
  return request
end

return C
