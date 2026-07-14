local entity_lib = require("devloop.entity")
local devloop_base = require("devloop.base")
local base_ids = require("devloop.base_ids")
local m_claims = require("devloop.claims")
local C = {}
local forge_validators = require("devloop.forge_validators")
local comment_strings = require("devloop.strings")
local shared = require("devloop.requests.shared")
local m_shared = require("devloop.markers.shared")
local m_builders = require("devloop.markers.builders")
local m_mq = require("devloop.merge_queue")

local strings = shared.strings
local ai_sentinel = shared.ai_sentinel

function C.build_observe_comment_request(M, issue, proposal)
  return m_claims.attach_issue_claim({
    schema = "github-proxy.v1",
    repo = issue.repo,
    issue_number = issue.number,
    body = comment_strings.comment_string(M, "thinking_started") .. "\n\n"
      .. M.state_marker(proposal.proposal_id, "thinking", tostring(proposal.effect_version or proposal.dedup_key)),
    dedup_key = base_ids.dedup_key({
      tostring(proposal.proposal_id),
      "comment",
      "thinking",
      tostring(proposal.dedup_key),
    }),
    source_ref = base_ids.normalize_source_ref(issue.source_ref),
  }, issue.source_ref)
end
function C.build_result_comment_request(M, repo, issue_number, reached, state_name)
  local marker = m_builders.result_marker(M, reached.proposal_id, reached.decision, reached.dedup_key)
  local canonical_state = state_name or "ready"
  local effects = canonical_state == "ready" and "result-marker,ready-label,devloop-ready" or "result-marker,ready-label,dependency-hold"
  local state_marker = M.state_marker(reached.proposal_id, canonical_state, tostring(reached.effect_version or reached.dedup_key), effects)
  local body_text = devloop_base.neutralize_untrusted_comment_text(reached.body or "")
  local verdict_summary = shared.build_verdict_summary(M, reached.angle_results)
  local body = comment_strings.comment_string(M, "decision_prefix") .. tostring(reached.decision)
  if verdict_summary ~= nil then
    body = body .. "\n" .. verdict_summary
  end
  body = body
    .. "\n\n" .. body_text
    .. "\n\n" .. state_marker
    .. "\n" .. marker
    .. "\n" .. ai_sentinel
  local request = m_claims.attach_issue_claim({
    schema = "github-proxy.v1",
    repo = repo,
    issue_number = issue_number,
    body = body,
    dedup_key = tostring(reached.proposal_id) .. "/comment/" .. tostring(reached.decision)
      .. "/" .. (tostring(reached.dedup_key):gsub(":", "-")),
    source_ref = base_ids.normalize_source_ref(reached.source_ref),
  }, reached.source_ref)
  if canonical_state == "ready" then
    request.handoff = {
      kind = "github-devloop.ready",
      proposal_id = reached.proposal_id,
      version = reached.dedup_key,
      marker_version = tostring(reached.effect_version or reached.dedup_key),
      source_ref = base_ids.normalize_source_ref(reached.source_ref),
    }
  end
  return request
end
function C.result_effects_complete(M, current, reached)
  if type(current) ~= "table" or type(reached) ~= "table" then
    return false
  end
  return M.has_result_marker(current.comments, reached.proposal_id, reached.decision, reached.dedup_key)
    and M.state_label_hint_matches(current.labels, "ready")
end

function C.build_converge_round_comment_request(M, repo, issue_number, unresolved, round, marker_body, handoff)
  return m_claims.attach_issue_claim({
    schema = "github-proxy.v1",
    repo = repo,
    issue_number = issue_number,
    body = shared.build_convergence_display(M, comment_strings.comment_string(M, "convergence_round_prefix"), unresolved, round)
      .. "\n\n" .. tostring(marker_body)
      .. "\n" .. ai_sentinel,
    dedup_key = base_ids.dedup_key({
      "converge-round",
      "comment",
      tostring(unresolved.proposal_id),
      tostring(round),
      tostring(unresolved.dedup_key),
    }),
    source_ref = base_ids.normalize_source_ref(unresolved.source_ref), handoff = handoff,
  }, unresolved.source_ref)
