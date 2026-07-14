local parsers_misc = require("devloop.parsers.misc")
local shared = require("devloop.convergence.shared")
local C = {}

local valid_round = shared.valid_round
local max_digest_len = shared.max_digest_len
local max_attr_len = shared.max_attr_len
local max_question_len = shared.max_question_len
local safe_attr = shared.safe_attr
local decode_attr = shared.decode_attr
local decode_angle_replay = shared.decode_angle_replay
local encode_angle_replay = shared.encode_angle_replay
local converge_question_digest = shared.converge_question_digest
local converge_verdicts_digest = shared.converge_verdicts_digest
local converge_angles_digest = shared.converge_angles_digest
local attr = shared.attr
local is_digest = shared.is_digest
local is_bounded_attr = shared.is_bounded_attr

local function converge_record_map(M, comments, kind, matches)
  local records_by_round = {}
  if type(comments) ~= "table" then
    return {}
  end

  local marker_pattern = "<!%-%- fkst:github%-devloop:" .. kind .. ":v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      local round = valid_round(attr(marker, "round"))
      local question = attr(marker, "question")
      local verdicts = attr(marker, "verdicts")
      local dedup = attr(marker, "dedup")
      local narrowed_question = decode_attr(attr(marker, "narrowed_question"))
      local angle_digests = decode_angle_replay(attr(marker, "angle_digests"))
      local version = attr(marker, "version")
      if round ~= nil
        and matches(marker)
        and is_digest(question)
        and is_digest(verdicts)
        and is_bounded_attr(M, dedup, M._max_dedup_len) then
        records_by_round[round] = {
          round = round,
          question = question,
          verdicts = verdicts,
          dedup = dedup,
          version = version,
          narrowed_question = narrowed_question,
          angle_digests = angle_digests,
        }
      end
    end
  end

  local facts = {}
  for _, record in pairs(records_by_round) do
    table.insert(facts, record)
  end
  table.sort(facts, function(a, b)
    return a.round < b.round
  end)
  return facts
end
function C.append_converge_round_fact(M, facts, round, narrowed_question, angle_digests, dedup_key)
  local copied = {}
  for _, fact in ipairs(facts or {}) do
    table.insert(copied, fact)
  end
  table.insert(copied, {
    round = round,
    question = converge_question_digest(narrowed_question),
    verdicts = converge_verdicts_digest(angle_digests),
    dedup = dedup_key,
  })
  return copied
end

function C.converge_base_version(M, consensus_dedup)
  return (tostring(consensus_dedup or ""):gsub("/loop/%d+$", ""))
end

function C.converge_proposal_base_dedup(M, consensus_dedup)
  local base_version = C.converge_base_version(M, consensus_dedup)
  return base_version:match("^consensus:(.+)$") or base_version
end
function C.converge_round_marker(M, proposal_id, base_version, source_ref_digest, round, consensus_dedup, narrowed_question, angle_digests)
  local n = valid_round(round)
  if n == nil then
    error("github-devloop: invalid converge round")
  end
  return '<!-- fkst:github-devloop:converge-round:v1 proposal="' .. safe_attr(proposal_id, M._max_key_len)
    .. '" version="' .. safe_attr(base_version, M._max_dedup_len)
    .. '" source_ref="' .. safe_attr(source_ref_digest, max_digest_len)
    .. '" round="' .. tostring(n)
    .. '" dedup="' .. safe_attr(consensus_dedup, M._max_dedup_len)
    .. '" question="' .. converge_question_digest(narrowed_question)
    .. '" verdicts="' .. converge_verdicts_digest(angle_digests)
    .. '" angles="' .. converge_angles_digest(angle_digests)
    .. '" narrowed_question="' .. safe_attr(narrowed_question, max_question_len)
    .. '" angle_digests="' .. encode_angle_replay(angle_digests)
    .. '" -->'
end
function C.review_converge_round_marker(M, review_proposal_id, issue_proposal_id, issue_version, head_sha, source_ref_digest, round, consensus_dedup, narrowed_question, angle_digests)
  local n = valid_round(round)
  if n == nil then
    error("github-devloop: invalid review converge round")
  end
  local heartbeat_version = M.liveness_heartbeat_version(issue_version, M.liveness_signal_producer_contract("review-converge-round"))
  return '<!-- fkst:github-devloop:review-converge-round:v1 proposal="' .. safe_attr(review_proposal_id, M._max_key_len)
    .. '" issue_proposal="' .. safe_attr(issue_proposal_id, M._max_key_len)
    .. '" version="' .. safe_attr(heartbeat_version, M._max_dedup_len)
    .. '" head_sha="' .. safe_attr(head_sha, max_attr_len)
    .. '" source_ref="' .. safe_attr(source_ref_digest, max_digest_len)
    .. '" round="' .. tostring(n)
    .. '" dedup="' .. safe_attr(consensus_dedup, M._max_dedup_len)
    .. '" question="' .. converge_question_digest(narrowed_question)
    .. '" verdicts="' .. converge_verdicts_digest(angle_digests)
    .. '" angles="' .. converge_angles_digest(angle_digests)
    .. '" narrowed_question="' .. safe_attr(narrowed_question, max_question_len)
    .. '" angle_digests="' .. encode_angle_replay(angle_digests)
    .. '" -->'
