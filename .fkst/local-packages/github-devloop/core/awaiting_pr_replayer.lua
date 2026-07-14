local git_mechanics = require("devloop.git_mechanics")
local entity_lib = require("devloop.entity")
local base_ids = require("devloop.base_ids")
local requests_labels = require("devloop.requests.labels")
local parsers_pr = require("devloop.parsers.pr")
local config = require("devloop.config")
local m_facts = require("devloop.markers.facts")
-- `awaiting-pr` is the issue-side `dependency_wait` twin: poll-reconcile the delegated PR's terminal fact and never drive `github-devloop-pr` internal lifecycle queues; the PR package owns those queues.
local S, replay_fields = {}, require("devloop.replay_fields")
local replayer = require("devloop.replayer")
local forge_validators = require("devloop.forge_validators")
local contract_time = require("contract.time")
local transition_version = require("contract.transition_version")
local autonomy_ledger = require("devloop.autonomy_ledger")
local m_builders = require("devloop.markers.builders")
local devloop_entity_view = require("devloop.github_proxy_entity_view")
function S.install(M)
local child_terminal_states = {
  merged = true,
  ["closed-unmerged"] = true,
  blocked = true,
}
local canonical_pr_is_merged, origin_matches_delegation, canonical_merged_child_state, merged_child_landed_on_upstream
local function log_skip(dept, proposal_id, state, from_state, to_state, outcome, reason)
  return replayer.replay_log_skip(M, dept, proposal_id, state, from_state, to_state, outcome, reason)
end

local function raise_effects(dept, proposal_id, apply_state, version, label_changes, effects)
  return replay_fields.replay_raise_effects(M.log_apply, M.log_raise, dept, proposal_id, apply_state, version, label_changes, effects)
end

local function next_reimplementation_version(version)
  local base = tostring(version or "")
  local next_n = M.version_reimplement_round(base) + 1
  return base .. "/reimplement/" .. tostring(next_n)
end

local function parent_state_for_child_terminal(state, child_state)
  if child_state.state == "merged" then
    return {
      to_state = "merged",
      version = state.version,
      reason = "child-pr-merged",
    }
  end
  if child_state.state == "closed-unmerged" then
    if M.version_reimplement_round(state.version) >= config.max_fix_rounds(M) then
      return {
        to_state = "blocked",
        version = tostring(state.version or "") .. "/blocked/replacement-budget-exhausted",
        reason = "replacement-budget-exhausted",
      }
    end
    return {
      to_state = "ready",
      version = next_reimplementation_version(state.version),
      reason = "child-pr-closed-unmerged",
    }
  end
  return {
    to_state = "blocked",
    version = tostring(state.version or "") .. "/blocked/child-pr-blocked",
    reason = "child-pr-blocked",
  }
end

local function read_delegated_child_pr(dept, issue, delegation)
  local pr_view = devloop_entity_view.fetch_pr_view_origin(issue.repo, delegation.pr_number, nil, {
    force_fresh = true,
    consumer = dept,
  })
  if pr_view.exit_code ~= 0 then
    error("github-devloop: awaiting-pr-child-view-failed: " .. tostring(pr_view.stderr))
  end
  local current_pr = parsers_pr.parse_pr_view_origin(M, pr_view.stdout)
  current_pr.number, current_pr.force_fresh = delegation.pr_number, true
  return current_pr
end

local function child_lineage_matches_delegation(state, delegation, child_state)
  return tostring(delegation.version or "") == tostring(state.version or "")
    and transition_version.strip_suffixes(child_state.version) == transition_version.strip_suffixes(delegation.version)
end

local function autonomy_post_merge_pr(pr)
  if type(pr) ~= "table" or type(pr.status_check_rollup) ~= "table" or #pr.status_check_rollup == 0 then
    return nil
  end
  return pr
end

local function resume_terminal_markers(issue, next_state, delegation, current_pr)
  if next_state.to_state ~= "merged" then
    return ""
  end
  local head_sha = tostring(current_pr and current_pr.head_sha or "")
  if not forge_validators.is_git_sha(head_sha) then
    error("github-devloop: avm-ledger-missing-head-sha: awaiting-pr autonomy result requires merged PR head sha")
  end
  local merge_ready = {
    proposal_id = delegation.proposal_id,
    pr_number = delegation.pr_number,
    version = next_state.version,
    reviewed_head_sha = head_sha,
  }
  local autonomy_record = autonomy_ledger.autonomy_result_record(M, issue.repo, issue.number, merge_ready, issue, autonomy_post_merge_pr(current_pr))
  return "\n" .. m_builders.merged_marker(M, delegation.proposal_id, delegation.pr_number, next_state.version, head_sha, autonomy_record)
    .. "\n" .. autonomy_ledger.autonomy_result_marker(M, autonomy_record)