end

function C.build_dependency_hold_comment_request(M, repo, issue_number, proposal_id, version, gate, marker, source_ref)
  local reason = devloop_base.neutralize_untrusted_comment_text(gate and gate.reason or "")
  if reason == "" then
    reason = gate and gate.kind or "dependency-hold"
  end
  return m_claims.attach_issue_claim({
    schema = "github-proxy.v1",
    repo = repo,
    issue_number = issue_number,
    body = comment_strings.comment_string(M, "dependency_hold_prefix") .. tostring(gate and gate.kind or "unknown")
      .. "\n\n" .. comment_strings.comment_string(M, "reason_inline_label") .. reason
      .. "\n\n" .. tostring(marker),
    dedup_key = base_ids.dedup_key({ "dependency", "comment", tostring(proposal_id), tostring(version), tostring(gate and gate.kind or "unknown") }),
    source_ref = base_ids.normalize_source_ref(source_ref),
  }, source_ref)
end

function C.build_dependency_release_comment_request(M, repo, issue_number, proposal_id, version, gate, source_ref)
  local reason = devloop_base.neutralize_untrusted_comment_text(gate and gate.reason or "satisfied")
  if reason == "" then
    reason = "satisfied"
  end
  local note_markers = M.dependency_gate_note_markers(proposal_id, version, gate)
  local markers = M.dependency_release_marker(proposal_id, version)
  if note_markers ~= "" then
    markers = markers .. "\n" .. note_markers
  end
  return m_claims.attach_issue_claim({
    schema = "github-proxy.v1",
    repo = repo,
    issue_number = issue_number,
    body = comment_strings.comment_string(M, "dependency_release_prefix") .. reason
      .. "\n\n" .. comment_strings.comment_string(M, "reason_inline_label") .. reason
      .. "\n\n" .. markers,
    dedup_key = base_ids.dedup_key({ "dependency", "comment", "release", tostring(proposal_id), tostring(version), reason }),
    source_ref = base_ids.normalize_source_ref(source_ref),
  }, source_ref)
end

function C.build_intake_decision_comment_request(M, repo, issue_number, candidate, decision, reason, service_class)
  if not m_shared.is_intake_service_class(service_class) then
    error("github-devloop: invalid intake service class")
  end
  local normalized_class = m_shared.normalize_intake_service_class(service_class)
  local marker = m_builders.intake_decision_marker(M, candidate.proposal_id, decision, candidate.dedup_key, normalized_class)
  local safe_reason = devloop_base.neutralize_untrusted_comment_text(reason or "")
  if safe_reason == "" then
    safe_reason = comment_strings.comment_string(M, "no_reason_provided")
  end
  if #safe_reason > M._max_meta_reason_len then
    safe_reason = base_ids.truncate_utf8(safe_reason, M._max_meta_reason_len)
  end
  local detail = ""
  if decision == "track" then
    detail = "\n\n" .. comment_strings.comment_string(M, "intake_tracking_ack")
  end
  return m_claims.attach_issue_claim({
    schema = "github-proxy.v1",
    repo = repo,
    issue_number = issue_number,
    body = comment_strings.comment_string(M, "intake_decision_prefix") .. tostring(decision)
      .. "\nService class: " .. normalized_class
      .. detail
      .. "\n\n" .. comment_strings.comment_string(M, "reason_block_label") .. "\n" .. safe_reason
      .. "\n\n" .. marker,
    dedup_key = base_ids.dedup_key({
      "intake",
      "comment",
      tostring(candidate.proposal_id),
      tostring(candidate.dedup_key),
    }),
    source_ref = base_ids.normalize_source_ref(candidate.source_ref),
  }, candidate.source_ref)
end