end

function C.converge_round_facts(M, comments, proposal_id, base_version, source_ref_digest)
  local matches = function(marker)
    return attr(marker, "proposal") == tostring(proposal_id)
      and attr(marker, "version") == tostring(base_version)
      and attr(marker, "source_ref") == tostring(source_ref_digest)
  end
  return converge_record_map(M, comments, "converge%-round", matches)
end

function C.converge_round_facts_for_source(M, comments, proposal_id, source_ref_digest)
  local matches = function(marker)
    return attr(marker, "proposal") == tostring(proposal_id)
      and attr(marker, "source_ref") == tostring(source_ref_digest)
  end
  return converge_record_map(M, comments, "converge%-round", matches)
end

function C.converge_round_facts_for_proposal(M, comments, proposal_id)
  local matches = function(marker)
    return attr(marker, "proposal") == tostring(proposal_id)
  end
  return converge_record_map(M, comments, "converge%-round", matches)
end

function C.converge_round_facts_for_proposal_boundary(M, comments, proposal_id, narrowed_question, angle_digests)
  local question = converge_question_digest(narrowed_question)
  local verdicts = converge_verdicts_digest(angle_digests)
  local matches = function(marker)
    return attr(marker, "proposal") == tostring(proposal_id)
      and attr(marker, "question") == question
      and attr(marker, "verdicts") == verdicts
  end
  return converge_record_map(M, comments, "converge%-round", matches)
end

function C.review_converge_round_facts(M, comments, review_proposal_id, issue_proposal_id, issue_version, head_sha, source_ref_digest)
  local heartbeat_version = M.liveness_heartbeat_version(issue_version, M.liveness_signal_producer_contract("review-converge-round"))
  local matches = function(marker)
    return attr(marker, "proposal") == tostring(review_proposal_id)
      and attr(marker, "issue_proposal") == tostring(issue_proposal_id)
      and attr(marker, "version") == tostring(heartbeat_version)
      and attr(marker, "head_sha") == tostring(head_sha)
      and attr(marker, "source_ref") == tostring(source_ref_digest)
  end
  return converge_record_map(M, comments, "review%-converge%-round", matches)
end

function C.converge_budget_round(M, comments, proposal_id)
  return C.max_converge_round(M, C.converge_round_facts_for_proposal(M, comments, proposal_id))
end

function C.converge_boundary_budget_round(M, comments, proposal_id, narrowed_question, angle_digests)
  return C.max_converge_round(M, C.converge_round_facts_for_proposal_boundary(M, comments, proposal_id, narrowed_question, angle_digests))
end

function C.review_converge_budget_round(M, comments, review_proposal_id, issue_proposal_id)
  local matches = function(marker)
    return attr(marker, "proposal") == tostring(review_proposal_id)
      and attr(marker, "issue_proposal") == tostring(issue_proposal_id)
  end
  return C.max_converge_round(M, converge_record_map(M, comments, "review%-converge%-round", matches))
end

function C.max_converge_round(M, facts)
  local max_seen = 0
  if type(facts) ~= "table" then
    return max_seen
  end
  for _, fact in ipairs(facts) do
    local round = valid_round(type(fact) == "table" and fact.round or nil)
    if round ~= nil and round > max_seen then
      max_seen = round
    end
  end
  return max_seen
end

function C.has_converge_round_marker(M, comments, proposal_id, base_version, source_ref_digest, round)
  local n = valid_round(round)
  if n == nil then
    return false
  end
  for _, fact in ipairs(C.converge_round_facts(M, comments, proposal_id, base_version, source_ref_digest)) do
    if fact.round == n then
      return true
    end
  end
  return false
end
function C.has_review_converge_round_marker(M, comments, review_proposal_id, issue_proposal_id, issue_version, head_sha, source_ref_digest, round)
  local n = valid_round(round)
  if n == nil then
    return false
  end
  for _, fact in ipairs(C.review_converge_round_facts(M, comments, review_proposal_id, issue_proposal_id, issue_version, head_sha, source_ref_digest)) do
    if fact.round == n then
      return true
    end
  end
  return false
end

function C.is_true_stall(M, facts, current_round)
  local round = valid_round(current_round)
  if round == nil or round < 3 or type(facts) ~= "table" then
    return false
  end

  local by_round = {}
  for _, fact in ipairs(facts) do
    if type(fact) == "table" then
      local fact_round = valid_round(fact.round)
      if fact_round ~= nil then
        by_round[fact_round] = fact
      end
    end
  end

  local current = by_round[round]
  local previous = by_round[round - 1]
  local before_previous = by_round[round - 2]
  if current == nil or previous == nil or before_previous == nil then
    return false
  end

return current.question == previous.question
    and previous.question == before_previous.question
    and current.verdicts == previous.verdicts
    and previous.verdicts == before_previous.verdicts
end

return C
