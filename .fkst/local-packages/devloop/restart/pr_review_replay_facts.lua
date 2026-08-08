local devloop_base = require("devloop.base")
local entity_lib = require("devloop.entity")
local base_ids = require("devloop.base_ids")
local strings = require("contract.strings")
local parsers_misc = require("devloop.parsers.misc")
local payloads_builders = require("devloop.payloads.builders")
local m_facts = require("devloop.markers.facts")
local forge_validators = require("devloop.forge_validators")
local transition_version = require("contract.transition_version")

local S = {}

local function review_proposal_from_dedup(dedup_key)
  return devloop_base.pr_review_proposal_id_from_consensus_dedup_key(dedup_key)
end

local function same_review_result_dedup(left, right)
  local left_canonical = devloop_base.canonical_pr_review_consensus_dedup_key(left)
  local right_canonical = devloop_base.canonical_pr_review_consensus_dedup_key(right)
  return left_canonical ~= nil and left_canonical == right_canonical
end

local function review_meta_fact_from_converge_marker(M, comments, issue_proposal_id, issue_version)
  if type(comments) ~= "table" then
    return nil
  end
  local marker_pattern = "<!%-%- fkst:github%-devloop:review%-converge%-round:v1.-%-%->"
  local heartbeat_version = M.liveness_heartbeat_version(issue_version, M.liveness_signal_producer_contract("review-converge-round"))
  local best = nil
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(comments)) do
    for marker in parsers_misc._comment_body(comment):gmatch(marker_pattern) do
      local marker_issue = marker:match('issue_proposal="([^"]+)"')
      local marker_version = marker:match('version="([^"]*)"')
      local review_proposal = marker:match('proposal="([^"]+)"')
      local consensus_dedup = marker:match('dedup="([^"]*)"')
      local round = tonumber(marker:match('round="(%d+)"'))
      local _, pr_number, review_version = devloop_base.parse_pr_review_proposal_id(review_proposal)
      local repo = base_ids.parse_proposal_id(issue_proposal_id)
      if marker_issue == tostring(issue_proposal_id)
        and marker_version == tostring(heartbeat_version)
        and review_version == tostring(heartbeat_version)
        and repo ~= nil
        and forge_validators.is_positive_pr_number(pr_number)
        and strings.is_path_safe_key(review_proposal, M._max_key_len)
        and strings.is_bounded_string(consensus_dedup, M._max_dedup_len)
        and (best == nil or (round or 0) > (best.n or 0)) then
        best = {
          proposal_id = review_proposal,
          dedup_key = consensus_dedup,
          source_ref = entity_lib.pr_source_ref(repo, pr_number),
          pr_number = tonumber(pr_number),
          n = (round or 0) + 1,
        }
      end
    end
  end
  return best
end

