local parsers_misc = require("devloop.parsers.misc")
local devloop_base = require("devloop.base")
local shared = require("devloop.convergence.shared")
local transition_version = require("contract.transition_version")
local C = {}

local valid_round = shared.valid_round
local max_digest_len = shared.max_digest_len
local max_attr_len = shared.max_attr_len
local max_question_len = shared.max_question_len
local safe_attr = shared.safe_attr
local decode_attr = shared.decode_attr
local decode_angle_replay = shared.decode_angle_replay
local encode_angle_replay = shared.encode_angle_replay
local decode_findings_record = shared.decode_findings_record
local encode_findings_record = shared.encode_findings_record
local converge_question_digest = shared.converge_question_digest
local converge_verdicts_digest = shared.converge_verdicts_digest
local converge_angles_digest = shared.converge_angles_digest
local attr = shared.attr
local is_digest = shared.is_digest
local is_bounded_attr = shared.is_bounded_attr
local normalize_findings_record = shared.normalize_findings_record

local function converge_record_map(comments, kind, matches)
  local records_by_round = {}
  if type(comments) ~= "table" then
    return {}
  end

  local marker_pattern = "<!%-%- fkst:github%-devloop:" .. kind .. ":v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(comments)) do
    for marker in parsers_misc._comment_body(comment):gmatch(marker_pattern) do
      local round = valid_round(attr(marker, "round"))
      local question = attr(marker, "question")
      local verdicts = attr(marker, "verdicts")
      local dedup = attr(marker, "dedup")
      local narrowed_question = decode_attr(attr(marker, "narrowed_question"))
      local angle_digests = decode_angle_replay(attr(marker, "angle_digests"))
      local findings_record = decode_findings_record(attr(marker, "findings_record"))
      local essence_stall = attr(marker, "essence_stall") == "true"
      local version = attr(marker, "version")
      if round ~= nil
        and matches(marker)
        and is_digest(question)
        and is_digest(verdicts)
        and is_bounded_attr(nil, dedup, devloop_base._max_dedup_len) then
        records_by_round[round] = {
          round = round,
          question = question,
          verdicts = verdicts,
          dedup = dedup,
          version = version,
          narrowed_question = narrowed_question,
          angle_digests = angle_digests,
          findings_record = findings_record,
          essence_stall = essence_stall,
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
function C.append_converge_round_fact(facts, round, narrowed_question, angle_digests, dedup_key, findings_record, essence_stall)
  local copied = {}
  for _, fact in ipairs(facts or {}) do
    table.insert(copied, fact)
  end
  local normalized_findings = normalize_findings_record(findings_record)
  table.insert(copied, {
    round = round,
    question = converge_question_digest(narrowed_question),
    verdicts = converge_verdicts_digest(angle_digests),
    dedup = dedup_key,
    findings_record = normalized_findings,
    essence_stall = essence_stall == true,
  })
  return copied
end

function C.has_essence_stall(facts)
  for _, fact in ipairs(facts or {}) do
    if type(fact) == "table" and fact.essence_stall == true then
      return true
    end
  end
  return false
end

function C.continuation_budget_exhausted(facts)
  return C.max_converge_round(facts) >= 1
end

local terminal_causes = {
  ["external-evidence-required"] = true,
  ["no-semantic-progress"] = true,
  ["evidence-continuation-budget-exhausted"] = true,
}

function C.is_terminal_cause(value)
  return terminal_causes[tostring(value)] == true
end

function C.terminal_cause(facts, current_round)
  if C.has_essence_stall(facts) then
    return "external-evidence-required"
  end
  if C.is_true_stall(facts, current_round) then
    return "no-semantic-progress"
  end
  if C.continuation_budget_exhausted(facts) then
    return "evidence-continuation-budget-exhausted"
  end
  return nil
end

function C.converge_base_version(consensus_dedup)
  return transition_version.strip_trailing_loop(consensus_dedup)
end

function C.converge_proposal_base_dedup(consensus_dedup)
  local base_version = C.converge_base_version(consensus_dedup)
  return base_version:match("^consensus:(.+)$") or base_version
end
function C.converge_round_marker(proposal_id, base_version, source_ref_digest, round, consensus_dedup, narrowed_question, angle_digests, findings_record, essence_stall)
  local n = valid_round(round)
  if n == nil then
    error("github-devloop: invalid converge round")
  end
  return '<!-- fkst:github-devloop:converge-round:v1 proposal="' .. safe_attr(proposal_id, devloop_base._max_key_len)
    .. '" version="' .. safe_attr(base_version, devloop_base._max_dedup_len)
    .. '" source_ref="' .. safe_attr(source_ref_digest, max_digest_len)
    .. '" round="' .. tostring(n)
    .. '" dedup="' .. safe_attr(consensus_dedup, devloop_base._max_dedup_len)
    .. '" question="' .. converge_question_digest(narrowed_question)
    .. '" verdicts="' .. converge_verdicts_digest(angle_digests)
    .. '" angles="' .. converge_angles_digest(angle_digests)
    .. '" narrowed_question="' .. safe_attr(narrowed_question, max_question_len)
    .. '" angle_digests="' .. encode_angle_replay(angle_digests)
    .. '" findings_record="' .. encode_findings_record(findings_record)
    .. '" essence_stall="' .. (essence_stall == true and "true" or "false")
    .. '" -->'
end
function C.review_converge_round_marker(M, review_proposal_id, issue_proposal_id, issue_version, head_sha, source_ref_digest, round, consensus_dedup, narrowed_question, angle_digests, findings_record, essence_stall)
  local n = valid_round(round)
  if n == nil then
    error("github-devloop: invalid review converge round")
  end
  local heartbeat_version = M.liveness_heartbeat_version(issue_version, M.liveness_signal_producer_contract("review-converge-round"))
  return '<!-- fkst:github-devloop:review-converge-round:v1 proposal="' .. safe_attr(review_proposal_id, devloop_base._max_key_len)
    .. '" issue_proposal="' .. safe_attr(issue_proposal_id, devloop_base._max_key_len)
    .. '" version="' .. safe_attr(heartbeat_version, devloop_base._max_dedup_len)
    .. '" head_sha="' .. safe_attr(head_sha, max_attr_len)
    .. '" source_ref="' .. safe_attr(source_ref_digest, max_digest_len)
    .. '" round="' .. tostring(n)
    .. '" dedup="' .. safe_attr(consensus_dedup, devloop_base._max_dedup_len)
    .. '" question="' .. converge_question_digest(narrowed_question)
    .. '" verdicts="' .. converge_verdicts_digest(angle_digests)
    .. '" angles="' .. converge_angles_digest(angle_digests)
    .. '" narrowed_question="' .. safe_attr(narrowed_question, max_question_len)
    .. '" angle_digests="' .. encode_angle_replay(angle_digests)
    .. '" findings_record="' .. encode_findings_record(findings_record)
    .. '" essence_stall="' .. (essence_stall == true and "true" or "false")
    .. '" -->'
end

function C.converge_round_facts(comments, proposal_id, base_version, source_ref_digest)
  local matches = function(marker)
    return attr(marker, "proposal") == tostring(proposal_id)
      and attr(marker, "version") == tostring(base_version)
      and attr(marker, "source_ref") == tostring(source_ref_digest)
  end
  return converge_record_map(comments, "converge%-round", matches)
end

function C.converge_round_facts_for_source(comments, proposal_id, source_ref_digest)
  local matches = function(marker)
    return attr(marker, "proposal") == tostring(proposal_id)
      and attr(marker, "source_ref") == tostring(source_ref_digest)
  end
  return converge_record_map(comments, "converge%-round", matches)
end

function C.converge_round_facts_for_proposal(comments, proposal_id)
  local matches = function(marker)
    return attr(marker, "proposal") == tostring(proposal_id)
  end
  return converge_record_map(comments, "converge%-round", matches)
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
  return converge_record_map(comments, "review%-converge%-round", matches)
end

function C.converge_budget_round(comments, proposal_id)
  return C.max_converge_round(C.converge_round_facts_for_proposal(comments, proposal_id))
end

function C.max_converge_round(facts)
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

function C.has_converge_round_marker(comments, proposal_id, base_version, source_ref_digest, round)
  local n = valid_round(round)
  if n == nil then
    return false
  end
  for _, fact in ipairs(C.converge_round_facts(comments, proposal_id, base_version, source_ref_digest)) do
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

function C.is_true_stall(facts, current_round)
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

  return current.verdicts == previous.verdicts
    and previous.verdicts == before_previous.verdicts
end

return C
