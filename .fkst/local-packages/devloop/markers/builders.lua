local strings = require("contract.strings")
local C = {}
local forge_validators = require("devloop.forge_validators")
local autonomy_ledger = require("devloop.autonomy_ledger")
local shared = require("devloop.markers.shared")

local valid_round = shared.valid_round
local strings = shared.strings
local safe_marker_attr = shared.safe_marker_attr

function C.review_meta_marker(M, issue_proposal_id, dedup_key, action, version, blocking_gap, reason)
  local fields = ""
  if action ~= nil then
    if not M._is_review_meta_action(action) then
      error("github-devloop: invalid review-meta action")
    end
    fields = fields .. '" action="' .. tostring(action)
  end
  if version ~= nil then
    fields = fields .. '" version="' .. tostring(version)
  end
  if action == "fix" then
    local gap = safe_marker_attr(M, blocking_gap, M._max_blocking_gap_len)
    if gap == "" or not strings.is_bounded_string(gap, M._max_blocking_gap_len) then
      error("github-devloop: invalid review-meta gap")
    end
    fields = fields .. '" gap="' .. gap
  elseif action == "spec-amendment" then
    fields = fields .. '" reason="blocked-pending-spec'
  end
  return '<!-- fkst:github-devloop:review-meta:v1 proposal="' .. tostring(issue_proposal_id)
    .. '" dedup="' .. tostring(dedup_key)
    .. fields
    .. '" -->'
end

function C.fix_reflection_marker(M, issue_proposal_id, dedup_key, verdict, version, fix_round)
  if verdict ~= "checkpoint" and verdict ~= "continue" and verdict ~= "spec-gap" then
    error("github-devloop: invalid fix reflection verdict")
  end
  local n = valid_round(fix_round)
  if n == nil then
    error("github-devloop: invalid fix reflection round")
  end
  local version_field = ""
  if version ~= nil then
    version_field = '" version="' .. tostring(version)
  end
  return '<!-- fkst:github-devloop:fix-reflection:v1 proposal="' .. tostring(issue_proposal_id)
    .. '" dedup="' .. tostring(dedup_key)
    .. '" verdict="' .. tostring(verdict)
    .. version_field
    .. '" fix_round="' .. tostring(n)
    .. '" -->'
end

function C.fix_marker(M, issue_proposal_id, review_proposal_id, review_dedup_key, old_head_sha, new_head_sha)
  if not forge_validators.is_git_sha(old_head_sha) or not forge_validators.is_git_sha(new_head_sha) then
    error("github-devloop: invalid fix head sha")
  end
  return '<!-- fkst:github-devloop:fix:v1 proposal="' .. tostring(issue_proposal_id)
    .. '" review_proposal="' .. tostring(review_proposal_id)
    .. '" review_dedup="' .. tostring(review_dedup_key)
    .. '" old_head_sha="' .. tostring(old_head_sha)
    .. '" new_head_sha="' .. tostring(new_head_sha)
    .. '" -->'
end

function C.merge_gate_marker(M, issue_proposal_id, pr_number, version, review_proposal_id, review_dedup_key, head_sha, gate_baseline_sha, reason, predecessor_set)
  if not forge_validators.is_positive_pr_number(pr_number) or not forge_validators.is_git_sha(head_sha) then
    error("github-devloop: invalid merge-gate marker")
  end
  local baseline_field = ""
  if gate_baseline_sha ~= nil then
    if not forge_validators.is_git_sha(gate_baseline_sha) then
      error("github-devloop: invalid merge-gate marker")
    end
    baseline_field = '" gate_baseline_sha="' .. tostring(gate_baseline_sha)
  end
  local predecessor_field = ""
  if predecessor_set ~= nil then
    if not strings.is_path_safe_key(predecessor_set, M._max_dedup_len) then
      error("github-devloop: invalid merge-gate predecessor set")
    end
    predecessor_field = '" predecessor_set="' .. tostring(predecessor_set)
  end
  return '<!-- fkst:github-devloop:merge-gate:v1 proposal="' .. tostring(issue_proposal_id)
    .. '" pr="' .. tostring(pr_number)
    .. '" version="' .. tostring(version)
    .. '" review_proposal="' .. tostring(review_proposal_id)
    .. '" review_dedup="' .. tostring(review_dedup_key)
    .. '" head_sha="' .. tostring(head_sha)
    .. baseline_field
    .. predecessor_field
    .. '" reason="' .. tostring(strings.sanitize_key(reason or "gate-failed", false):gsub("/", "-"))
    .. '" -->'