local function build_ops(ctx)
  local ops = {}

  function ops.review_meta_replay_fact_from_state(comments, issue_proposal_id, issue_version, pr_number, head_sha, n)
    local repo = base_ids.parse_proposal_id(issue_proposal_id)
    if repo == nil
      or not forge_validators.is_positive_pr_number(pr_number)
      or not forge_validators.is_git_sha(head_sha)
      or not strings.is_bounded_string(issue_version, ctx._max_dedup_len) then
      return nil
    end
    local marker_pattern = "<!%-%- fkst:github%-devloop:review%-meta:v1.-%-%->"
    for _, comment in ipairs(parsers_misc._trusted_marker_comments(comments)) do
      for marker in parsers_misc._comment_body(comment):gmatch(marker_pattern) do
        local marker_issue = marker:match('proposal="([^"]+)"')
        local marker_dedup = marker:match('dedup="([^"]*)"')
        local review_proposal = review_proposal_from_dedup(marker_dedup)
        local _, review_pr_number, review_version, reviewed_head_sha = devloop_base.parse_pr_review_proposal_id(review_proposal)
        if marker_issue == tostring(issue_proposal_id)
          and tostring(review_pr_number or "") == tostring(pr_number)
          and review_version == transition_version.safe_version_segment(ctx._strip_latest_fix_version_suffix(issue_version))
          and tostring(reviewed_head_sha or "") == tostring(head_sha)
          and devloop_base.is_safe_pr_review_result_ref(review_proposal, marker_dedup) then
          return {
            proposal_id = review_proposal,
            dedup_key = marker_dedup,
            source_ref = entity_lib.pr_source_ref(repo, pr_number),
            pr_number = tonumber(pr_number),
            n = tonumber(n) or 0,
          }
        end
      end
    end
    marker_pattern = "<!%-%- fkst:github%-devloop:fix%-reflection:v1.-%-%->"
    for _, comment in ipairs(parsers_misc._trusted_marker_comments(comments)) do
      for marker in parsers_misc._comment_body(comment):gmatch(marker_pattern) do
        local marker_issue = marker:match('proposal="([^"]+)"')
        local marker_dedup = marker:match('dedup="([^"]*)"')
        local verdict = marker:match('verdict="([^"]+)"')
        local marker_version = marker:match('version="([^"]*)"')
        local round = tonumber(marker:match('fix_round="(%d+)"'))
        local review_proposal = review_proposal_from_dedup(marker_dedup)
        local canonical_marker_dedup = devloop_base.canonical_pr_review_consensus_dedup_key(marker_dedup)
        local _, review_pr_number, review_version, reviewed_head_sha = devloop_base.parse_pr_review_proposal_id(review_proposal)
        if marker_issue == tostring(issue_proposal_id)
          and verdict == "checkpoint"
          and marker_version == tostring(issue_version)
          and tostring(review_pr_number or "") == tostring(pr_number)
          and review_version == transition_version.safe_version_segment(ctx._strip_latest_fix_version_suffix(issue_version))
          and tostring(reviewed_head_sha or "") == tostring(head_sha)
          and devloop_base.is_safe_pr_review_result_ref(review_proposal, marker_dedup) then
          local reject_fact = m_facts.review_reject_fact(comments, issue_proposal_id, issue_version)
          if reject_fact == nil
            or tostring(reject_fact.review_proposal_id or "") ~= tostring(review_proposal)
            or not same_review_result_dedup(reject_fact.review_dedup_key, marker_dedup)
            or not strings.is_bounded_string(reject_fact.blocking_gap, ctx._max_blocking_gap_len) then
            return nil
          end
          local reflection_dedup = payloads_builders.fix_reflection_dedup_key(issue_proposal_id, issue_version, pr_number, round, canonical_marker_dedup)
          return {
            proposal_id = review_proposal,
            dedup_key = reflection_dedup,
            review_dedup_key = canonical_marker_dedup,
            source_ref = entity_lib.pr_source_ref(repo, pr_number),
            pr_number = tonumber(pr_number),
            n = tonumber(n) or 0,
            mode = "fix-reflection",
            fix_round = round,
            blocking_gap = reject_fact.blocking_gap,
          }
        end
      end
    end
    local reject_fact = m_facts.review_reject_fact(comments, issue_proposal_id, issue_version)
    local _, reject_pr_number, _, reviewed_head_sha = devloop_base.parse_pr_review_proposal_id(reject_fact and reject_fact.review_proposal_id)
    if reject_fact ~= nil
      and tostring(reject_pr_number or "") == tostring(pr_number)
      and tostring(reviewed_head_sha or "") == tostring(head_sha)
      and devloop_base.is_safe_pr_review_result_ref(reject_fact.review_proposal_id, reject_fact.review_dedup_key) then
      return {
        proposal_id = reject_fact.review_proposal_id,
        dedup_key = reject_fact.review_dedup_key,
        source_ref = entity_lib.pr_source_ref(repo, pr_number),
        pr_number = tonumber(pr_number),
        n = tonumber(n) or 0,
      }
    end
    return nil
  end

  function ops.review_meta_replay_fact(comments, issue_proposal_id, issue_version, pr_number, head_sha)
    local converge_fact = review_meta_fact_from_converge_marker(ctx, comments, issue_proposal_id, issue_version)
    if converge_fact ~= nil then
      return converge_fact
    end
    return ops.review_meta_replay_fact_from_state(comments, issue_proposal_id, issue_version, pr_number, head_sha, 0)
  end

  function ops.fixing_replay_feedback_fact(comments, issue_proposal_id, issue_version)
    local reject_fact = m_facts.review_reject_fact(comments, issue_proposal_id, issue_version)
    if reject_fact ~= nil then
      return reject_fact
    end
    local meta_fix_fact = m_facts.review_meta_fix_fact(comments, issue_proposal_id, issue_version)
    if meta_fix_fact ~= nil then
      return meta_fix_fact
    end
    return m_facts.merge_gate_fix_fact(comments, issue_proposal_id, issue_version)
  end

  ctx.review_meta_replay_fact_from_state = ops.review_meta_replay_fact_from_state
  ctx.review_meta_replay_fact = ops.review_meta_replay_fact
  ctx.fixing_replay_feedback_fact = ops.fixing_replay_feedback_fact

  return ops
end

S.install = function(ctx)
  local ops = build_ops(ctx)
  return {
    review_meta_replay_fact_from_state = ops.review_meta_replay_fact_from_state,
    review_meta_replay_fact = ops.review_meta_replay_fact,
    fixing_replay_feedback_fact = ops.fixing_replay_feedback_fact,
  }
end

return S
