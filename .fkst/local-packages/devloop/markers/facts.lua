local entity_lib = require("devloop.entity")
local devloop_base = require("devloop.base")
local base_ids = require("devloop.base_ids")
local strings = require("contract.strings")
local parsers_misc = require("devloop.parsers.misc")
local C = {}
local forge_validators = require("devloop.forge_validators")
local contract_time = require("contract.time")
local transition_version = require("contract.transition_version")
local autonomy_ledger = require("devloop.autonomy_ledger")
local shared = require("devloop.markers.shared")
local m_builders = require("devloop.markers.builders")

local valid_round = shared.valid_round
local marker_attr = shared.marker_attr
local decode_marker_attr = shared.decode_marker_attr

local function review_result_fact_from_marker(M, marker, comment, issue_proposal_id, issue_version, expected_decision)
  local review_proposal = marker_attr(marker, "proposal")
  local marker_issue = marker_attr(marker, "issue_proposal")
  local decision = marker_attr(marker, "decision")
  local review_dedup = marker_attr(marker, "dedup")
  local _, _, review_version, reviewed_head_sha = devloop_base.parse_pr_review_proposal_id(review_proposal)
  local expected_dedup = review_proposal ~= nil and ("consensus:" .. tostring(review_proposal) .. "/review") or nil
  if marker_issue == tostring(issue_proposal_id)
    and (expected_decision == nil or decision == expected_decision)
    and (decision == "approve" or decision == "reject")
    and review_version == transition_version.safe_version_segment(M._strip_latest_fix_version_suffix(issue_version))
    and review_dedup == expected_dedup
    and strings.is_bounded_string(review_dedup, M._max_dedup_len)
    and forge_validators.is_git_sha(reviewed_head_sha) then
    local fact = {
      review_proposal_id = review_proposal,
      review_dedup_key = review_dedup,
      reviewed_head_sha = reviewed_head_sha,
      decision = decision,
      review_reason = parsers_misc._comment_body(M, comment),
      comment_created_at = parsers_misc._comment_created_at(M, comment),
    }
    if decision == "reject" then
      local marker_fix_round = valid_round(marker_attr(marker, "fix_round"))
      if marker_fix_round == nil or marker_fix_round ~= M.version_fix_round(issue_version) then
        return nil
      end
      local gap = decode_marker_attr(marker_attr(marker, "gap"))
      if gap == nil or not strings.is_bounded_string(gap, M._max_blocking_gap_len) then
        return nil
      end
      fact.blocking_gap = gap
      fact.fix_round = marker_fix_round
    end
    return fact
  end
  return nil
end

local function review_proposal_from_dedup(dedup_key)
  return tostring(dedup_key or ""):match("^consensus:(.+)/review$")
end