end

function C.implementing_marker(M, proposal_id, dedup_key, branch, head_sha, base_branch, base_sha)
  if not forge_validators.is_git_ref_safe(branch) then
    error("github-devloop: invalid branch")
  end
  if not forge_validators.is_git_sha(head_sha) then
    error("github-devloop: invalid head sha")
  end
  if not forge_validators.is_git_ref_safe(base_branch) then
    error("github-devloop: invalid base branch")
  end
  if not forge_validators.is_git_sha(base_sha) then
    error("github-devloop: invalid base sha")
  end
  return '<!-- fkst:github-devloop:implementing:v1 proposal="' .. tostring(proposal_id)
    .. '" dedup="' .. tostring(dedup_key)
    .. '" branch="' .. tostring(branch)
    .. '" head_sha="' .. tostring(head_sha)
    .. '" base_branch="' .. tostring(base_branch)
    .. '" base_sha="' .. tostring(base_sha)
    .. '" -->'
end

function C.pr_link_marker(M, proposal_id, pr_number, branch, impl_version, base_branch)
  if not forge_validators.is_positive_pr_number(pr_number) then
    error("github-devloop: invalid pr number")
  end
  if not forge_validators.is_git_ref_safe(branch) then
    error("github-devloop: invalid branch")
  end
  if not forge_validators.is_git_ref_safe(base_branch) then
    error("github-devloop: invalid base branch")
  end
  return '<!-- fkst:github-devloop:pr-link:v1 proposal="' .. tostring(proposal_id)
    .. '" pr="' .. tostring(pr_number)
    .. '" branch="' .. tostring(branch)
    .. '" impl_version="' .. tostring(impl_version)
    .. '" base_branch="' .. tostring(base_branch)
    .. '" -->'
end

function C.pr_link_marker_template(M, proposal_id, branch, impl_version, base_branch)
  if not forge_validators.is_git_ref_safe(branch) then
    error("github-devloop: invalid branch")
  end
  if not forge_validators.is_git_ref_safe(base_branch) then
    error("github-devloop: invalid base branch")
  end
  return '<!-- fkst:github-devloop:pr-link:v1 proposal="' .. tostring(proposal_id)
    .. '" pr="{{pr_number}}"'
    .. ' branch="' .. tostring(branch)
    .. '" impl_version="' .. tostring(impl_version)
    .. '" base_branch="' .. tostring(base_branch)
    .. '" -->'
end

function C.pr_delegation_marker(M, issue_proposal_id, pr_proposal_id, pr_number, version, delegation)
  if not forge_validators.is_positive_pr_number(pr_number) then
    error("github-devloop: invalid pr-delegation pr number")
  end
  if not strings.is_bounded_string(issue_proposal_id, M._max_key_len)
    or not strings.is_bounded_string(pr_proposal_id, M._max_key_len)
    or not strings.is_bounded_string(version, M._max_dedup_len)
    or not strings.is_path_safe_key(delegation, M._max_dedup_len) then
    error("github-devloop: invalid pr-delegation marker")
  end
  return '<!-- fkst:github-devloop:pr-delegation:v1 proposal="' .. tostring(issue_proposal_id)
    .. '" pr_proposal="' .. tostring(pr_proposal_id)
    .. '" pr="' .. tostring(pr_number)
    .. '" version="' .. tostring(version)
    .. '" delegation="' .. tostring(delegation)
    .. '" -->'
end

function C.pr_origin_marker(M, proposal_id, issue_number, branch, impl_version, base_branch)
  if not forge_validators.is_git_ref_safe(branch) then
    error("github-devloop: invalid branch")
  end
  if not forge_validators.is_git_ref_safe(base_branch) then
    error("github-devloop: invalid base branch")
  end
  return '<!-- fkst:github-devloop:pr-origin:v1 proposal="' .. tostring(proposal_id)
    .. '" issue="' .. tostring(issue_number)
    .. '" branch="' .. tostring(branch)
    .. '" impl_version="' .. tostring(impl_version)
    .. '" base_branch="' .. tostring(base_branch)
    .. '" -->'
end

