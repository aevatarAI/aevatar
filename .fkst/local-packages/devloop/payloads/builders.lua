local entity_lib = require("devloop.entity")
local devloop_base = require("devloop.base")
local base_ids = require("devloop.base_ids")
local strings = require("contract.strings")
local parsers_misc = require("devloop.parsers.misc")
local m_facts = require("devloop.markers.facts")
local C = {}
local forge_validators = require("devloop.forge_validators")
local shared = require("devloop.payloads.shared")
local board = require("devloop.payloads.board")

local function commit_subject_title(M, current)
  if type(current) ~= "table" then
    return nil
  end
  local title = tostring(current.title or "")
    :gsub("%c", " ")
    :gsub("%s+", " ")
    :gsub("^%s+", "")
    :gsub("%s+$", "")
  if title == "" then
    return nil
  end
  title = devloop_base._neutralize_fkst_markers(title)
  return title
end

local function bounded_commit_subject(M, prefix, issue_number, current)
  local subject = tostring(prefix) .. " refs #" .. tostring(issue_number)
  local title = commit_subject_title(M, current)
  if title ~= nil then
    local title_prefix = subject .. ": "
    local room = 200 - #title_prefix
    if room > 0 then
      if #title > room then
        title = base_ids.truncate_utf8(title, room)
      end
      if title ~= "" then
        subject = title_prefix .. title
      end
    end
  end
  return subject
end

function C.build_devloop_ready_payload(M, source)
  local ready_version = base_ids.dedup_key({
    "ready",
    tostring(source.dedup_key),
  })
  local marker_version = tostring(source.effect_version or source.dedup_key)
  local payload = {
    schema = "github-devloop.ready.v1",
    proposal_id = source.proposal_id,
    dedup_key = ready_version,
    source_ref = base_ids.normalize_source_ref(source.source_ref),
  }
  if source.include_ready_hand_off == true and source.ready_comment_id ~= nil then
    payload.ready_hand_off = {
      kind = "own-state-marker",
      proposal_id = source.proposal_id,
      state = "ready",
      marker_version = marker_version,
      event_version = ready_version,
      stage_rank = M.stage_rank("ready"),
      effects = "result-marker,ready-label,devloop-ready",
      comment_id = source.ready_comment_id,
    }
  end
  local framing = shared.bounded_framing(M, source.framing)
  if framing ~= nil then
    payload.framing = framing
  end
  local attempt = tonumber(source.impl_retry_attempt)
  if attempt ~= nil then
    if attempt < 1 or attempt ~= math.floor(attempt) or attempt > M._max_impl_retry_attempts then
      error("github-devloop: invalid implementation retry attempt")
    end
    payload.impl_retry_attempt = attempt
  end
  if source.operator_reentry ~= nil then
    payload.operator_reentry = source.operator_reentry
  end
  return payload
end

function C.build_devloop_reviewing_payload(M, origin, pr_number, source_ref, version)
  local review_version = version or origin.impl_version
  local payload = {
    schema = "github-devloop.reviewing.v1",
    proposal_id = origin.proposal_id,
    pr_number = pr_number,
    version = review_version,
    dedup_key = base_ids.dedup_key({
      "reviewing",
      tostring(origin.proposal_id),
      tostring(review_version),
      tostring(pr_number),
    }),
    source_ref = base_ids.normalize_source_ref(source_ref),
  }
  if origin.reviewing_comment_id ~= nil then
    payload.reviewing_hand_off = {
      kind = "own-state-marker",
      proposal_id = origin.proposal_id,
      state = "reviewing",
      marker_version = review_version,
      event_version = review_version,
      stage_rank = M.stage_rank("reviewing"),
      comment_id = origin.reviewing_comment_id,
    }
  end
  return payload
end