end

local function build_resume_comment_request(issue, state, next_state, child_state, delegation, current_pr)
  local source_ref = issue.source_ref or entity_lib.issue_source_ref(issue.repo, issue.number)
  local state_marker = M.state_marker(delegation.proposal_id, next_state.to_state, next_state.version)
  return entity_lib.build_entity_comment_request({
    kind = "issue",
    repo = issue.repo,
    number = issue.number,
  }, "github-devloop resumed parent issue from delegated PR child state"
    .. "\n\nChild PR: #" .. tostring(delegation.pr_number)
    .. "\nChild state: " .. tostring(child_state.state)
    .. "\nReason: " .. tostring(next_state.reason)
    .. "\n\n" .. state_marker
    .. resume_terminal_markers(issue, next_state, delegation, current_pr), base_ids.dedup_key({
    "awaiting-pr",
    "resume",
    tostring(delegation.proposal_id),
    tostring(state.version),
    tostring(delegation.pr_number),
    tostring(delegation.delegation),
    tostring(child_state.state),
    tostring(next_state.to_state),
    tostring(next_state.version),
  }), source_ref)
end

function M.replay_awaiting_pr_state(dept, issue, state, row, facts)
  local proposal_id = facts.proposal_id
  if state.state ~= "awaiting-pr" then
    return log_skip(dept, proposal_id, state, row.from_state, row.driving_queue, "skip-foreign(state)", "awaiting-pr replay requires awaiting-pr state")
  end
  local delegation = facts["pr-delegation"] or facts.pr_delegation
  if delegation == nil then
    return log_skip(dept, proposal_id, state, "awaiting-pr", "awaiting-pr", "skip-foreign(pr-delegation-missing)", "awaiting-pr marker is visible without matching pr-delegation")
  end
  if tostring(delegation.proposal_id or "") ~= tostring(proposal_id or "")
    or tostring(delegation.version or "") ~= tostring(state.version or "") then
    return log_skip(dept, proposal_id, state, "awaiting-pr", "awaiting-pr", "skip-stale(pr-delegation-version)", "pr-delegation proposal or version does not match awaiting-pr state")
  end
  local pr_repo, pr_number = entity_lib.parse_pr_proposal_id(delegation.pr_proposal_id or delegation.pr_proposal)
  if pr_repo ~= issue.repo or tostring(pr_number or "") ~= tostring(delegation.pr_number or "") then
    return log_skip(dept, proposal_id, state, "awaiting-pr", "awaiting-pr", "skip-stale(pr-delegation-child)", "pr-delegation child identity is malformed or cross-repo")
  end
  local current_pr = (facts.current_pr ~= nil and facts.current_pr.force_fresh == true) and facts.current_pr or read_delegated_child_pr(dept, issue, delegation)
  local child_state = facts.child_state or facts["child-state"] or require("devloop.entity").current_entity_state(M, current_pr.comments, delegation.proposal_id)
  local canonical_merged_state = canonical_merged_child_state(issue, state, delegation, current_pr)
  if canonical_merged_state ~= nil then
    child_state = canonical_merged_state
  end
  if child_state == nil or child_state.state == nil then
    return log_skip(dept, proposal_id, state, "awaiting-pr", "awaiting-pr", "skip-pending(child-terminal-missing)", "delegated child PR has no trusted terminal marker or canonical merged state")
  end
  if child_terminal_states[child_state.state] ~= true then
    return log_skip(dept, proposal_id, state, "awaiting-pr", "awaiting-pr", "skip-pending(child-nonterminal)", "delegated child PR is not terminal")
  end
  if not child_lineage_matches_delegation(state, delegation, child_state) then
    return log_skip(dept, proposal_id, state, "awaiting-pr", "awaiting-pr", "skip-stale(child-state-lineage)", "child terminal state does not match parent delegation lineage")
  end
  local next_state = parent_state_for_child_terminal(state, child_state)
  if next_state.to_state == "merged" then
    if canonical_merged_state == nil then
      return log_skip(dept, proposal_id, state, "awaiting-pr", "awaiting-pr", "skip-pending(canonical-child-pr-merged-missing)", "delegated child PR has a merged marker but is not canonically merged by GitHub")
    end
    local landed, outcome, reason = merged_child_landed_on_upstream(dept, issue, state, delegation, current_pr)
    if not landed then
      return log_skip(dept, proposal_id, state, "awaiting-pr", "awaiting-pr", outcome, reason)
    end
  end
  local transition = M.versioned_transition_status(state, { "awaiting-pr" }, next_state.to_state, state.version)
  if transition ~= "apply" and transition ~= "idempotent" then
    return log_skip(dept, proposal_id, state, "awaiting-pr", next_state.to_state, M.cas_outcome(state, transition, state.version), next_state.reason)
  end
  if transition == "idempotent" then
    return log_skip(dept, proposal_id, state, "awaiting-pr", next_state.to_state, "skip-idempotent(already at to_state)", "parent issue already reflects delegated child terminal")
  end

  local comment_request = build_resume_comment_request(issue, state, next_state, child_state, delegation, current_pr)
  local label_request = requests_labels.build_state_label_request(M,
    issue.repo,
    issue.number,
    next_state.to_state,
    base_ids.dedup_key({
      "awaiting-pr",
      "label",
      tostring(proposal_id),
      tostring(delegation.pr_number),
      tostring(delegation.delegation),
      tostring(next_state.to_state),
      tostring(next_state.version),
    }),
    issue.source_ref or entity_lib.issue_source_ref(issue.repo, issue.number)
  )
  local add_labels, remove_labels = M.state_label_changes(next_state.to_state)
  M.log_cas_decision(dept, proposal_id, state, "awaiting-pr", next_state.to_state, "applied(" .. next_state.reason .. ")", "delegated child terminal fact matched parent delegation")
  local effects = {
    { queue = "github-proxy.github_issue_comment_request", payload = comment_request },
    { queue = "github-proxy.github_issue_label_request", payload = label_request },
  }
  if next_state.to_state == "merged" and config.write_mode(M) == "real" then
    local close_result = M.gh_issue_close(issue.repo, issue.number, 60)
    if close_result.exit_code ~= 0 then
      error("github-devloop: awaiting-pr-issue-close-failed: " .. tostring(close_result.stderr))
    end
    devloop_entity_view.invalidate_entity_after_write(issue.repo, "issue", issue.number)
  end
  return raise_effects(dept, proposal_id, next_state.to_state, next_state.version, { add = add_labels, remove = remove_labels }, effects)