function C.review_result_marker(M, review_proposal_id, issue_proposal_id, decision, dedup_key, fix_round, blocking_gap)
  if decision ~= "approve" and decision ~= "reject" then
    error("github-devloop: invalid review decision")
  end
  local fix_round_field = ""
  local gap_field = ""
  if decision == "reject" then
    if fix_round ~= nil then
      local n = valid_round(fix_round)
      if n == nil then
        error("github-devloop: invalid review reject fix round")
      end
      fix_round_field = '" fix_round="' .. tostring(n)
    end
    local gap = safe_marker_attr(M, blocking_gap, M._max_blocking_gap_len)
    if gap == "" or not strings.is_bounded_string(gap, M._max_blocking_gap_len) then
      error("github-devloop: invalid review reject gap")
    end
    gap_field = '" gap="' .. gap
  end
  return '<!-- fkst:github-devloop:review-result:v1 proposal="' .. tostring(review_proposal_id)
    .. '" issue_proposal="' .. tostring(issue_proposal_id)
    .. '" decision="' .. tostring(decision)
    .. '" dedup="' .. tostring(dedup_key)
    .. fix_round_field
    .. gap_field
    .. '" -->'
end

function C.merge_ready_marker(M, issue_proposal_id, pr_number, version, review_proposal_id, review_dedup_key, head_sha)
  if not forge_validators.is_positive_pr_number(pr_number) then
    error("github-devloop: invalid merge-ready pr number")
  end
  if not forge_validators.is_git_sha(head_sha) then
    error("github-devloop: invalid merge-ready head sha")
  end
  if not strings.is_bounded_string(version, M._max_dedup_len)
    or not strings.is_bounded_string(review_proposal_id, M._max_key_len)
    or not strings.is_bounded_string(review_dedup_key, M._max_dedup_len) then
    error("github-devloop: invalid merge-ready marker")
  end
  return '<!-- fkst:github-devloop:merge-ready:v1 proposal="' .. tostring(issue_proposal_id)
    .. '" pr="' .. tostring(pr_number)
    .. '" version="' .. tostring(version)
    .. '" review_proposal="' .. tostring(review_proposal_id)
    .. '" review_dedup="' .. tostring(review_dedup_key)
    .. '" head_sha="' .. tostring(head_sha)
    .. '" -->'
end

function C.high_risk_review_evidence_marker(M, issue_proposal_id, version, pr_number, head_sha, review_proposal_id, review_dedup_key, paths_digest, angle_digest)
  if not forge_validators.is_positive_pr_number(pr_number) or not forge_validators.is_git_sha(head_sha) then
    error("github-devloop: invalid high-risk review evidence marker")
  end
  if not strings.is_bounded_string(version, M._max_dedup_len)
    or not strings.is_bounded_string(review_proposal_id, M._max_key_len)
    or not strings.is_bounded_string(review_dedup_key, M._max_dedup_len)
    or not strings.is_bounded_string(paths_digest, M._max_key_len)
    or not strings.is_bounded_string(angle_digest, M._max_key_len) then
    error("github-devloop: invalid high-risk review evidence marker")
  end
  return '<!-- fkst:github-devloop:high-risk-review-evidence:v1 proposal="' .. tostring(issue_proposal_id)
    .. '" version="' .. tostring(version)
    .. '" pr="' .. tostring(pr_number)
    .. '" head_sha="' .. tostring(head_sha)
    .. '" review_proposal="' .. tostring(review_proposal_id)
    .. '" review_dedup="' .. tostring(review_dedup_key)
    .. '" risk="high" angle="high-risk" verdict="approve" paths_digest="' .. tostring(paths_digest)
    .. '" angle_digest="' .. tostring(angle_digest)
    .. '" -->'
end

function C.review_carry_over_marker(M, issue_proposal_id, version, old_review_proposal_id, old_review_dedup_key, approved_head_sha, new_review_proposal_id, new_review_dedup_key, new_head_sha, base_head_sha)
  if not forge_validators.is_git_sha(approved_head_sha)
    or not forge_validators.is_git_sha(new_head_sha)
    or not forge_validators.is_git_sha(base_head_sha) then
    error("github-devloop: invalid review carry-over marker")
  end
  if not strings.is_bounded_string(version, M._max_dedup_len)
    or not strings.is_bounded_string(old_review_proposal_id, M._max_key_len)
    or not strings.is_bounded_string(old_review_dedup_key, M._max_dedup_len)
    or not strings.is_bounded_string(new_review_proposal_id, M._max_key_len)
    or not strings.is_bounded_string(new_review_dedup_key, M._max_dedup_len) then
    error("github-devloop: invalid review carry-over marker")
  end
  return '<!-- fkst:github-devloop:review-carry-over:v1 proposal="' .. tostring(issue_proposal_id)
    .. '" version="' .. tostring(version)
    .. '" old_review_proposal="' .. tostring(old_review_proposal_id)
    .. '" old_review_dedup="' .. tostring(old_review_dedup_key)
    .. '" approved_head_sha="' .. tostring(approved_head_sha)
    .. '" new_review_proposal="' .. tostring(new_review_proposal_id)
    .. '" new_review_dedup="' .. tostring(new_review_dedup_key)
    .. '" new_head_sha="' .. tostring(new_head_sha)
    .. '" base_head_sha="' .. tostring(base_head_sha)
    .. '" proof="merge-tree-empty-delta" -->'
