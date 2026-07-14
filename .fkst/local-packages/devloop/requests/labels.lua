local base_ids = require("devloop.base_ids")
local m_claims = require("devloop.claims")
local C = {}

local function label_colors_for(M, add_labels)
  local colors = {}
  local has_color = false
  for _, label in ipairs(add_labels or {}) do
    local color = M._label_colors and M._label_colors[tostring(label)]
    if color ~= nil then
      colors[tostring(label)] = color
      has_color = true
    end
  end
  return has_color and colors or nil
end

function C.build_label_request(M, repo, issue_number, add_labels, remove_labels, dedup_key, source_ref)
  return m_claims.attach_issue_claim({
    schema = "github-proxy.label.v1",
    repo = repo,
    target_kind = "issue",
    target_number = issue_number,
    issue_number = issue_number,
    add_labels = add_labels or {},
    remove_labels = remove_labels or {},
    label_colors = label_colors_for(M, add_labels),
    dedup_key = dedup_key,
    source_ref = base_ids.normalize_source_ref(source_ref),
  }, source_ref)
end

function C.build_state_label_request(M, repo, issue_number, to_state, dedup_key_value, source_ref)
  local add_labels, remove_labels = M.state_label_changes(to_state)
  return C.build_label_request(M, repo, issue_number, add_labels, remove_labels, dedup_key_value, source_ref)
end

function C.build_thinking_label_request(M, issue, proposal)
  return C.build_state_label_request(M,
    issue.repo,
    issue.number,
    "thinking",
    tostring(proposal.effect_version or proposal.dedup_key) .. "/label/thinking",
    issue.source_ref
  )
end

function C.build_result_label_request(M, repo, issue_number, reached)
  return C.build_state_label_request(M,
    repo,
    issue_number,
    "ready",
    tostring(reached.proposal_id) .. "/label/" .. tostring(reached.decision),
    reached.source_ref
  )
end

function C.build_intake_enabled_label_request(M, repo, issue_number, candidate)
  local add_labels, remove_labels = M.intake_service_class_label_changes(candidate.service_class)
  table.insert(add_labels, 1, M._enabled_label)
  return C.build_label_request(M,
    repo,
    issue_number,
    add_labels,
    remove_labels,
    base_ids.dedup_key({
      "intake",
      "label",
      tostring(candidate.proposal_id),
      tostring(candidate.dedup_key),
    }),
    candidate.source_ref
  )
end

function C.build_intake_tracking_label_request(M, repo, issue_number, candidate)
  local add_labels, remove_labels = M.intake_service_class_label_changes(candidate.service_class)
  table.insert(add_labels, 1, M._tracking_label)
  return C.build_label_request(M,
    repo,
    issue_number,
    add_labels,
    remove_labels,
    base_ids.dedup_key({
      "intake",
      "label",
      "tracking",
      tostring(candidate.proposal_id),
      tostring(candidate.dedup_key),
    }),
    candidate.source_ref
  )
end

function C.build_implementing_label_request(M, repo, issue_number, ready)
  return C.build_state_label_request(M,
    repo,
    issue_number,
    "implementing",
    base_ids.dedup_key({
      "implement",
      "label",
      "implementing",
      tostring(ready.dedup_key),
    }),
    ready.source_ref
  )
end

function C.build_impl_failed_label_request(M, repo, issue_number, ready, reason)
  return C.build_state_label_request(M,
    repo,
    issue_number,
    "impl-failed",
    base_ids.dedup_key({
      "implement",
      "label",
      "impl-failed",
      tostring(reason or "failed"),
      tostring(ready.dedup_key),
    }),
    ready.source_ref
  )
end

function C.build_reviewing_label_request(M, repo, issue_number, origin, pr_number, source_ref)
  return C.build_state_label_request(M,
    repo,
    issue_number,
    "reviewing",
    base_ids.dedup_key({
      "observe-pr",
      "label",
      tostring(origin.proposal_id),
      tostring(origin.impl_version),
      tostring(pr_number),
    }),
    source_ref
  )
end

function C.build_pr_base_unmanaged_label_request(M, repo, issue_number, origin, pr_number, integration_branch, source_ref)
  return C.build_state_label_request(M,
    repo,
    issue_number,
    "blocked",
    base_ids.dedup_key({
      "observe-pr",
      "label",
      "pr-base-unmanaged",
      tostring(origin.proposal_id),
      tostring(origin.impl_version),
      tostring(pr_number),
      tostring(origin.base_branch),
      tostring(integration_branch),
    }),
    source_ref
  )
end

function C.build_review_result_label_request(M, repo, issue_number, issue_proposal_id, reached, source_ref)
  local to_state = reached.reflection_checkpoint and "review-meta"
    or reached.decision == "approve" and "merge-ready"
    or "fixing"
  return C.build_state_label_request(M,
    repo,
    issue_number,
    to_state,
    base_ids.dedup_key({
      "review-result",
      "label",
      tostring(issue_proposal_id),
      tostring(reached.decision),
      tostring(reached.dedup_key),
    }),
    source_ref
  )
end

function C.build_fix_reviewing_label_request(M, repo, issue_number, fix, new_head_sha, new_version)
  return C.build_state_label_request(M,
    repo,
    issue_number,
    "reviewing",
    base_ids.dedup_key({
      "fix",
      "label",
      tostring(fix.proposal_id),
      tostring(fix.review_dedup_key),
      tostring(new_head_sha),
    }),
    fix.source_ref
  )
end

function C.build_merge_head_reviewing_label_request(M, repo, issue_number, merge_ready, new_head_sha, new_version, source_ref)
  return C.build_state_label_request(M,
    repo,
    issue_number,
    "reviewing",
    base_ids.dedup_key({
      "merge",
      "label",
      "reviewing",
      tostring(merge_ready.proposal_id),
      tostring(new_version),
      tostring(new_head_sha),
    }),
    source_ref
  )
end

return C