function C.build_current_head_reviewing_payload(M, origin, pr_number, current_pr, state, source_ref)
  local review_proposal_id = devloop_base.pr_review_proposal_id(origin.repo, pr_number, state.version, current_pr.head_sha)
  if m_facts.has_any_review_result_marker(M, current_pr.comments, review_proposal_id, origin.proposal_id) then
    return nil
  end
  return C.build_devloop_reviewing_payload(M, {
    proposal_id = origin.proposal_id,
    impl_version = state.version,
  }, pr_number, source_ref, state.version)
end

function C.build_devloop_fixing_payload(M, origin, pr_number, review_fact, source_ref)
  local version = origin.impl_version
  if review_fact.fix_version ~= nil then
    version = review_fact.fix_version
  end
  local payload = {
    schema = "github-devloop.fixing.v1",
    proposal_id = origin.proposal_id,
    pr_number = pr_number,
    version = version,
    review_proposal_id = review_fact.review_proposal_id,
    review_dedup_key = review_fact.review_dedup_key,
    reviewed_head_sha = review_fact.reviewed_head_sha,
    dedup_key = base_ids.dedup_key({
      "fixing",
      tostring(origin.proposal_id),
      tostring(version),
      tostring(pr_number),
      tostring(review_fact.review_dedup_key),
    }),
    source_ref = base_ids.normalize_source_ref(source_ref),
  }
  local framing = shared.bounded_framing(M, review_fact.framing or origin.framing)
  if framing ~= nil then
    payload.framing = framing
  end
  local blocking_gap = shared.bounded_control_text(M, review_fact.blocking_gap, M._max_blocking_gap_len)
  if blocking_gap ~= nil then
    payload.blocking_gap = blocking_gap
  end
  if review_fact.gate_baseline_sha ~= nil then
    if not forge_validators.is_git_sha(review_fact.gate_baseline_sha) then
      error("github-devloop: invalid gate baseline sha")
    end
    payload.gate_baseline_sha = tostring(review_fact.gate_baseline_sha)
  end
  if review_fact.predecessor_set ~= nil then
    if not strings.is_path_safe_key(review_fact.predecessor_set, M._max_dedup_len) then
      error("github-devloop: invalid predecessor set")
    end
    payload.predecessor_set = tostring(review_fact.predecessor_set)
  end
  local gate_failure_excerpt = shared.bounded_control_text(M, review_fact.gate_failure_excerpt, parsers_misc.max_rollup_failure_summary_len)
  if gate_failure_excerpt ~= nil then
    payload.gate_failure_excerpt = gate_failure_excerpt
  end
  return payload
end

local function replay_fact_sha(value, fallback)
  if value ~= nil then
    if not forge_validators.is_git_sha(value) then
      error("github-devloop: invalid replay fact sha")
    end
    return tostring(value)
  end
  return fallback
end

function C.build_replayed_fixing_payload(M, origin, pr_number, feedback, source_ref)
  local payload = C.build_devloop_fixing_payload(M, origin, pr_number, {
    review_proposal_id = feedback.review_proposal_id,
    review_dedup_key = feedback.review_dedup_key,
    reviewed_head_sha = feedback.reviewed_head_sha,
    blocking_gap = feedback.blocking_gap,
    gate_baseline_sha = feedback.gate_baseline_sha,
    predecessor_set = feedback.predecessor_set,
    gate_failure_excerpt = feedback.review_reason,
  }, source_ref)
  payload.dedup_key = base_ids.dedup_key({
    "fixing",
    "replay",
    tostring(origin.proposal_id),
    tostring(payload.version),
    tostring(pr_number),
    tostring(feedback.review_dedup_key),
    replay_fact_sha(feedback.gate_baseline_sha, "nobase"),
    tostring(feedback.predecessor_set or "nopred"),
    replay_fact_sha(feedback.reviewed_head_sha, "nohead"),
  })
  return payload
end