function C.build_implementing_comment_request(M, repo, issue_number, ready, worktree, branch, head_sha, base_branch, base_sha, attempt, started_at, exec_ref)
  if not forge_validators.is_git_ref_safe(branch) then
    error("github-devloop: invalid implementing branch")
  end
  if not forge_validators.is_git_sha(head_sha) then
    error("github-devloop: invalid implementing head_sha")
  end
  if not forge_validators.is_git_ref_safe(base_branch) then
    error("github-devloop: invalid implementing base_branch")
  end
  if not forge_validators.is_git_sha(base_sha) then
    error("github-devloop: invalid implementing base_sha")
  end
  local marker = m_builders.implementing_marker(M, ready.proposal_id, ready.dedup_key, branch, head_sha, base_branch, base_sha)
  local attempt_marker = M.implement_attempt_marker(ready.proposal_id, ready.dedup_key, attempt or 1, started_at or "", exec_ref)
  return m_claims.attach_issue_claim({
    schema = "github-proxy.v1",
    repo = repo,
    issue_number = issue_number,
    body = comment_strings.comment_string(M, "implementation_output_published")
      .. "\n\n" .. comment_strings.comment_string(M, "worktree_label") .. tostring(worktree)
      .. "\n" .. comment_strings.comment_string(M, "branch_label") .. tostring(branch)
      .. "\n" .. comment_strings.comment_string(M, "head_label") .. tostring(head_sha)
      .. "\n" .. comment_strings.comment_string(M, "base_branch_label") .. tostring(base_branch)
      .. "\n" .. comment_strings.comment_string(M, "base_head_label") .. tostring(base_sha)
      .. "\n\n" .. attempt_marker
      .. "\n" .. marker,
    dedup_key = base_ids.dedup_key({
      "implement",
      "comment",
      "implementing",
      tostring(ready.dedup_key),
    }),
    source_ref = base_ids.normalize_source_ref(ready.source_ref),
  }, ready.source_ref)
end

function C.build_implementing_state_comment_request(M, repo, issue_number, ready, worktree, branch, base_branch, base_sha, attempt, started_at, exec_ref)
  if not forge_validators.is_git_ref_safe(branch) then
    error("github-devloop: invalid implementing branch")
  end
  if not forge_validators.is_git_ref_safe(base_branch) then
    error("github-devloop: invalid implementing base_branch")
  end
  if not forge_validators.is_git_sha(base_sha) then
    error("github-devloop: invalid implementing base_sha")
  end
  local state_marker = M.state_marker(ready.proposal_id, "implementing", ready.dedup_key)
  local attempt_marker = M.implement_attempt_marker(ready.proposal_id, ready.dedup_key, attempt or 1, started_at or "", exec_ref)
  return m_claims.attach_issue_claim({
    schema = "github-proxy.v1",
    repo = repo,
    issue_number = issue_number,
    body = "github-devloop implementation worktree ready"
      .. "\n\n" .. comment_strings.comment_string(M, "worktree_label") .. tostring(worktree)
      .. "\n" .. comment_strings.comment_string(M, "branch_label") .. tostring(branch)
      .. "\n" .. comment_strings.comment_string(M, "base_branch_label") .. tostring(base_branch)
      .. "\n" .. comment_strings.comment_string(M, "base_head_label") .. tostring(base_sha)
      .. "\n\n" .. state_marker
      .. "\n" .. attempt_marker,
    dedup_key = base_ids.dedup_key({
      "implement",
      "comment",
      "implementing-state",
      tostring(ready.dedup_key),
    }),
    source_ref = base_ids.normalize_source_ref(ready.source_ref),
  }, ready.source_ref)
end

