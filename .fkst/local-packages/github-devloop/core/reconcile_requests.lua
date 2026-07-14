local entity_lib = require("devloop.entity")
local devloop_base = require("devloop.base")
local base_ids = require("devloop.base_ids")
local m_claims = require("devloop.claims")
local requests_labels = require("devloop.requests.labels")
local conv_reconcile = require("devloop.convergence.reconcile")
local S = {}
local comment_strings = require("devloop.strings")

function S.install(M)
local ai_sentinel = "⟦AI:FKST⟧"

function M.build_reconcile_label_request(repo, issue_number, reconcile)
  return requests_labels.build_state_label_request(M,
    repo,
    issue_number,
    "blocked",
    base_ids.dedup_key({
      "reconcile",
      "label",
      tostring(reconcile.dedup_key),
    }),
    reconcile.source_ref
  )
end

function M.build_review_reconcile_label_request(repo, issue_number, review_reconcile)
  return requests_labels.build_state_label_request(M,
    repo,
    issue_number,
    "blocked",
    base_ids.dedup_key({
      "review-reconcile",
      "label",
      tostring(review_reconcile.dedup_key),
    }),
    review_reconcile.source_ref
  )
end

function M.build_fix_reconcile_label_request(repo, issue_number, fix_reconcile)
  return requests_labels.build_state_label_request(M,
    repo,
    issue_number,
    "blocked",
    base_ids.dedup_key({
      "fix-reconcile",
      "label",
      tostring(fix_reconcile.dedup_key),
    }),
    fix_reconcile.source_ref
  )
end

function M.build_reconcile_comment_request(repo, issue_number, reconcile, action, reason, state_version)
  local version = state_version or conv_reconcile.reconcile_state_version(M, reconcile.base_version, reconcile.round)
  local marker = conv_reconcile.reconcile_marker(M, reconcile.proposal_id, reconcile.base_version, reconcile.round, action)
  local state_marker = M.state_marker(reconcile.proposal_id, "blocked", version)
  local safe_reason = devloop_base.neutralize_untrusted_comment_text(reason or "")
  return m_claims.attach_issue_claim({
    schema = "github-proxy.v1",
    repo = repo,
    issue_number = issue_number,
    body = comment_strings.comment_string(M, "reconcile_action_prefix") .. tostring(action)
      .. "\n\n" .. comment_strings.comment_string(M, "reason_block_label") .. "\n" .. safe_reason
      .. "\n\n"
      .. state_marker .. "\n" .. marker
      .. "\n" .. ai_sentinel,
    dedup_key = base_ids.dedup_key({
      "reconcile",
      "comment",
      tostring(reconcile.dedup_key),
    }),
    source_ref = base_ids.normalize_source_ref(reconcile.source_ref),
  }, reconcile.source_ref)
end

function M.build_fix_reconcile_comment_request(repo, issue_number, fix_reconcile, action, reason)
  local version = conv_reconcile.fix_reconcile_state_version(M, fix_reconcile.issue_version)
  local marker = conv_reconcile.fix_reconcile_marker(M, fix_reconcile.proposal_id, fix_reconcile.issue_version, action)
  local state_marker = M.state_marker(fix_reconcile.proposal_id, "blocked", version)
  local safe_reason = devloop_base.neutralize_untrusted_comment_text(reason or "")
  local _, pr_number = devloop_base.parse_pr_source_ref(fix_reconcile.source_ref)
  return entity_lib.build_entity_comment_request({
    kind = "pr",
    repo = repo,
    number = pr_number,
  }, comment_strings.comment_string(M, "fix_reconcile_action_prefix") .. tostring(action)
    .. "\n\n" .. comment_strings.comment_string(M, "reason_block_label") .. "\n" .. safe_reason
    .. "\n\n"
    .. state_marker .. "\n" .. marker
    .. "\n" .. ai_sentinel, base_ids.dedup_key({
    "fix-reconcile",
    "comment",
    tostring(fix_reconcile.dedup_key),
  }), fix_reconcile.source_ref)
end

function M.build_review_reconcile_comment_request(repo, issue_number, review_reconcile, action, reason, state_version)
  local version = state_version or conv_reconcile.review_reconcile_state_version(M, review_reconcile.issue_version, review_reconcile.round)
  local marker = conv_reconcile.review_reconcile_marker(M, review_reconcile.proposal_id, review_reconcile.issue_version, review_reconcile.round, action)
  local state_marker = M.state_marker(review_reconcile.proposal_id, "blocked", version)
  local safe_reason = devloop_base.neutralize_untrusted_comment_text(reason or "")
  local _, pr_number = devloop_base.parse_pr_source_ref(review_reconcile.source_ref)
  return entity_lib.build_entity_comment_request({
    kind = "pr",
    repo = repo,
    number = pr_number,
  }, comment_strings.comment_string(M, "review_reconcile_action_prefix") .. tostring(action)
    .. "\n\n" .. comment_strings.comment_string(M, "reason_block_label") .. "\n" .. safe_reason
    .. "\n\n"
    .. state_marker .. "\n" .. marker
    .. "\n" .. ai_sentinel, base_ids.dedup_key({
    "review-reconcile",
    "comment",
    tostring(review_reconcile.dedup_key),
  }), review_reconcile.source_ref)
end
end

return S