function C.build_devloop_review_meta_payload(M, unresolved, issue_proposal_id, issue_version, pr_number, n, source_ref)
  return {
    schema = "github-devloop.review-meta.v1",
    proposal_id = issue_proposal_id,
    review_proposal_id = unresolved.proposal_id,
    review_dedup_key = unresolved.dedup_key,
    version = issue_version,
    pr_number = pr_number,
    n = n,
    dedup_key = base_ids.dedup_key({
      "review-meta",
      tostring(issue_proposal_id),
      tostring(issue_version),
      tostring(pr_number),
      tostring(n),
      tostring(unresolved.dedup_key),
    }),
    source_ref = base_ids.normalize_source_ref(source_ref or unresolved.source_ref),
  }
end

function C.fix_reflection_dedup_key(M, issue_proposal_id, issue_version, pr_number, fix_round, review_dedup_key)
  return base_ids.dedup_key({
    "fix-reflection",
    tostring(issue_proposal_id),
    tostring(issue_version),
    tostring(pr_number),
    tostring(fix_round),
    tostring(review_dedup_key),
  })
end

function C.build_devloop_fix_reflection_payload(M, unresolved, issue_proposal_id, issue_version, pr_number, fix_round, source_ref)
  local review_dedup_key = unresolved.review_dedup_key or unresolved.dedup_key
  local payload = C.build_devloop_review_meta_payload(M, {
    proposal_id = unresolved.proposal_id,
    dedup_key = review_dedup_key,
    source_ref = unresolved.source_ref,
  }, issue_proposal_id, issue_version, pr_number, fix_round, source_ref)
  payload.mode = "fix-reflection"
  payload.fix_round = fix_round
  payload.dedup_key = C.fix_reflection_dedup_key(M, issue_proposal_id, issue_version, pr_number, fix_round, review_dedup_key)
  return payload
end

function C.build_devloop_merge_ready_payload(M, issue_proposal_id, pr_number, version, review_fact, source_ref)
  local current_head_sha = review_fact and review_fact.current_head_sha
  if current_head_sha == nil then
    current_head_sha = review_fact and review_fact.reviewed_head_sha
  end
  return {
    schema = "github-devloop.merge-ready.v1",
    proposal_id = issue_proposal_id,
    pr_number = pr_number,
    version = version,
    review_proposal_id = review_fact and review_fact.review_proposal_id,
    review_dedup_key = review_fact and review_fact.review_dedup_key,
    reviewed_head_sha = review_fact and review_fact.reviewed_head_sha,
    dedup_key = base_ids.dedup_key({
      "merge-ready",
      tostring(issue_proposal_id),
      tostring(version),
      tostring(pr_number),
      tostring(review_fact and review_fact.review_dedup_key or "review"),
      tostring(current_head_sha or "nohead"),
    }),
    source_ref = base_ids.normalize_source_ref(source_ref),
  }
end

function C.build_devloop_decompose_payload(M, fix_reconcile)
  return {
    schema = "github-devloop.decompose.v1",
    proposal_id = fix_reconcile.proposal_id,
    pr_number = fix_reconcile.pr_number,
    version = fix_reconcile.issue_version,
    review_proposal_id = fix_reconcile.review_proposal_id,
    review_dedup_key = fix_reconcile.review_dedup_key,
    head_sha = fix_reconcile.head_sha,
    round = fix_reconcile.round,
    dedup_key = base_ids.dedup_key({
      "decompose",
      tostring(fix_reconcile.proposal_id),
      tostring(fix_reconcile.issue_version),
    }),
    source_ref = base_ids.normalize_source_ref(fix_reconcile.source_ref),
  }
end

function C.build_devloop_intake_candidate_payload(M, repo, issue_number, updated_at, options)
  local opts = options or {}
  local proposal_id = base_ids.proposal_id(repo, issue_number)
  local source_ref = {
    kind = "external",
    ref = tostring(repo) .. "#issue/" .. tostring(issue_number),
  }
  local effect_id = opts.effect_id or M.intake_dedup_key(proposal_id, updated_at)
  local dedup_key = opts.dedup_key
    or (opts.effect_id ~= nil and M.intake_candidate_delivery_dedup_key(proposal_id, effect_id, opts.delivery_version))
    or effect_id
  return {
    schema = "github-devloop.intake-candidate.v1",
    repo = repo,
    issue_number = issue_number,
    proposal_id = proposal_id,
    dedup_key = dedup_key,
    effect_id = effect_id,
    reintake_command_created_at = opts.reintake_command_created_at,
    source_ref = source_ref,
  }