function C.build_implement_attempt_comment_request(M, repo, issue_number, ready, attempt, started_at, exec_ref)
  local marker = M.implement_attempt_marker(ready.proposal_id, ready.dedup_key, attempt, started_at, exec_ref)
  return {
    schema = "github-proxy.v1",
    repo = repo,
    issue_number = issue_number,
    body = "github-devloop implementation attempt started\n\n" .. marker,
    dedup_key = base_ids.dedup_key({
      "implement",
      "comment",
      "attempt",
      tostring(ready.dedup_key),
      tostring(attempt),
    }),
    source_ref = base_ids.normalize_source_ref(ready.source_ref),
  }
end

function C.build_implement_version_mismatch_comment_request(M, repo, issue_number, ready, expected_version, current_version, attempt)
  local marker = M.implement_version_mismatch_marker(ready.proposal_id, expected_version, current_version, attempt)
  return {
    schema = "github-proxy.v1",
    repo = repo,
    issue_number = issue_number,
    body = "github-devloop implementation version mismatch observed\n\n" .. marker,
    dedup_key = base_ids.dedup_key({
      "implement",
      "comment",
      "version-mismatch",
      tostring(ready.proposal_id),
      devloop_base.implement_version_mismatch_key(expected_version, current_version),
      tostring(attempt),
    }),
    source_ref = base_ids.normalize_source_ref(ready.source_ref),
  }
end

function C.build_impl_failure_comment_request(M, repo, issue_number, ready, reason, detail, attempt)
  local safe_reason = strings.sanitize_key(reason or "failed", M._max_key_len):gsub("/", "-")
  local retry_attempt = tonumber(attempt) or 1
  local text = tostring(detail or "")
  if #text > M._max_impl_output_len then
    text = base_ids.truncate_utf8(text, M._max_impl_output_len)
  end
  if text == "" then
    text = comment_strings.comment_string(M, "no_implementation_output")
  end
  text = devloop_base.neutralize_untrusted_comment_text(text)

  local marker = M.impl_failure_marker(ready.proposal_id, ready.dedup_key, safe_reason, attempt)
  local state_marker = M.state_marker(ready.proposal_id, "impl-failed", ready.dedup_key)
  return m_claims.attach_issue_claim({
    schema = "github-proxy.v1",
    repo = repo,
    issue_number = issue_number,
    body = comment_strings.comment_string(M, "implementation_failed_prefix") .. safe_reason
      .. "\n\n" .. text
      .. "\n\n" .. state_marker
      .. "\n" .. marker,
    dedup_key = base_ids.dedup_key({
      "implement",
      "comment",
      "failure",
      safe_reason,
      tostring(retry_attempt),
      tostring(ready.dedup_key),
    }),
    source_ref = base_ids.normalize_source_ref(ready.source_ref),
  }, ready.source_ref)
end

function C.build_queue_starvation_reconcile_comment_request(M, repo, merge_ready, cause)
  local attempt_key = cause and cause.attempt_key or "attempt"
  local marker = m_mq.queue_starvation_reconcile_marker(M,
    merge_ready.proposal_id,
    merge_ready.pr_number,
    merge_ready.version,
    merge_ready.reviewed_head_sha,
    cause and cause.incident_identity or "merge-ready",
    attempt_key,
    "head-redriven"
  )
  return entity_lib.build_entity_comment_request({
    kind = "pr",
    repo = repo,
    number = merge_ready.pr_number,
  }, "github-devloop queue-starvation reconciliation redrove the merge-ready queue head"
    .. "\n\nQueue head PR: #" .. tostring(merge_ready.pr_number)
    .. "\nReviewed head: " .. tostring(merge_ready.reviewed_head_sha)
    .. "\nAttempt: " .. tostring(attempt_key)
    .. "\n\n" .. marker, base_ids.dedup_key({
    "queue-starvation",
    "reconcile",
    tostring(merge_ready.proposal_id),
    tostring(merge_ready.pr_number),
    tostring(merge_ready.version),
    tostring(merge_ready.reviewed_head_sha),
    tostring(attempt_key),
  }), entity_lib.pr_source_ref(repo, merge_ready.pr_number))
end

return C