function C.intake_decision_fact(M, comments, issue_proposal_id)
  if type(comments) ~= "table" then
    return nil
  end
  local marker_pattern = "<!%-%- fkst:github%-devloop:intake%-decision:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      local marker_issue = marker:match('proposal="([^"]+)"')
      local decision = marker:match('decision="([^"]+)"')
      local service_class = marker:match('class="([^"]+)"')
      local dedup = marker:match('dedup="([^"]*)"')
      if marker_issue == tostring(issue_proposal_id)
        and (decision == "enable" or decision == "track" or decision == "decline" or decision == "escalate-to-class")
        and shared.is_intake_service_class(service_class)
        and strings.is_bounded_string(dedup, M._max_dedup_len) then
        return {
          proposal_id = marker_issue,
          decision = decision,
          service_class = shared.normalize_intake_service_class(service_class),
          dedup_key = dedup,
          comment_created_at = parsers_misc._comment_created_at(M, comment),
        }
      end
    end
  end
  return nil
end

function C.has_intake_decision_marker(M, comments, issue_proposal_id)
  return C.intake_decision_fact(M, comments, issue_proposal_id) ~= nil
end

function C.review_reject_fact(M, comments, issue_proposal_id, issue_version)
  if type(comments) ~= "table" then
    return nil
  end
  local marker_pattern = "<!%-%- fkst:github%-devloop:review%-result:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      local fact = review_result_fact_from_marker(M, marker, comment, issue_proposal_id, issue_version, "reject")
      if fact ~= nil then
        return fact
      end
    end
  end
  return nil
end

function C.review_result_fact(M, comments, issue_proposal_id, issue_version, expected_decision)
  if type(comments) ~= "table" then
    return nil
  end
  local marker_pattern = "<!%-%- fkst:github%-devloop:review%-result:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      local fact = review_result_fact_from_marker(M, marker, comment, issue_proposal_id, issue_version, expected_decision)
      if fact ~= nil then
        return fact
      end
    end
  end
  return nil
end

local function bounded_marker_line(M, value, limit)
  local text = tostring(value or ""):gsub("%c", " "):gsub("%s+", " ")
  text = text:gsub("^%s+", ""):gsub("%s+$", "")
  if text == "" then
    return nil
  end
  local cap = limit or M._max_blocking_gap_len
  if #text > cap then
    text = base_ids.truncate_utf8(text, cap)
  end
  return text
end

local function highest_state_fix_round(M, body, issue_proposal_id)
  local highest = nil
  local marker_pattern = "<!%-%- fkst:github%-devloop:state:v1.-%-%->"
  for marker in tostring(body or ""):gmatch(marker_pattern) do
    if marker_attr(marker, "proposal") == tostring(issue_proposal_id) then
      local round = M.version_fix_round(marker_attr(marker, "version"))
      if highest == nil or round > highest then
        highest = round
      end
    end
  end
  return highest
end

function C.review_prior_round_ledger(M, comments, issue_proposal_id, issue_version)
  if type(comments) ~= "table" then
    return nil
  end
  local latest_reject = nil
  local latest_fix = nil
  local marker_pattern = "<!%-%- fkst:github%-devloop:review%-result:v1.-%-%->"
  local rejected_fix_version = M._strip_latest_fix_version_suffix(issue_version)
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    local body = parsers_misc._comment_body(M, comment)
    for marker in body:gmatch(marker_pattern) do
      local fact = review_result_fact_from_marker(M, marker, comment, issue_proposal_id, rejected_fix_version, "reject")
      if fact ~= nil and (latest_reject == nil or fact.fix_round > latest_reject.fix_round) then
        latest_reject = {
          gap = fact.blocking_gap,
          fix_round = fact.fix_round,
          created_at = parsers_misc._comment_created_at(M, comment),
        }
      end
    end
    local fix_summary = body:match("\nFix%-round summary:%s*([^\n]+)") or body:match("^Fix%-round summary:%s*([^\n]+)")
    fix_summary = bounded_marker_line(M, fix_summary, M._max_review_ledger_len)
    local fix_summary_round = highest_state_fix_round(M, body, issue_proposal_id)
    if fix_summary ~= nil
      and fix_summary_round ~= nil
      and (latest_fix == nil or fix_summary_round > latest_fix.fix_round) then
      latest_fix = {
        summary = fix_summary,
        fix_round = fix_summary_round,
        created_at = parsers_misc._comment_created_at(M, comment),
      }
    end
  end
  if latest_reject == nil then
    return nil
  end
  local lines = {
    "Last named blocking gap: " .. latest_reject.gap,
  }
  if latest_fix ~= nil then
    table.insert(lines, "Latest fix-round summary: " .. latest_fix.summary)
  end
  local ledger = table.concat(lines, "\n")
  if #ledger > M._max_review_ledger_len then
    ledger = base_ids.truncate_utf8(ledger, M._max_review_ledger_len)
  end
  return devloop_base.neutralize_untrusted_prompt_text(ledger)
end

function C.review_meta_fix_fact(M, comments, issue_proposal_id, issue_version)
  if type(comments) ~= "table" then
    return nil
  end
  local marker_pattern = "<!%-%- fkst:github%-devloop:review%-meta:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      local marker_issue = marker:match('proposal="([^"]+)"')
      local marker_dedup = marker:match('dedup="([^"]*)"')
      local action = marker:match('action="([^"]+)"')
      local version = marker:match('version="([^"]*)"')
      local gap = decode_marker_attr(marker_attr(marker, "gap"))
      if marker_issue == tostring(issue_proposal_id)
        and marker_dedup ~= nil
        and action == "fix"
        and version == tostring(issue_version)
        and strings.is_bounded_string(gap, M._max_blocking_gap_len) then
        local review_proposal = review_proposal_from_dedup(marker_dedup)
        local _, _, _, reviewed_head_sha = devloop_base.parse_pr_review_proposal_id(review_proposal)
        return {
          review_proposal_id = review_proposal,
          review_dedup_key = marker_dedup,
          reviewed_head_sha = reviewed_head_sha,
          review_reason = parsers_misc._comment_body(M, comment),
          blocking_gap = gap,
        }
      end
    end
  end
  return nil
end

function C.review_meta_decision_fact(M, comments, issue_proposal_id, issue_version)
  if type(comments) ~= "table" then
    return nil
  end
  local expected_lineage = transition_version.strip_suffixes(issue_version)
  local marker_pattern = "<!%-%- fkst:github%-devloop:review%-meta:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      local marker_issue = marker_attr(marker, "proposal")
      local marker_dedup = marker_attr(marker, "dedup")
      local action = marker_attr(marker, "action")
      local version = marker_attr(marker, "version")
      local gap = decode_marker_attr(marker_attr(marker, "gap"))
      local marker_lineage = transition_version.strip_suffixes(version)
      if marker_issue == tostring(issue_proposal_id)
        and marker_lineage == expected_lineage
        and (action == "fix" or action == "block" or action == "spec-amendment")
        and strings.is_bounded_string(marker_dedup, M._max_dedup_len) then
        local review_proposal = review_proposal_from_dedup(marker_dedup)
        local _, _, _, reviewed_head_sha = devloop_base.parse_pr_review_proposal_id(review_proposal)
        if review_proposal ~= nil and forge_validators.is_git_sha(reviewed_head_sha) then
          if action == "fix" and (gap == nil or not strings.is_bounded_string(gap, M._max_blocking_gap_len)) then
            return nil
          end
          return {
            review_proposal_id = review_proposal,
            review_dedup_key = marker_dedup,
            reviewed_head_sha = reviewed_head_sha,
            action = action,
            version = version,
            review_reason = parsers_misc._comment_body(M, comment),
            blocking_gap = gap,
            comment_created_at = parsers_misc._comment_created_at(M, comment),
          }
        end
      end
    end
  end
  return nil
end

local function merge_gate_fix_fact_matches_bindings(fact, opts)
  if type(opts) ~= "table" then
    return true
  end
  local baseline_bound = opts.match_gate_baseline_sha == true or opts.gate_baseline_sha ~= nil
  return (opts.review_proposal_id == nil or fact.review_proposal_id == tostring(opts.review_proposal_id))
    and (opts.review_dedup_key == nil or fact.review_dedup_key == tostring(opts.review_dedup_key))
    and (not baseline_bound
      or (opts.gate_baseline_sha ~= nil and fact.gate_baseline_sha == tostring(opts.gate_baseline_sha))
      or (opts.gate_baseline_sha == nil and fact.gate_baseline_sha == nil))
end
function C.merge_gate_fix_fact(M, comments, issue_proposal_id, issue_version, opts)
  if type(comments) ~= "table" then
    return nil
  end
  local marker_pattern = "<!%-%- fkst:github%-devloop:merge%-gate:v1.-%-%->"
  local first_fact = nil
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      local marker_issue = marker:match('proposal="([^"]+)"')
      local marker_version = marker:match('version="([^"]*)"')
      local marker_review_proposal = marker:match('review_proposal="([^"]+)"')
      local marker_review_dedup = marker:match('review_dedup="([^"]*)"')
      local marker_head_sha = marker:match('head_sha="([^"]+)"')
      local marker_gate_baseline_sha = marker:match('gate_baseline_sha="([^"]+)"')
      local marker_predecessor_set = marker:match('predecessor_set="([^"]+)"')
      local marker_reason = marker:match('reason="([^"]+)"')
      if marker_issue == tostring(issue_proposal_id)
        and marker_version == tostring(issue_version)
        and strings.is_bounded_string(marker_review_proposal, M._max_key_len)
        and strings.is_bounded_string(marker_review_dedup, M._max_dedup_len)
        and strings.is_bounded_string(marker_reason, M._max_key_len)
        and forge_validators.is_git_sha(marker_head_sha)
        and (marker_gate_baseline_sha == nil or forge_validators.is_git_sha(marker_gate_baseline_sha))
        and (marker_predecessor_set == nil or strings.is_path_safe_key(marker_predecessor_set, M._max_dedup_len)) then
        local fact = {
          review_proposal_id = marker_review_proposal,
          review_dedup_key = marker_review_dedup,
          reviewed_head_sha = marker_head_sha,
          gate_baseline_sha = marker_gate_baseline_sha,
          predecessor_set = marker_predecessor_set,
          reason = marker_reason,
          review_reason = parsers_misc._comment_body(M, comment),
        }
        if first_fact == nil then
          first_fact = fact
        end
        if merge_gate_fix_fact_matches_bindings(fact, opts) then
          return fact
        end
      end
    end
  end
  if type(opts) == "table" then
    return nil
  end
  return first_fact
end

function C.merge_ready_fact(M, comments, issue_proposal_id, issue_version, pr_number, head_sha)
  if type(comments) ~= "table" then
    return nil
  end
  local marker_pattern = "<!%-%- fkst:github%-devloop:merge%-ready:v1.-%-%->"
  local best = nil
  local best_seconds = nil
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      local marker_issue = marker:match('proposal="([^"]+)"')
      local marker_pr = marker:match('pr="([^"]+)"')
      local marker_version = marker:match('version="([^"]*)"')
      local marker_review_proposal = marker:match('review_proposal="([^"]+)"')
      local marker_review_dedup = marker:match('review_dedup="([^"]*)"')
      local marker_head_sha = marker:match('head_sha="([^"]+)"')
      if marker_issue == tostring(issue_proposal_id)
        and (pr_number == nil or tostring(marker_pr) == tostring(pr_number))
        and tostring(marker_version) == tostring(issue_version)
        and (head_sha == nil or tostring(marker_head_sha) == tostring(head_sha))
        and strings.is_bounded_string(marker_review_proposal, M._max_key_len)
        and strings.is_bounded_string(marker_review_dedup, M._max_dedup_len)
        and forge_validators.is_git_sha(marker_head_sha) then
        local candidate = {
          proposal_id = marker_issue,
          pr_number = tonumber(marker_pr),
          version = marker_version,
          review_proposal_id = marker_review_proposal,
          review_dedup_key = marker_review_dedup,
          head_sha = marker_head_sha,
          reviewed_head_sha = marker_head_sha,
          comment_created_at = parsers_misc._comment_created_at(M, comment),
        }
        local candidate_seconds = contract_time.iso_timestamp_epoch_seconds(candidate.comment_created_at) or 0
        if best == nil or candidate_seconds >= best_seconds then
          best = candidate
          best_seconds = candidate_seconds
        end
      end
    end
  end
  return best
end

function C.high_risk_review_evidence_fact(M, comments, issue_proposal_id, issue_version, pr_number, head_sha, review_proposal_id, review_dedup_key, paths_digest)
  if type(comments) ~= "table" then
    return nil
  end
  if not strings.is_bounded_string(paths_digest, M._max_key_len) then
    return nil
  end
  local marker_pattern = "<!%-%- fkst:github%-devloop:high%-risk%-review%-evidence:v1.-%-%->"
  local best = nil
  local best_seconds = nil
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      local marker_issue = marker_attr(marker, "proposal")
      local marker_version = marker_attr(marker, "version")
      local marker_pr = marker_attr(marker, "pr")
      local marker_head_sha = marker_attr(marker, "head_sha")
      local marker_review_proposal = marker_attr(marker, "review_proposal")
      local marker_review_dedup = marker_attr(marker, "review_dedup")
      local marker_risk = marker_attr(marker, "risk")
      local marker_angle = marker_attr(marker, "angle")
      local marker_verdict = marker_attr(marker, "verdict")
      local marker_paths_digest = marker_attr(marker, "paths_digest")
      local marker_angle_digest = marker_attr(marker, "angle_digest")
      if marker_issue == tostring(issue_proposal_id)
        and marker_version == tostring(issue_version)
        and tostring(marker_pr or "") == tostring(pr_number or "")
        and tostring(marker_head_sha or "") == tostring(head_sha or "")
        and tostring(marker_review_proposal or "") == tostring(review_proposal_id or "")
        and tostring(marker_review_dedup or "") == tostring(review_dedup_key or "")
        and marker_risk == "high"
        and marker_angle == "high-risk"
        and marker_verdict == "approve"
        and tostring(marker_paths_digest or "") == tostring(paths_digest)
        and forge_validators.is_git_sha(marker_head_sha)
        and strings.is_bounded_string(marker_review_proposal, M._max_key_len)
        and strings.is_bounded_string(marker_review_dedup, M._max_dedup_len)
        and strings.is_bounded_string(marker_paths_digest, M._max_key_len)
        and strings.is_bounded_string(marker_angle_digest, M._max_key_len) then
        local candidate = {
          proposal_id = marker_issue,
          version = marker_version,
          pr_number = tonumber(marker_pr),
          head_sha = marker_head_sha,
          reviewed_head_sha = marker_head_sha,
          review_proposal_id = marker_review_proposal,
          review_dedup_key = marker_review_dedup,
          risk = marker_risk,
          angle = marker_angle,
          verdict = marker_verdict,
          paths_digest = marker_paths_digest,
          angle_digest = marker_angle_digest,
          comment_created_at = parsers_misc._comment_created_at(M, comment),
        }
        local candidate_seconds = contract_time.iso_timestamp_epoch_seconds(candidate.comment_created_at) or 0
        if best == nil or candidate_seconds >= best_seconds then
          best = candidate
          best_seconds = candidate_seconds
        end
      end
    end
  end
  return best
end

function C.review_result_approval_matches_event(M, comments, merge_ready)
  if type(comments) ~= "table" or type(merge_ready) ~= "table" then
    return false, "missing-review-result-approve"
  end
  local marker_pattern = "<!%-%- fkst:github%-devloop:review%-result:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      local review_proposal = marker:match('proposal="([^"]+)"')
      local issue_proposal = marker:match('issue_proposal="([^"]+)"')
      local decision = marker:match('decision="([^"]+)"')
      local review_dedup = marker:match('dedup="([^"]*)"')
      local _, review_pr_number, review_version, reviewed_head_sha = devloop_base.parse_pr_review_proposal_id(review_proposal)
      if tostring(review_proposal or "") == tostring(merge_ready.review_proposal_id or "")
        and tostring(issue_proposal or "") == tostring(merge_ready.proposal_id or "")
        and decision == "approve"
        and tostring(review_dedup or "") == tostring(merge_ready.review_dedup_key or "")
        and tostring(review_pr_number or "") == tostring(merge_ready.pr_number or "")
        and tostring(reviewed_head_sha or "") == tostring(merge_ready.reviewed_head_sha or "")
        and tostring(review_version or "") == transition_version.safe_version_segment(merge_ready.version) then
        return true, "review-result-approve"
      end
    end
  end
  return false, "missing-review-result-approve"
end

local function review_proposal_version_matches_merge_ready(review_version, merge_ready_version, review_dedup_key)
  local merge_text = tostring(merge_ready_version or "")
  if tostring(review_version or "") == transition_version.safe_version_segment(merge_text) then
    return true
  end
  local base = merge_text:match("^(.-)/review%-loop/%d+")
  if base == nil then
    return false
  end
  return tostring(review_dedup_key or ""):find("review%-meta", 1) ~= nil
    and tostring(review_version or "") == transition_version.safe_version_segment(base)
    and merge_text:find("/review%-meta%-action/", 1) ~= nil
end

function C.merge_ready_approval_matches_event(M, fact, merge_ready)
  if type(fact) ~= "table" or type(merge_ready) ~= "table" then
    return false, "missing-merge-ready-approval"
  end
  if tostring(fact.proposal_id or "") ~= tostring(merge_ready.proposal_id or "")
    or tostring(fact.pr_number or "") ~= tostring(merge_ready.pr_number or "")
    or tostring(fact.version or "") ~= tostring(merge_ready.version or "")
    or tostring(fact.review_proposal_id or "") ~= tostring(merge_ready.review_proposal_id or "")
    or tostring(fact.review_dedup_key or "") ~= tostring(merge_ready.review_dedup_key or "")
    or tostring(fact.head_sha or "") ~= tostring(merge_ready.reviewed_head_sha or "") then
    return false, "merge-ready-approval-mismatch"
  end

  local entity = entity_lib.parse_entity_proposal_id(merge_ready.proposal_id)
  local entity_repo = entity and entity.repo or nil
  local review_repo, review_pr_number, review_version, review_head_sha = devloop_base.parse_pr_review_proposal_id(fact.review_proposal_id)
  local expected_review_repo = entity_repo and devloop_base.safe_pr_review_repo_segment(entity_repo) or nil
  if review_repo == nil
    or tostring(review_repo) ~= tostring(expected_review_repo or "")
    or tostring(review_pr_number) ~= tostring(merge_ready.pr_number or "")
    or tostring(review_head_sha) ~= tostring(merge_ready.reviewed_head_sha or "") then
    return false, "merge-ready-review-proposal-mismatch"
  end
  if not review_proposal_version_matches_merge_ready(review_version, merge_ready.version, merge_ready.review_dedup_key) then
    return false, "merge-ready-review-proposal-version-mismatch"
  end

  return true, "merge-ready-approval"
end

function C.merging_fact(M, comments, issue_proposal_id, pr_number, version, head_sha)
  if type(comments) ~= "table" then
    return nil
  end
  local marker_pattern = "<!%-%- fkst:github%-devloop:merging:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      local marker_issue = marker:match('proposal="([^"]+)"')
      local marker_pr = marker:match('pr="([^"]+)"')
      local marker_version = marker:match('version="([^"]*)"')
      local marker_head_sha = marker:match('head_sha="([^"]+)"')
      if marker_issue == tostring(issue_proposal_id)
        and tostring(marker_pr) == tostring(pr_number)
        and tostring(marker_version) == tostring(version)
        and (head_sha == nil or tostring(marker_head_sha) == tostring(head_sha))
        and forge_validators.is_git_sha(marker_head_sha) then
        return {
          proposal_id = marker_issue,
          pr_number = tonumber(marker_pr),
          version = marker_version,
          head_sha = marker_head_sha,
          comment_created_at = parsers_misc._comment_created_at(M, comment),
        }
      end
    end
  end
  return nil
end

function C.merged_fact(M, comments, issue_proposal_id, pr_number, version)
  if type(comments) ~= "table" then
    return nil
  end
  local marker_pattern = "<!%-%- fkst:github%-devloop:merged:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      local marker_issue = marker:match('proposal="([^"]+)"')
      local marker_pr = marker:match('pr="([^"]+)"')
      local marker_version = marker:match('version="([^"]*)"')
      local marker_head_sha = marker:match('head_sha="([^"]+)"')
      if marker_issue == tostring(issue_proposal_id)
        and tostring(marker_pr) == tostring(pr_number)
        and (version == nil or tostring(marker_version) == tostring(version))
        and forge_validators.is_git_sha(marker_head_sha) then
        local autonomy_result = nil
        if marker:find('autonomy_result="v1"', 1, true) ~= nil then
          autonomy_result = autonomy_ledger.autonomy_result_record_from_marker(M, marker, comment, marker_issue, marker_pr, marker_version, marker_head_sha)
        end
        return {
          proposal_id = marker_issue,
          pr_number = tonumber(marker_pr),
          version = marker_version,
          head_sha = marker_head_sha,
          autonomy_result = autonomy_result,
          comment_created_at = parsers_misc._comment_created_at(M, comment),
        }
      end
    end
  end
  return nil
end

function C.has_merged_marker(M, comments, issue_proposal_id, pr_number, version, head_sha)
  local fact = C.merged_fact(M, comments, issue_proposal_id, pr_number, version)
  return fact ~= nil and tostring(fact.head_sha) == tostring(head_sha)
end

function C.has_review_result_marker(M, comments, review_proposal_id, issue_proposal_id, decision, dedup_key)
  if type(comments) ~= "table" then
    return false
  end
  local marker_pattern = "<!%-%- fkst:github%-devloop:review%-result:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      if marker_attr(marker, "proposal") == tostring(review_proposal_id)
        and marker_attr(marker, "issue_proposal") == tostring(issue_proposal_id)
        and marker_attr(marker, "decision") == tostring(decision)
        and marker_attr(marker, "dedup") == tostring(dedup_key) then
        return true
      end
    end
  end
  return false
end

function C.has_review_meta_marker(M, comments, issue_proposal_id, dedup_key)
  if type(comments) ~= "table" then
    return false
  end

  local marker_pattern = "<!%-%- fkst:github%-devloop:review%-meta:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      local marker_proposal = marker:match('proposal="([^"]+)"')
      local marker_dedup = marker:match('dedup="([^"]*)"')
      if marker_proposal == tostring(issue_proposal_id) and marker_dedup == tostring(dedup_key) then
        return true
      end
    end
  end
  return false
end

function C.has_fix_marker(M, comments, issue_proposal_id, review_proposal_id, review_dedup_key, old_head_sha, new_head_sha)
  if type(comments) ~= "table" then
    return false
  end
  local needle = m_builders.fix_marker(M, issue_proposal_id, review_proposal_id, review_dedup_key, old_head_sha, new_head_sha)
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    if parsers_misc._comment_body(M, comment):find(needle, 1, true) ~= nil then
      return true
    end
  end
  return false
end

function C.has_any_review_result_marker(M, comments, review_proposal_id, issue_proposal_id)
  if type(comments) ~= "table" then
    return false
  end
  local marker_pattern = "<!%-%- fkst:github%-devloop:review%-result:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      if marker:match('proposal="([^"]+)"') == tostring(review_proposal_id)
        and marker:match('issue_proposal="([^"]+)"') == tostring(issue_proposal_id) then
        return true
      end
    end
  end
  return false
end

local function has_versioned_marker(comments, marker)
  if type(comments) ~= "table" then
    return false
  end
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    if parsers_misc._comment_body(M, comment):find(marker, 1, true) ~= nil then
      return true
    end
  end
  return false
end

function C.has_implementing_marker(M, comments, proposal_id, dedup_key)
  if type(comments) ~= "table" then
    return false
  end
  local marker_pattern = "<!%-%- fkst:github%-devloop:implementing:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      if marker:match('proposal="([^"]+)"') == tostring(proposal_id)
        and marker:match('dedup="([^"]*)"') == tostring(dedup_key) then
        return true
      end
    end
  end
  return false
end

function C.implementing_fact(M, comments, proposal_id, dedup_key)
  if type(comments) ~= "table" then
    return nil
  end
  local marker_pattern = "<!%-%- fkst:github%-devloop:implementing:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      local marker_proposal = marker:match('proposal="([^"]+)"')
      local marker_dedup = marker:match('dedup="([^"]*)"')
      local marker_branch = marker:match('branch="([^"]+)"')
      local marker_head_sha = marker:match('head_sha="([^"]+)"')
      local marker_base_branch = marker:match('base_branch="([^"]+)"')
      local marker_base_sha = marker:match('base_sha="([^"]+)"')
      if marker_proposal == proposal_id
        and marker_dedup == tostring(dedup_key)
        and forge_validators.is_git_ref_safe(marker_branch)
        and forge_validators.is_git_sha(marker_head_sha)
        and forge_validators.is_git_ref_safe(marker_base_branch)
        and forge_validators.is_git_sha(marker_base_sha) then
        return {
          proposal_id = marker_proposal,
          dedup_key = marker_dedup,
          branch = marker_branch,
          head_sha = marker_head_sha,
          base_branch = marker_base_branch,
          base_sha = marker_base_sha,
        }
      end
    end
  end
  return nil
end

function C.pr_link_fact(M, comments, proposal_id)
  if type(comments) ~= "table" then
    return nil
  end
  local marker_pattern = "<!%-%- fkst:github%-devloop:pr%-link:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      local marker_proposal = marker:match('proposal="([^"]+)"')
      local marker_pr = marker:match('pr="([^"]+)"')
      local marker_branch = marker:match('branch="([^"]+)"')
      local marker_impl_version = marker:match('impl_version="([^"]*)"')
      local marker_base_branch = marker:match('base_branch="([^"]+)"')
      if marker_proposal == proposal_id
        and forge_validators.is_positive_pr_number(marker_pr)
        and forge_validators.is_git_ref_safe(marker_branch)
        and strings.is_bounded_string(marker_impl_version, M._max_dedup_len)
        and forge_validators.is_git_ref_safe(marker_base_branch) then
        return {
          proposal_id = marker_proposal,
          pr_number = tonumber(marker_pr),
          branch = marker_branch,
          impl_version = marker_impl_version,
          base_branch = marker_base_branch,
        }
      end
    end
  end
  return nil
end

function C.pr_delegation_fact(M, comments, proposal_id, version, delegation)
  if type(comments) ~= "table" then
    return nil
  end
  local marker_pattern = "<!%-%- fkst:github%-devloop:pr%-delegation:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      local marker_proposal = marker:match('proposal="([^"]+)"')
      local marker_pr_proposal = marker:match('pr_proposal="([^"]+)"')
      local marker_pr = marker:match('pr="([^"]+)"')
      local marker_version = marker:match('version="([^"]*)"')
      local marker_delegation = marker:match('delegation="([^"]*)"')
      local _, pr_number = entity_lib.parse_pr_proposal_id(marker_pr_proposal)
      if marker_proposal == tostring(proposal_id)
        and (version == nil or marker_version == tostring(version))
        and (delegation == nil or marker_delegation == tostring(delegation))
        and pr_number ~= nil
        and tostring(pr_number) == tostring(marker_pr)
        and forge_validators.is_positive_pr_number(marker_pr)
        and strings.is_bounded_string(marker_version, M._max_dedup_len)
        and strings.is_path_safe_key(marker_delegation, M._max_dedup_len) then
        return {
          proposal_id = marker_proposal,
          pr_proposal_id = marker_pr_proposal,
          pr_proposal = marker_pr_proposal,
          pr_number = tonumber(marker_pr),
          version = marker_version,
          delegation = marker_delegation,
          comment_created_at = parsers_misc._comment_created_at(M, comment),
        }
      end
    end
  end
  return nil
end

function C.pr_origin_fact(M, comments)
  if type(comments) ~= "table" then
    return nil
  end
  local marker_pattern = "<!%-%- fkst:github%-devloop:pr%-origin:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      local marker_proposal = marker:match('proposal="([^"]+)"')
      local marker_issue = marker:match('issue="([^"]+)"')
      local marker_branch = marker:match('branch="([^"]+)"')
      local marker_impl_version = marker:match('impl_version="([^"]*)"')
      local marker_base_branch = marker:match('base_branch="([^"]+)"')
      local repo, issue_number = base_ids.parse_proposal_id(marker_proposal)
      if repo ~= nil
        and marker_issue == issue_number
        and forge_validators.is_git_ref_safe(marker_branch)
        and strings.is_bounded_string(marker_impl_version, M._max_dedup_len)
        and forge_validators.is_git_ref_safe(marker_base_branch) then
        return {
          proposal_id = marker_proposal,
          repo = repo,
          issue_number = issue_number,
          branch = marker_branch,
          impl_version = marker_impl_version,
          base_branch = marker_base_branch,
        }
      end
      local pr_repo, pr_number = entity_lib.parse_pr_proposal_id(marker_proposal)
      if pr_repo ~= nil
        and marker_issue == tostring(pr_number)
        and forge_validators.is_git_ref_safe(marker_branch)
        and strings.is_bounded_string(marker_impl_version, M._max_dedup_len)
        and forge_validators.is_git_ref_safe(marker_base_branch) then
        return {
          proposal_id = marker_proposal,
          repo = pr_repo,
          issue_number = nil,
          pr_number = pr_number,
          branch = marker_branch,
          impl_version = marker_impl_version,
          base_branch = marker_base_branch,
          pr_native = true,
        }
      end
    end
  end
  return nil
end

function C.has_orphan_reaped_marker(M, comments, proposal_id, pr_number)
  if type(comments) ~= "table" then
    return false
  end
  local marker_pattern = "<!%-%- fkst:github%-devloop:orphan%-reaped:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      if marker:match('proposal="([^"]+)"') == tostring(proposal_id)
        and tostring(marker:match('pr="([^"]+)"')) == tostring(pr_number) then
        return true
      end
    end
  end
  return false
end

return C