end

function C.build_proposal(M, issue)
  local proposal_id = base_ids.proposal_id(issue.repo, issue.number)
  local title = tostring(issue.title or "")
  if #title > M._max_title_len then
    title = base_ids.truncate_utf8(title, M._max_title_len)
  end
  local body = "Judge the current GitHub issue from the full source content."
    .. "\nIssue: " .. tostring(issue.repo) .. "#" .. tostring(issue.number)
    .. "\nRecurrence: read recent closed issues in context; if this is the third same-class instance, reframe to a class solution or give an explicit waiver."

  return {
    schema = "consensus.proposal.v1",
    verdict_mode = "converge",
    proposal_id = proposal_id,
    title = title,
    body = body,
    content_fetch = issue.content_fetch,
    dedup_key = devloop_base.proposal_dedup_key(proposal_id, issue.updated_at),
    source_ref = base_ids.normalize_source_ref(issue.source_ref),
  }
end

function C.build_board_proposal(M, issue, tick)
  return board.append_board_digest_to_proposal(M, C.build_proposal(M, issue), issue.repo, tick)
end

-- Thread the meta-judge's narrowing onto a re-raised next-round proposal so the next
-- angles converge instead of blindly re-judging the same question. The next round sees
-- ONLY the bounded convergence_question + prior-round digests (verdict + short reply),
-- never prior peer full text, preserving angle peer-invisibility. The `/loop/N` dedup
-- shape stays unchanged so the existing round parsing + budget endpoint still work.
local function apply_converge_fields(proposal, n, converge)
  proposal.round = n
  if type(converge) ~= "table" then
    return proposal
  end
  if converge.narrowed_question ~= nil and converge.narrowed_question ~= "" then
    proposal.convergence_question = converge.narrowed_question
  end
  if type(converge.angle_digests) == "table" then
    proposal.prior_round_digests = converge.angle_digests
  end
  return proposal
end

function C.build_loop_proposal(M, repo, issue_number, current, source_ref, n, converge, content_fetch, dedup_key)
  local issue = {
    repo = repo,
    number = issue_number,
    title = current.title,
    updated_at = current.updated_at,
    source_ref = source_ref,
    content_fetch = content_fetch,
  }
  local proposal = C.build_proposal(M, issue)
  proposal.dedup_key = dedup_key or (proposal.dedup_key .. "/loop/" .. tostring(n))
  return apply_converge_fields(proposal, n, converge)
end

function C.build_board_loop_proposal(M, repo, issue_number, current, source_ref, n, converge, tick, content_fetch, dedup_key)
  return board.append_board_digest_to_proposal(M, C.build_loop_proposal(M, repo, issue_number, current, source_ref, n, converge, content_fetch, dedup_key), repo, tick)
end

local function apply_high_risk_angles(proposal, high_risk)
  if high_risk == true then
    proposal.angles = { "minimal", "structural", "delete", "high-risk" }
  end
  return proposal
end