end

canonical_pr_is_merged = function(current_pr)
  local state = tostring(current_pr and current_pr.state or ""):upper()
  if state == "MERGED" then
    return true
  end
  local merged_at = current_pr and current_pr.merged_at
  if type(merged_at) ~= "string" then
    return false
  end
  return contract_time.iso_timestamp_epoch_seconds(merged_at) ~= nil
end

origin_matches_delegation = function(issue, delegation, current_pr, branches)
  local origin = m_facts.pr_origin_fact(M, current_pr and current_pr.comments)
  if origin == nil
    or origin.pr_native == true
    or tostring(origin.proposal_id or "") ~= tostring(delegation.proposal_id or "")
    or tostring(origin.issue_number or "") ~= tostring(issue.number or "")
    or transition_version.strip_suffixes(origin.impl_version) ~= transition_version.strip_suffixes(delegation.version)
    or tostring(origin.branch or "") ~= tostring(current_pr and current_pr.head_ref_name or "")
    or tostring(origin.base_branch or "") ~= tostring(current_pr and current_pr.base_ref_name or "") then
    return false
  end
  if branches ~= nil and tostring(origin.base_branch or "") ~= tostring(branches.integration or "") then
    return false
  end
  return true
end

canonical_merged_child_state = function(issue, state, delegation, current_pr)
  if not canonical_pr_is_merged(current_pr) then
    return nil
  end
  if not origin_matches_delegation(issue, delegation, current_pr) then
    return nil
  end
  return {
    state = "merged",
    version = delegation.version or state.version,
    proposal_id = delegation.proposal_id,
    head_sha = current_pr.head_sha,
    merge_commit_sha = current_pr.merge_commit_sha,
  }
end

merged_child_landed_on_upstream = function(dept, issue, state, delegation, current_pr)
  local branches = config.branch_config(M)
  if not origin_matches_delegation(issue, delegation, current_pr, branches) then
    return false, "skip-stale(pr-origin-rollup-lineage)", "merged child PR lacks current split-topology origin facts"
  end
  if tostring(branches.integration or "") == tostring(branches.upstream or "") then
    return true
  end
  local merge_commit_sha = tostring(current_pr and current_pr.merge_commit_sha or "")
  if not forge_validators.is_git_sha(merge_commit_sha) then
    return false, "skip-pending(merge-commit-missing)", "canonical merged child PR has no GitHub mergeCommit.oid"
  end
  git_mechanics.fetch_branch(M.git, branches.upstream, "awaiting-pr upstream fetch")
  local upstream_head = git_mechanics.remote_head(M.git, branches.upstream, "awaiting-pr upstream head", "unsafe awaiting-pr upstream head")
  if not git_mechanics.is_ancestor(M.git, merge_commit_sha, upstream_head, "awaiting-pr rollup merge-commit reachability") then
    return false, "skip-pending(rollup-not-landed)", "child PR merge commit is not reachable from upstream branch"
  end
  return true
end

return {
  ["awaiting-pr"] = M.replay_awaiting_pr_state,
}
end

return S