end

function C.merged_marker(M, issue_proposal_id, pr_number, version, head_sha, autonomy_record)
  if not forge_validators.is_positive_pr_number(pr_number) or not forge_validators.is_git_sha(head_sha) then
    error("github-devloop: invalid merged marker")
  end
  local autonomy_attrs = autonomy_record ~= nil and (' autonomy_result="v1"' .. autonomy_ledger.autonomy_result_marker_attrs(M, autonomy_record)) or ""
  return '<!-- fkst:github-devloop:merged:v1 proposal="' .. tostring(issue_proposal_id)
    .. '" pr="' .. tostring(pr_number)
    .. '" version="' .. tostring(version)
    .. '" head_sha="' .. tostring(head_sha) .. '"' .. autonomy_attrs .. ' -->'
end

function C.merging_marker(M, issue_proposal_id, pr_number, version, head_sha)
  if not forge_validators.is_positive_pr_number(pr_number) or not forge_validators.is_git_sha(head_sha) then
    error("github-devloop: invalid merging marker")
  end
  return '<!-- fkst:github-devloop:merging:v1 proposal="' .. tostring(issue_proposal_id)
    .. '" pr="' .. tostring(pr_number)
    .. '" version="' .. tostring(version)
    .. '" head_sha="' .. tostring(head_sha)
    .. '" -->'
end

function C.intake_decision_marker(M, issue_proposal_id, decision, dedup_key, service_class)
  if decision ~= "enable" and decision ~= "track" and decision ~= "decline" and decision ~= "escalate-to-class" then
    error("github-devloop: invalid intake decision")
  end
  if not strings.is_bounded_string(dedup_key, M._max_dedup_len) then
    error("github-devloop: invalid intake dedup")
  end
  if not shared.is_intake_service_class(service_class) then
    error("github-devloop: invalid intake service class")
  end
  local normalized_class = shared.normalize_intake_service_class(service_class)
  return '<!-- fkst:github-devloop:intake-decision:v1 proposal="' .. tostring(issue_proposal_id)
    .. '" decision="' .. tostring(decision)
    .. '" class="' .. normalized_class
    .. '" dedup="' .. tostring(dedup_key)
    .. '" -->'
end

function C.orphan_reaped_marker(M, proposal_id, pr_number, reason)
  if not forge_validators.is_positive_pr_number(pr_number) then
    error("github-devloop: invalid orphan reaped pr number")
  end
  local safe_reason = strings.sanitize_key(reason or "parent-terminal", false):gsub("/", "-")
  return '<!-- fkst:github-devloop:orphan-reaped:v1 proposal="' .. tostring(proposal_id)
    .. '" pr="' .. tostring(pr_number)
    .. '" reason="' .. tostring(safe_reason)
    .. '" -->'
end

function C.pr_base_unmanaged_marker(M, proposal_id, pr_number, pr_base, integration_branch)
  if not forge_validators.is_positive_pr_number(pr_number) then
    error("github-devloop: invalid unmanaged-base pr number")
  end
  if not forge_validators.is_git_ref_safe(pr_base) or not forge_validators.is_git_ref_safe(integration_branch) then
    error("github-devloop: invalid unmanaged-base branch")
  end
  return '<!-- fkst:github-devloop:pr-base-unmanaged:v1 proposal="' .. tostring(proposal_id)
    .. '" pr="' .. tostring(pr_number)
    .. '" reason="pr-base-unmanaged'
    .. '" pr_base="' .. tostring(pr_base)
    .. '" integration_branch="' .. tostring(integration_branch)
    .. '" -->'
end

function C.result_marker(M, proposal_id, decision, dedup_key)
  if decision ~= "approve" and decision ~= "reject" then
    error("github-devloop: invalid decision")
  end
  return '<!-- fkst:github-devloop:result:v1 proposal="' .. tostring(proposal_id)
    .. '" decision="' .. decision
    .. '" dedup="' .. tostring(dedup_key)
    .. '" -->'
end
return C