function C.build_pr_review_proposal(M, repo, issue_number, pr_number, version, head_sha, current_issue, source_ref, pr_comments, content_fetch, high_risk)
  local review_id = devloop_base.pr_review_proposal_id(repo, pr_number, version, head_sha)
  local title = "Review PR #" .. tostring(pr_number)
  if issue_number ~= nil then
    title = title .. " for issue #" .. tostring(issue_number)
  end
  if type(current_issue) == "table" and tostring(current_issue.title or "") ~= "" then
    title = "Review PR #" .. tostring(pr_number) .. ": " .. tostring(current_issue.title)
  end
  if #title > M._max_title_len then
    title = base_ids.truncate_utf8(title, M._max_title_len)
  end

  local issue_title = type(current_issue) == "table" and tostring(current_issue.title or "") or ""
  if #issue_title > M._max_title_len then
    issue_title = base_ids.truncate_utf8(issue_title, M._max_title_len)
  end
  issue_title = devloop_base.neutralize_untrusted_prompt_text(devloop_base._neutralize_fkst_markers(issue_title))
  local body = "Review the PR diff and decide whether it should advance to merge-ready."
    .. "\nEntity proposal: " .. tostring(issue_number ~= nil and base_ids.proposal_id(repo, issue_number) or entity_lib.pr_proposal_id(repo, pr_number))
    .. "\nReviewed PR head: " .. tostring(head_sha)
    .. "\nIssue title: " .. issue_title
    .. "\n" .. M.short_review_observation_boundary_clause()
    .. "\nReview contract: reject only for a stated issue requirement the diff fails; beyond stated bounds is advisory/spec-amendment."
    .. "\nRead the local context bundle before judging."
  local issue_proposal_id = tostring(issue_number ~= nil and base_ids.proposal_id(repo, issue_number) or entity_lib.pr_proposal_id(repo, pr_number))
  local ledger = m_facts.review_prior_round_ledger(M, pr_comments, issue_proposal_id, version)
  if ledger ~= nil and ledger ~= "" then
    body = body
      .. "\nPrior review ledger:\n"
      .. ledger
      .. "\nJudge whether THE NAMED GAP is closed; new objections only for fix regressions inside the issue's stated bounds. For rollup-red or failing-check re-review, scope the question to the diff change and the named failing check, not to restoration of gate state."
  end
  if #body > M._max_body_len then
    error("github-devloop: PR review proposal exceeds bounded body")
  end

  return apply_high_risk_angles({
    schema = "consensus.proposal.v1",
    verdict_mode = "gate",
    proposal_id = review_id,
    title = devloop_base.neutralize_untrusted_prompt_text(title),
    body = body,
    content_fetch = content_fetch,
    dedup_key = base_ids.dedup_key({
      review_id,
      "review",
    }),
    source_ref = base_ids.normalize_source_ref(source_ref),
  }, high_risk)
end

function C.build_board_pr_review_proposal(M, repo, issue_number, pr_number, version, head_sha, current_issue, source_ref, tick, pr_comments, content_fetch, high_risk)
  return board.append_board_digest_to_proposal(M, C.build_pr_review_proposal(M, repo, issue_number, pr_number, version, head_sha, current_issue, source_ref, pr_comments, content_fetch, high_risk), repo, tick)
end

function C.build_pr_review_loop_proposal(M, repo, issue_number, pr_number, version, head_sha, current_issue, source_ref, n, converge, pr_comments, content_fetch, high_risk, dedup_key)
  local proposal = C.build_pr_review_proposal(M, repo, issue_number, pr_number, version, head_sha, current_issue, source_ref, pr_comments, content_fetch, high_risk)
  proposal.dedup_key = dedup_key or (proposal.dedup_key .. "/loop/" .. tostring(n))
  return apply_converge_fields(proposal, n, converge)
end

function C.build_board_pr_review_loop_proposal(M, repo, issue_number, pr_number, version, head_sha, current_issue, source_ref, n, converge, tick, pr_comments, content_fetch, high_risk, dedup_key)
  return board.append_board_digest_to_proposal(M, C.build_pr_review_loop_proposal(M, repo, issue_number, pr_number, version, head_sha, current_issue, source_ref, n, converge, pr_comments, content_fetch, high_risk, dedup_key), repo, tick)
end

function C.implement_commit_subject(M, issue_number, current)
  return bounded_commit_subject(M, "auto-implement", issue_number, current)
end

function C.fix_commit_subject(M, issue_number, current)
  return bounded_commit_subject(M, "auto-fix", issue_number, current)
end
return C
